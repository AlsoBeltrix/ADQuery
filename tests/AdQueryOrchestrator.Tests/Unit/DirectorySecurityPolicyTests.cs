using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class DirectorySecurityPolicyTests
{
    [Fact]
    public async Task ConfiguredFile_ReplacesDefaultsAndDrivesNormalPlanValidation()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllLines(
            Path.Combine(tempDirectory.Path, "user-attributes.txt"),
            [
                "# custom user attributes",
                "",
                "  employeeID  ",
                "EMPLOYEEid",
                "customAttribute"
            ]);
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Security:AttributeFiles:User"] = "user-attributes.txt"
        });
        var policy = CreatePolicy(configuration, tempDirectory.Path);

        Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.User, "EMPLOYEEID"));
        Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.User, "CUSTOMattribute"));
        Assert.False(policy.IsAttributeAllowed(DirectoryObjectType.User, "mobile"));

        var validator = CreateValidator(configuration, policy);
        var accepted = await validator.ValidateSecurityAsync(
            CreatePlan("employeeID", "contains"));
        var rejected = await validator.ValidateSecurityAsync(
            CreatePlan("mobile", "contains"));

        Assert.True(accepted.OperationsValid, string.Join(Environment.NewLine, accepted.SecurityErrors));
        Assert.False(rejected.OperationsValid);
        Assert.Contains(rejected.SecurityErrors, error => error.Contains("'mobile'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task MissingConfiguredFile_FallsBackToDefaults()
    {
        using var tempDirectory = new TemporaryDirectory();
        var configuration = CreateConfiguration(new Dictionary<string, string?>
        {
            ["Security:AttributeFiles:User"] = "missing-user-attributes.txt"
        });
        var policy = CreatePolicy(configuration, tempDirectory.Path);

        Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.User, "DISPLAYNAME"));
        Assert.False(policy.IsAttributeAllowed(DirectoryObjectType.User, "employeeID"));

        var validator = CreateValidator(configuration, policy);
        var accepted = await validator.ValidateSecurityAsync(
            CreatePlan("displayName", "equals"));
        var rejected = await validator.ValidateSecurityAsync(
            CreatePlan("employeeID", "equals"));

        Assert.True(accepted.OperationsValid, string.Join(Environment.NewLine, accepted.SecurityErrors));
        Assert.False(rejected.OperationsValid);
    }

    [Fact]
    public void EmptyConfiguredFile_FallsBackForEveryObjectType()
    {
        using var tempDirectory = new TemporaryDirectory();
        File.WriteAllLines(
            Path.Combine(tempDirectory.Path, "empty.txt"),
            ["", "   ", " # only a comment"]);
        var configuration = CreateConfiguration(
            Enum.GetValues<DirectoryObjectType>()
                .Select(objectType => new KeyValuePair<string, string?>(
                    $"Security:AttributeFiles:{objectType}",
                    "empty.txt")));
        var policy = CreatePolicy(configuration, tempDirectory.Path);

        Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.User, "displayName"));
        Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.Group, "groupType"));
        Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.Computer, "dnsHostName"));
        Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.OrganizationalUnit, "description"));
        Assert.False(policy.IsAttributeAllowed(DirectoryObjectType.User, "employeeID"));
        Assert.False(policy.HasAllowedAttributes((DirectoryObjectType)999));
        Assert.False(policy.IsAttributeAllowed((DirectoryObjectType)999, "displayName"));
    }

    [Theory]
    [InlineData("equals", true)]
    [InlineData("NOT_EQUALS", true)]
    [InlineData("contains", true)]
    [InlineData("not_contains", true)]
    [InlineData("starts_with", true)]
    [InlineData("not_starts_with", true)]
    [InlineData("ends_with", true)]
    [InlineData("NOT_ENDS_WITH", true)]
    [InlineData("and", true)]
    [InlineData("or", true)]
    [InlineData("regex", false)]
    public void FilterOperators_PreserveExistingCaseInsensitivePolicy(
        string operatorValue,
        bool expected)
    {
        var policy = CreatePolicy(CreateConfiguration(), AppContext.BaseDirectory);

        Assert.Equal(expected, policy.IsFilterOperatorAllowed(operatorValue));
    }

    [Fact]
    public async Task PlanValidator_BlankLeafOperatorStillDefaultsToEquals()
    {
        var configuration = CreateConfiguration();
        var policy = CreatePolicy(configuration, AppContext.BaseDirectory);
        var validator = CreateValidator(configuration, policy);
        var plan = CreatePlan("displayName", " ");

        var result = await validator.ValidateSecurityAsync(plan);

        Assert.True(result.OperationsValid, string.Join(Environment.NewLine, result.SecurityErrors));
        Assert.Equal("equals", plan.Steps[0].Filters[0].Operator);
    }

    [Fact]
    public async Task PlanValidator_UsesInjectedPolicyForAttributesAndOperators()
    {
        var configuration = CreateConfiguration();
        var validator = CreateValidator(configuration, new SentinelSecurityPolicy());

        var accepted = await validator.ValidateSecurityAsync(
            CreatePlan("policy_only_attribute", "policy_only_operator"));
        var rejected = await validator.ValidateSecurityAsync(
            CreatePlan("displayName", "equals"));

        Assert.True(accepted.OperationsValid, string.Join(Environment.NewLine, accepted.SecurityErrors));
        Assert.False(rejected.OperationsValid);
    }

    private static DirectorySecurityPolicy CreatePolicy(
        IConfiguration configuration,
        string contentRootPath)
    {
        return new DirectorySecurityPolicy(
            configuration,
            new TestWebHostEnvironment(contentRootPath),
            NullLogger<PlanValidator>.Instance);
    }

    private static PlanValidator CreateValidator(
        IConfiguration configuration,
        IDirectorySecurityPolicy policy)
    {
        return new PlanValidator(
            NullLogger<PlanValidator>.Instance,
            configuration,
            policy);
    }

    private static IConfiguration CreateConfiguration(
        IEnumerable<KeyValuePair<string, string?>>? settings = null)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
    }

    private static DirectoryQueryPlan CreatePlan(string attribute, string operatorValue)
    {
        return new DirectoryQueryPlan
        {
            Steps =
            [
                new DirectoryPlanStep
                {
                    Step = 1,
                    Name = "row",
                    Operation = "search",
                    TargetType = DirectoryObjectType.User,
                    Attributes = [attribute],
                    Filters =
                    [
                        new DirectoryFilter
                        {
                            Attribute = attribute,
                            Operator = operatorValue,
                            Value = "value"
                        }
                    ]
                }
            ],
            Projection = new ProjectionDefinition { RowStep = "row" }
        };
    }

    private sealed class SentinelSecurityPolicy : IDirectorySecurityPolicy
    {
        public bool HasAllowedAttributes(DirectoryObjectType objectType)
        {
            return objectType == DirectoryObjectType.User;
        }

        public bool IsAttributeAllowed(DirectoryObjectType objectType, string? attribute)
        {
            return objectType == DirectoryObjectType.User &&
                   string.Equals(attribute, "policy_only_attribute", StringComparison.OrdinalIgnoreCase);
        }

        public bool IsFilterOperatorAllowed(string? operatorValue)
        {
            return string.Equals(
                operatorValue,
                "policy_only_operator",
                StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class TestWebHostEnvironment(string contentRootPath) : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AdQueryOrchestrator.Tests";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = contentRootPath;

        public string EnvironmentName { get; set; } = Environments.Development;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = contentRootPath;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"adquery-security-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
