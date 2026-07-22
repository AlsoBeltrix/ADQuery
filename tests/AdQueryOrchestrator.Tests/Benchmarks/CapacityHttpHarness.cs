using System.Security.Claims;
using System.Text.Encodings.Web;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Tests.Benchmarks;

/// <summary>
/// Self-hosts the real application through <see cref="WebApplicationFactory{Program}"/>
/// with the external dependencies faked so the current <c>/api/query/csv-enrich</c>
/// path can be driven end to end for capacity measurement, with no live provider,
/// no Active Directory, and no writes to the production output root:
/// <list type="bullet">
/// <item>the provider (<see cref="IClaudeService"/>) returns a fixed plan;</item>
/// <item>the directory (<see cref="IActiveDirectoryService"/>) returns deterministic
/// synthetic records keyed off the requested match value;</item>
/// <item>the result writer targets an isolated temp directory;</item>
/// <item>authentication is replaced by a stub that always grants the required role.</item>
/// </list>
/// The success path derives its log path from the injected writer's output path, so
/// nothing touches <c>E:\WWWOutput</c>.
/// </summary>
internal sealed class CapacityHttpHarness : WebApplicationFactory<Program>
{
    private readonly string _outputRoot;
    private readonly int _retrievedValueCodeUnits;
    private readonly int _notFoundEvery;

    public CapacityHttpHarness(
        string outputRoot,
        IReadOnlyList<string> retrieveAttributes,
        int retrievedValueCodeUnits = 32,
        int notFoundEvery = 0)
    {
        _outputRoot = outputRoot;
        RetrieveAttributes = retrieveAttributes;
        _retrievedValueCodeUnits = retrievedValueCodeUnits;
        _notFoundEvery = notFoundEvery;
    }

    public IReadOnlyList<string> RetrieveAttributes { get; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Claude:ApiKey", "benchmark-not-a-real-key");

        builder.ConfigureServices(services =>
        {
            // Drop the background job executor: it is irrelevant to enrichment measurement.
            services.RemoveAll<IHostedService>();

            Replace<IClaudeService>(services, new StubPlanProvider(RetrieveAttributes));
            Replace<IActiveDirectoryService>(
                services,
                new SyntheticDirectory(RetrieveAttributes, _retrievedValueCodeUnits, _notFoundEvery));
            Replace<ICsvEnrichmentResultWriter>(services, new TempResultWriter(_outputRoot));
            Replace<ICsvEnrichmentResultIdGenerator>(services, new FixedIdGenerator());

            services.AddAuthentication(StubAuthHandler.SchemeName)
                .AddScheme<AuthenticationSchemeOptions, StubAuthHandler>(StubAuthHandler.SchemeName, _ => { });

            // The stub is the default; Negotiate must never resolve or run under TestServer.
            services.PostConfigure<AuthenticationOptions>(options =>
            {
                options.DefaultScheme = StubAuthHandler.SchemeName;
                options.DefaultAuthenticateScheme = StubAuthHandler.SchemeName;
                options.DefaultChallengeScheme = StubAuthHandler.SchemeName;
            });

            // Negotiate registers an IAuthenticationRequestHandler that throws under
            // TestServer (no Kestrel IConnectionItemsFeature). Remove the scheme from the
            // provider at startup so the request-handler pass never invokes it.
            services.AddTransient<IStartupFilter, RemoveNegotiateSchemeStartupFilter>();
        });
    }

    private sealed class RemoveNegotiateSchemeStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                var provider = app.ApplicationServices.GetRequiredService<IAuthenticationSchemeProvider>();
                provider.RemoveScheme(NegotiateDefaults.AuthenticationScheme);
                next(app);
            };
        }
    }

    private static void Replace<TService>(IServiceCollection services, TService instance)
        where TService : class
    {
        services.RemoveAll<TService>();
        services.AddSingleton(instance);
    }

    private sealed class StubPlanProvider : IClaudeService
    {
        private readonly CsvEnrichmentPlan _plan;

        public StubPlanProvider(IReadOnlyList<string> retrieveAttributes)
        {
            _plan = new CsvEnrichmentPlan
            {
                MatchColumn = "Employee",
                MatchAttribute = "sAMAccountName",
                RetrieveAttributes = retrieveAttributes.ToList(),
                OutputMode = "all",
                Description = "capacity benchmark",
            };
        }

        public Task<CsvEnrichmentPlanResponse> GenerateCsvEnrichmentPlanAsync(
            string userQuery,
            List<string> csvHeaders,
            int rowCount,
            CancellationToken cancellationToken = default,
            Dictionary<string, string>? columnPatterns = null)
            => Task.FromResult(new CsvEnrichmentPlanResponse
            {
                Success = true,
                Plan = _plan,
                RawResponse = "{}",
                TokenUsage = new TokenUsage { InputTokens = 0, OutputTokens = 0 },
            });

        public Task<ClaudeResponse> GenerateExecutionPlanAsync(
            string userQuery,
            string? context = null,
            int? requestedResultLimit = null,
            CancellationToken cancellationToken = default,
            string? modelOverride = null)
            => throw new NotSupportedException("Directory plan generation is not used by the capacity harness.");

        public Task<ClaudeHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ClaudeHealthResult { IsHealthy = true });
    }

    private sealed class SyntheticDirectory : IActiveDirectoryService
    {
        private readonly IReadOnlyList<string> _retrieveAttributes;
        private readonly string _value;
        private readonly int _notFoundEvery;

        public SyntheticDirectory(IReadOnlyList<string> retrieveAttributes, int valueCodeUnits, int notFoundEvery)
        {
            _retrieveAttributes = retrieveAttributes;
            _value = valueCodeUnits <= 0 ? string.Empty : new string('x', valueCodeUnits);
            _notFoundEvery = notFoundEvery;
        }

        public Task<IReadOnlyList<DirectoryRecord>> SearchAsync(
            DirectorySearchRequest request,
            CancellationToken cancellationToken = default)
        {
            var matchValue = request.Filters.FirstOrDefault()?.Value ?? string.Empty;
            if (ShouldMiss(matchValue))
            {
                return Task.FromResult<IReadOnlyList<DirectoryRecord>>(Array.Empty<DirectoryRecord>());
            }

            var record = new DirectoryRecord
            {
                ObjectType = DirectoryObjectType.User,
                DistinguishedName = $"CN={matchValue},OU=Benchmark,DC=example,DC=invalid",
            };
            foreach (var attribute in _retrieveAttributes)
            {
                record[attribute] = _value;
            }

            return Task.FromResult<IReadOnlyList<DirectoryRecord>>(new[] { record });
        }

        private bool ShouldMiss(string matchValue)
        {
            if (_notFoundEvery <= 0)
            {
                return false;
            }

            // Deterministic miss based on the trailing digits of the synthetic identifier.
            var digits = new string(matchValue.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var n) && n % _notFoundEvery == 0;
        }

        public Task<IReadOnlyList<DirectoryRecord>> ExpandGroupMembersAsync(
            IEnumerable<string> groupDistinguishedNames,
            bool recursive,
            IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DirectoryRecord>>(Array.Empty<DirectoryRecord>());

        public Task<IReadOnlyList<DirectoryRecord>> LookupAsync(
            IEnumerable<string> distinguishedNames,
            DirectoryObjectType targetType,
            IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DirectoryRecord>>(Array.Empty<DirectoryRecord>());

        public Task<IReadOnlyList<DirectoryRecord>> GetDirectReportsBatch(
            IEnumerable<string> managerDistinguishedNames,
            IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DirectoryRecord>>(Array.Empty<DirectoryRecord>());
    }

    private sealed class TempResultWriter : ICsvEnrichmentResultWriter
    {
        private readonly string _root;
        private int _counter;

        public TempResultWriter(string root)
        {
            _root = root;
            Directory.CreateDirectory(_root);
        }

        public string Write(string? ownerName, DateTime timestampUtc, byte[] content)
        {
            var path = Path.Combine(_root, $"enrich_{Interlocked.Increment(ref _counter)}.csv");
            File.WriteAllBytes(path, content);
            return path;
        }
    }

    private sealed class FixedIdGenerator : ICsvEnrichmentResultIdGenerator
    {
        private int _counter;

        public string CreateId() => $"bench-{Interlocked.Increment(ref _counter)}";
    }

    private sealed class StubAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "CapacityBenchmarkStub";

        public StubAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var claims = new[]
            {
                new Claim(ClaimTypes.Name, "ANALOG\\benchuser"),
                new Claim(ClaimTypes.Role, "ANALOG\\ADEXNLQ_Users"),
            };
            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
