using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using AdQuery.Orchestrator.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class AuthorizationPolicyTests
{
    private const string ApprovedIdentity = "ANALOG\\approved-user";
    private const string RefusedIdentity = "ANALOG\\refused-user";

    [Fact]
    public async Task UserInfo_ApprovedRoleReturnsAuthenticatedIdentity()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory, ApprovedIdentity);

        using var response = await client.GetAsync(
            "/api/user/info",
            TestContext.Current.CancellationToken);

        var content = await AssertStatusAsync(response, HttpStatusCode.OK);
        var body = JsonSerializer.Deserialize<UserInfoResponse>(
            content,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(body);
        Assert.Equal(ApprovedIdentity, body.Username);
        Assert.True(body.IsAuthenticated);
    }

    [Fact]
    public async Task UserInfo_AuthenticatedUserWithoutRoleReturnsForbidden()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory, RefusedIdentity);

        using var response = await client.GetAsync(
            "/api/user/info",
            TestContext.Current.CancellationToken);

        _ = await AssertStatusAsync(response, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UserInfo_AnonymousUserReturnsUnauthorized()
    {
        using var factory = CreateFactory();
        using var client = CreateClient(factory);

        using var response = await client.GetAsync(
            "/api/user/info",
            TestContext.Current.CancellationToken);

        _ = await AssertStatusAsync(response, HttpStatusCode.Unauthorized);
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IConfigureOptions<AuthenticationOptions>>();
                    services.AddAuthentication(options =>
                        {
                            options.DefaultAuthenticateScheme = HeaderAuthenticationHandler.SchemeName;
                            options.DefaultChallengeScheme = HeaderAuthenticationHandler.SchemeName;
                            options.DefaultForbidScheme = HeaderAuthenticationHandler.SchemeName;
                        })
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthenticationHandler>(
                            HeaderAuthenticationHandler.SchemeName,
                            _ => { });
                });
            });
    }

    private static HttpClient CreateClient(
        WebApplicationFactory<Program> factory,
        string? identity = null)
    {
        var client = factory.CreateClient(
            new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false,
                BaseAddress = new Uri("https://localhost")
            });
        if (identity is not null)
        {
            client.DefaultRequestHeaders.Add(HeaderAuthenticationHandler.IdentityHeader, identity);
        }

        return client;
    }

    private static async Task<string> AssertStatusAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus)
    {
        var content = await response.Content.ReadAsStringAsync(
            TestContext.Current.CancellationToken);
        Assert.True(
            response.StatusCode == expectedStatus,
            $"Expected HTTP {(int)expectedStatus}; received {(int)response.StatusCode}. Body: {content}");
        return content;
    }

    private sealed class HeaderAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public const string SchemeName = "TestHeader";
        public const string IdentityHeader = "X-Test-Identity";

        public HeaderAuthenticationHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder)
            : base(options, logger, encoder)
        {
        }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.TryGetValue(IdentityHeader, out var values))
            {
                return Task.FromResult(AuthenticateResult.NoResult());
            }

            var identityName = values.ToString();
            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, identityName)
            };
            if (string.Equals(identityName, ApprovedIdentity, StringComparison.Ordinal))
            {
                claims.Add(new Claim(ClaimTypes.Role, "ANALOG\\ADEXNLQ_Users"));
            }

            var identity = new ClaimsIdentity(claims, SchemeName);
            var principal = new ClaimsPrincipal(identity);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        protected override Task HandleChallengeAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status401Unauthorized;
            Response.Headers[HeaderNames.WWWAuthenticate] = SchemeName;
            return Task.CompletedTask;
        }

        protected override Task HandleForbiddenAsync(AuthenticationProperties properties)
        {
            Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        }
    }
}
