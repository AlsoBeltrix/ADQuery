using System.Text.Json;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class CsvEnrichmentServiceValidationTests
{
    [Theory]
    [InlineData(InvalidPlanCase.NullPlan, "CSV enrichment plan is required.")]
    [InlineData(InvalidPlanCase.BlankMatchColumn, "match_column is required.")]
    [InlineData(InvalidPlanCase.UnknownMatchColumn, "match_column must match a CSV header.")]
    [InlineData(InvalidPlanCase.BlankMatchAttribute, "match_attribute is required.")]
    [InlineData(InvalidPlanCase.DisallowedMatchAttribute, "match_attribute is not allow-listed for User directory queries.")]
    [InlineData(InvalidPlanCase.PaddedMatchAttribute, "match_attribute is not allow-listed for User directory queries.")]
    [InlineData(InvalidPlanCase.NullRetrieveAttributes, "retrieve_attributes is required.")]
    [InlineData(InvalidPlanCase.BlankRetrieveAttribute, "retrieve_attributes contains an empty value.")]
    [InlineData(InvalidPlanCase.DisallowedRetrieveAttribute, "retrieve_attributes contains an attribute not allow-listed for User directory queries.")]
    [InlineData(InvalidPlanCase.BlankFilterAttribute, "filter.attribute is required.")]
    [InlineData(InvalidPlanCase.DisallowedFilterAttribute, "filter.attribute is not allow-listed for User directory queries.")]
    [InlineData(InvalidPlanCase.BlankFilterOperator, "filter.operator is required.")]
    [InlineData(InvalidPlanCase.NonCanonicalFilterOperator, "filter.operator is not allowed by the directory security policy.")]
    [InlineData(InvalidPlanCase.NotStartsWithFilterOperator, "filter.operator is not supported for CSV enrichment.")]
    [InlineData(InvalidPlanCase.NotEndsWithFilterOperator, "filter.operator is not supported for CSV enrichment.")]
    [InlineData(InvalidPlanCase.CompoundAndFilterOperator, "filter.operator is not supported for CSV enrichment.")]
    [InlineData(InvalidPlanCase.CompoundOrFilterOperator, "filter.operator is not supported for CSV enrichment.")]
    [InlineData(InvalidPlanCase.BlankFilterValue, "filter.value is required.")]
    [InlineData(InvalidPlanCase.BlankOutputMode, "output_mode must be 'all' or 'filtered'.")]
    [InlineData(InvalidPlanCase.MatchedOutputMode, "output_mode must be 'all' or 'filtered'.")]
    [InlineData(InvalidPlanCase.PaddedOutputMode, "output_mode must be 'all' or 'filtered'.")]
    public async Task ExecuteAsync_InvalidPlanFailsBeforeDirectoryAccessWithoutMutation(
        InvalidPlanCase invalidCase,
        string expectedError)
    {
        var plan = CreateInvalidPlan(invalidCase);
        var originalJson = plan is null ? null : JsonSerializer.Serialize(plan);
        var originalRetrieveAttributes = plan?.RetrieveAttributes;
        var originalFilter = plan?.Filter;
        var directory = new RecordingDirectoryService();
        var service = CreateService(CreateDefaultPolicy(), directory);

        var result = await service.ExecuteAsync(
            plan,
            ["Employee"],
            [["ada"]],
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(CsvEnrichmentFailureKind.Validation, result.FailureKind);
        Assert.Empty(result.Data);
        Assert.Contains(expectedError, result.Errors);
        Assert.Empty(directory.Requests);
        if (plan is not null)
        {
            Assert.Equal(originalJson, JsonSerializer.Serialize(plan));
            Assert.Same(originalRetrieveAttributes, plan.RetrieveAttributes);
            Assert.Same(originalFilter, plan.Filter);
        }
    }

    [Fact]
    public async Task ExecuteAsync_ValidMixedCasePlanUsesImmutableAuthorizedSnapshot()
    {
        var policy = CreateDefaultPolicy();
        var record = new DirectoryRecord
        {
            ObjectType = DirectoryObjectType.User,
            DistinguishedName = "CN=Ada,DC=example,DC=com"
        };
        record["displayName"] = "Ada Lovelace";
        record["department"] = new[] { "Research", "Engineering" };
        var directory = new RecordingDirectoryService([record]);
        var service = CreateService(policy, directory);
        var plan = new CsvEnrichmentPlan
        {
            MatchColumn = "EMPLOYEE",
            MatchAttribute = "MAIL",
            RetrieveAttributes = ["displayName", "DISPLAYNAME"],
            Filter = new CsvEnrichmentFilter
            {
                Attribute = "Department",
                Operator = "CONTAINS",
                Value = "engineer"
            },
            OutputMode = "FILTERED"
        };
        var originalJson = JsonSerializer.Serialize(plan);
        var originalRetrieveAttributes = plan.RetrieveAttributes;
        var originalFilter = plan.Filter;

        var result = await service.ExecuteAsync(
            plan,
            ["employee"],
            [["ada"]],
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(1, result.MatchedRows);
        Assert.Equal(1, result.FilteredRows);
        var output = Assert.Single(result.Data);
        Assert.Equal("Ada Lovelace", output["AD_displayName"]);
        Assert.Equal("Matched", output["AD_Status"]);

        var request = Assert.Single(directory.Requests);
        Assert.Equal(
            ["displayName", "Department", "distinguishedName"],
            request.Attributes);
        var requestFilter = Assert.Single(request.Filters);
        Assert.Equal("MAIL", requestFilter.Attribute);
        Assert.Equal("equals", requestFilter.Operator);
        Assert.Equal("ada", requestFilter.Value);
        Assert.DoesNotContain(
            request.Attributes,
            attribute => attribute.Equals("mail", StringComparison.OrdinalIgnoreCase));
        Assert.All(
            request.Attributes,
            attribute => Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.User, attribute)));
        Assert.All(
            request.Filters,
            filter => Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.User, filter.Attribute)));

        Assert.Equal(originalJson, JsonSerializer.Serialize(plan));
        Assert.Same(originalRetrieveAttributes, plan.RetrieveAttributes);
        Assert.Same(originalFilter, plan.Filter);
        Assert.Equal(2, plan.RetrieveAttributes.Count);
    }

    [Fact]
    public async Task ExecuteAsync_DisallowedRetrieveAttributeFailsBeforeDirectoryAccess()
    {
        var directory = new RecordingDirectoryService();
        var service = CreateService(CreateDefaultPolicy(), directory);
        var plan = CreateValidPlan();
        plan.RetrieveAttributes = ["forbiddenAttribute"];

        var result = await service.ExecuteAsync(
            plan,
            ["Employee"],
            [["ada"]],
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(CsvEnrichmentFailureKind.Validation, result.FailureKind);
        Assert.Contains(
            "retrieve_attributes contains an attribute not allow-listed for User directory queries.",
            result.Errors);
        Assert.Empty(result.Data);
        Assert.Empty(directory.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_MissingUserPolicyFailsBeforeDirectoryAccess()
    {
        var policy = CreateDefaultPolicy();
        policy.Attributes.Clear();
        var directory = new RecordingDirectoryService();
        var service = CreateService(policy, directory);

        var result = await service.ExecuteAsync(
            CreateValidPlan(),
            ["Employee"],
            [["ada"]],
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(
            "No allow-listed user attributes are configured for CSV enrichment.",
            result.Errors);
        Assert.Empty(directory.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_EmptyIdentifierPreservesAllAndFilteredBehaviorWithoutDirectoryAccess()
    {
        var directory = new RecordingDirectoryService();
        var service = CreateService(CreateDefaultPolicy(), directory);
        var allPlan = CreateValidPlan();
        var filteredPlan = CreateValidPlan();
        filteredPlan.OutputMode = "filtered";

        var allResult = await service.ExecuteAsync(
            allPlan,
            ["Employee"],
            [[""]],
            TestContext.Current.CancellationToken);
        var filteredResult = await service.ExecuteAsync(
            filteredPlan,
            ["Employee"],
            [[""]],
            TestContext.Current.CancellationToken);

        Assert.True(allResult.Success);
        Assert.Equal("Empty identifier", Assert.Single(allResult.Data)["AD_Status"]);
        Assert.True(filteredResult.Success);
        Assert.Empty(filteredResult.Data);
        Assert.Empty(directory.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_MissingDistinguishedNamePolicyFailsBeforeDirectoryAccess()
    {
        var policy = CreateDefaultPolicy();
        policy.Attributes.Remove("distinguishedName");
        var directory = new RecordingDirectoryService();
        var service = CreateService(policy, directory);

        var result = await service.ExecuteAsync(
            CreateValidPlan(),
            ["Employee"],
            [["ada"]],
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(
            "CSV enrichment requires the allow-listed internal attribute 'distinguishedName'.",
            result.Errors);
        Assert.Empty(directory.Requests);
    }

    [Theory]
    [InlineData(
        "Enabled",
        "userAccountControl",
        "CSV enrichment requires the allow-listed backing attribute 'userAccountControl' for 'Enabled'.")]
    [InlineData(
        "AccountExpirationDate",
        "accountExpires",
        "CSV enrichment requires the allow-listed backing attribute 'accountExpires' for 'AccountExpirationDate'.")]
    public async Task ExecuteAsync_MissingBackingAttributePolicyFailsBeforeDirectoryAccess(
        string requestedAttribute,
        string backingAttribute,
        string expectedError)
    {
        var policy = CreateDefaultPolicy();
        policy.Attributes.Add(requestedAttribute);
        policy.Attributes.Remove(backingAttribute);
        var directory = new RecordingDirectoryService();
        var service = CreateService(policy, directory);
        var plan = CreateValidPlan();
        plan.RetrieveAttributes = [requestedAttribute];

        var result = await service.ExecuteAsync(
            plan,
            ["Employee"],
            [["ada"]],
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains(expectedError, result.Errors);
        Assert.Empty(directory.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_ValidatedBackingAttributesAreExplicitInDirectoryRequest()
    {
        var policy = CreateDefaultPolicy();
        policy.Attributes.UnionWith(
            ["Enabled", "userAccountControl", "AccountExpirationDate", "accountExpires"]);
        var directory = new RecordingDirectoryService();
        var service = CreateService(policy, directory);
        var plan = CreateValidPlan();
        plan.RetrieveAttributes = ["Enabled", "AccountExpirationDate"];

        var result = await service.ExecuteAsync(
            plan,
            ["Employee"],
            [["ada"]],
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        var request = Assert.Single(directory.Requests);
        Assert.Equal(
            ["Enabled", "AccountExpirationDate", "distinguishedName", "userAccountControl", "accountExpires"],
            request.Attributes);
        Assert.All(
            request.Attributes,
            attribute => Assert.True(policy.IsAttributeAllowed(DirectoryObjectType.User, attribute)));
    }

    [Fact]
    public async Task ExecuteAsync_ValidationErrorsRemainBoundedWithoutEchoingModelValues()
    {
        var policy = CreateDefaultPolicy();
        var directory = new RecordingDirectoryService();
        var service = CreateService(policy, directory);
        var hostileValue = new string('x', 10_000);
        var plan = CreateValidPlan();
        plan.RetrieveAttributes = Enumerable.Range(0, 1_000)
            .Select(index => index % 2 == 0 ? string.Empty : $"{hostileValue}{index}")
            .ToList();

        var result = await service.ExecuteAsync(
            plan,
            ["Employee"],
            [["ada"]],
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.InRange(result.Errors.Count, 1, 4);
        Assert.All(result.Errors, error => Assert.InRange(error.Length, 1, 200));
        Assert.DoesNotContain(result.Errors, error => error.Contains(hostileValue, StringComparison.Ordinal));
        Assert.Empty(directory.Requests);
    }

    [Fact]
    public void Validate_RequiresBothCanonicalPolicyAndCsvEvaluatorCapability()
    {
        var evaluator = new CsvEnrichmentFilterEvaluator();
        var canonicalDeniesContains = CreateDefaultPolicy();
        canonicalDeniesContains.Operators.Remove("contains");
        var canonicalAllowsSentinel = CreateDefaultPolicy();
        canonicalAllowsSentinel.Operators.Add("sentinel_operator");
        var containsPlan = CreateValidPlan();
        containsPlan.Filter = new CsvEnrichmentFilter
        {
            Attribute = "department",
            Operator = "contains",
            Value = "Engineering"
        };
        var sentinelPlan = CreateValidPlan();
        sentinelPlan.Filter = new CsvEnrichmentFilter
        {
            Attribute = "department",
            Operator = "sentinel_operator",
            Value = "Engineering"
        };

        var policyResult = new CsvEnrichmentPlanValidator(canonicalDeniesContains, evaluator)
            .Validate(containsPlan, ["Employee"]);
        var evaluatorResult = new CsvEnrichmentPlanValidator(canonicalAllowsSentinel, evaluator)
            .Validate(sentinelPlan, ["Employee"]);

        Assert.False(policyResult.IsValid);
        Assert.Contains(
            "filter.operator is not allowed by the directory security policy.",
            policyResult.Errors);
        Assert.False(evaluatorResult.IsValid);
        Assert.DoesNotContain(
            "filter.operator is not allowed by the directory security policy.",
            evaluatorResult.Errors);
        Assert.Contains(
            "filter.operator is not supported for CSV enrichment.",
            evaluatorResult.Errors);
    }

    private static CsvEnrichmentService CreateService(
        TestDirectorySecurityPolicy policy,
        RecordingDirectoryService directory)
    {
        var evaluator = new CsvEnrichmentFilterEvaluator();
        return new CsvEnrichmentService(
            NullLogger<CsvEnrichmentService>.Instance,
            directory,
            new CsvEnrichmentPlanValidator(policy, evaluator),
            evaluator);
    }

    private static TestDirectorySecurityPolicy CreateDefaultPolicy()
    {
        return new TestDirectorySecurityPolicy(
            ["sAMAccountName", "mail", "displayName", "department", "distinguishedName"],
            [
                "equals",
                "not_equals",
                "contains",
                "not_contains",
                "starts_with",
                "not_starts_with",
                "ends_with",
                "not_ends_with",
                "and",
                "or"
            ]);
    }

    private static CsvEnrichmentPlan CreateValidPlan()
    {
        return new CsvEnrichmentPlan
        {
            MatchColumn = "Employee",
            MatchAttribute = "sAMAccountName",
            RetrieveAttributes = ["displayName"],
            OutputMode = "all"
        };
    }

    private static CsvEnrichmentPlan? CreateInvalidPlan(InvalidPlanCase invalidCase)
    {
        if (invalidCase == InvalidPlanCase.NullPlan)
        {
            return null;
        }

        var plan = CreateValidPlan();
        switch (invalidCase)
        {
            case InvalidPlanCase.BlankMatchColumn:
                plan.MatchColumn = " ";
                break;
            case InvalidPlanCase.UnknownMatchColumn:
                plan.MatchColumn = "Unknown";
                break;
            case InvalidPlanCase.BlankMatchAttribute:
                plan.MatchAttribute = " ";
                break;
            case InvalidPlanCase.DisallowedMatchAttribute:
                plan.MatchAttribute = "forbiddenAttribute";
                break;
            case InvalidPlanCase.PaddedMatchAttribute:
                plan.MatchAttribute = " sAMAccountName ";
                break;
            case InvalidPlanCase.NullRetrieveAttributes:
                plan.RetrieveAttributes = null!;
                break;
            case InvalidPlanCase.BlankRetrieveAttribute:
                plan.RetrieveAttributes = [""];
                break;
            case InvalidPlanCase.DisallowedRetrieveAttribute:
                plan.RetrieveAttributes = ["forbiddenAttribute"];
                break;
            case InvalidPlanCase.BlankFilterAttribute:
                plan.Filter = CreateFilter(attribute: " ");
                break;
            case InvalidPlanCase.DisallowedFilterAttribute:
                plan.Filter = CreateFilter(attribute: "forbiddenAttribute");
                break;
            case InvalidPlanCase.BlankFilterOperator:
                plan.Filter = CreateFilter(operatorValue: " ");
                break;
            case InvalidPlanCase.NonCanonicalFilterOperator:
                plan.Filter = CreateFilter(operatorValue: "regex");
                break;
            case InvalidPlanCase.NotStartsWithFilterOperator:
                plan.Filter = CreateFilter(operatorValue: "not_starts_with");
                break;
            case InvalidPlanCase.NotEndsWithFilterOperator:
                plan.Filter = CreateFilter(operatorValue: "not_ends_with");
                break;
            case InvalidPlanCase.CompoundAndFilterOperator:
                plan.Filter = CreateFilter(operatorValue: "and");
                break;
            case InvalidPlanCase.CompoundOrFilterOperator:
                plan.Filter = CreateFilter(operatorValue: "or");
                break;
            case InvalidPlanCase.BlankFilterValue:
                plan.Filter = CreateFilter(value: " ");
                break;
            case InvalidPlanCase.BlankOutputMode:
                plan.OutputMode = " ";
                break;
            case InvalidPlanCase.MatchedOutputMode:
                plan.OutputMode = "matched";
                break;
            case InvalidPlanCase.PaddedOutputMode:
                plan.OutputMode = " all ";
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidCase), invalidCase, null);
        }

        return plan;
    }

    private static CsvEnrichmentFilter CreateFilter(
        string attribute = "department",
        string operatorValue = "equals",
        string value = "Engineering")
    {
        return new CsvEnrichmentFilter
        {
            Attribute = attribute,
            Operator = operatorValue,
            Value = value
        };
    }

    public enum InvalidPlanCase
    {
        NullPlan,
        BlankMatchColumn,
        UnknownMatchColumn,
        BlankMatchAttribute,
        DisallowedMatchAttribute,
        PaddedMatchAttribute,
        NullRetrieveAttributes,
        BlankRetrieveAttribute,
        DisallowedRetrieveAttribute,
        BlankFilterAttribute,
        DisallowedFilterAttribute,
        BlankFilterOperator,
        NonCanonicalFilterOperator,
        NotStartsWithFilterOperator,
        NotEndsWithFilterOperator,
        CompoundAndFilterOperator,
        CompoundOrFilterOperator,
        BlankFilterValue,
        BlankOutputMode,
        MatchedOutputMode,
        PaddedOutputMode
    }

    private sealed class TestDirectorySecurityPolicy : IDirectorySecurityPolicy
    {
        public TestDirectorySecurityPolicy(
            IEnumerable<string> attributes,
            IEnumerable<string> operators)
        {
            Attributes = new HashSet<string>(attributes, StringComparer.OrdinalIgnoreCase);
            Operators = new HashSet<string>(operators, StringComparer.OrdinalIgnoreCase);
        }

        public HashSet<string> Attributes { get; }

        public HashSet<string> Operators { get; }

        public bool HasAllowedAttributes(DirectoryObjectType objectType)
        {
            return objectType == DirectoryObjectType.User && Attributes.Count > 0;
        }

        public bool IsAttributeAllowed(DirectoryObjectType objectType, string? attribute)
        {
            return objectType == DirectoryObjectType.User &&
                   attribute is not null &&
                   Attributes.Contains(attribute);
        }

        public bool IsFilterOperatorAllowed(string? operatorValue)
        {
            return operatorValue is not null && Operators.Contains(operatorValue);
        }
    }

    private sealed class RecordingDirectoryService(
        IReadOnlyList<DirectoryRecord>? results = null) : IActiveDirectoryService
    {
        public List<DirectorySearchRequest> Requests { get; } = [];

        public Task<IReadOnlyList<DirectoryRecord>> SearchAsync(
            DirectorySearchRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            return Task.FromResult(results ?? (IReadOnlyList<DirectoryRecord>)Array.Empty<DirectoryRecord>());
        }

        public Task<IReadOnlyList<DirectoryRecord>> ExpandGroupMembersAsync(
            IEnumerable<string> groupDistinguishedNames,
            bool recursive,
            IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<DirectoryRecord>> LookupAsync(
            IEnumerable<string> distinguishedNames,
            DirectoryObjectType targetType,
            IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<DirectoryRecord>> GetDirectReportsBatch(
            IEnumerable<string> managerDistinguishedNames,
            IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }
}
