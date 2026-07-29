using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using AdQuery.Orchestrator.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F04 Slice 5 guard (F04-D3). A negation operator with an empty value means "the
/// attribute is populated" — the reading the model uses for "who has a manager". It used
/// to fault the whole turn with "Projection filter value is required".
///
/// The four negation operators must agree. Only <c>not_equals ""</c> ever evaluated
/// correctly; <c>not_contains</c>/<c>not_starts_with</c>/<c>not_ends_with</c> would have
/// matched *nothing*, because their base operators short-circuit to true on an empty
/// value (f04-or-5). A guard covering only <c>not_equals</c> is vacuous here.
/// </summary>
public sealed class EmptyValueNegationFilterTests
{
    private const string Attribute = "manager";

    public static TheoryData<string> NegationOperators =>
        ["not_equals", "not_contains", "not_starts_with", "not_ends_with"];

    // --- Validator: the plan is accepted, not faulted. ---

    [Theory]
    [MemberData(nameof(NegationOperators))]
    public async Task NegationWithEmptyValue_IsAccepted_OnProjectionAndStepFilters(string op)
    {
        var result = await ValidateAsync(PlanWith(op, value: ""));

        Assert.True(result.OperationsValid, string.Join(Environment.NewLine, result.SecurityErrors));
        Assert.DoesNotContain(result.SecurityErrors, e => e.Contains("value is required"));
    }

    [Theory]
    [MemberData(nameof(NegationOperators))]
    public async Task NegationWithWhitespaceValue_IsAlsoAccepted(string op)
    {
        // The validator's own emptiness test is IsNullOrWhiteSpace, so "   " must take the
        // same branch rather than falling through as a real value to compare against.
        var result = await ValidateAsync(PlanWith(op, value: "   "));

        Assert.True(result.OperationsValid, string.Join(Environment.NewLine, result.SecurityErrors));
    }

    [Theory]
    [InlineData("equals")]
    [InlineData("contains")]
    [InlineData("starts_with")]
    [InlineData("ends_with")]
    public async Task PositiveOperatorWithEmptyValue_IsStillRejected(string op)
    {
        // The strictness that remains: only negation carries the populated reading.
        var result = await ValidateAsync(PlanWith(op, value: ""));

        Assert.False(result.OperationsValid);
        Assert.Contains(result.SecurityErrors, e => e.Contains("Projection filter value is required"));
    }

    [Fact]
    public async Task AccountExpirationDateEqualsEmpty_KeepsItsLegacyMeaning()
    {
        // "never expires" is a positive operator and a distinct concept from "populated";
        // widening negation must not have narrowed it. (Whether the attribute is
        // allow-listed is a separate policy question this test does not own, so only the
        // empty-value rejection is asserted against.)
        var result = await ValidateAsync(PlanWith("equals", value: "", attribute: "AccountExpirationDate"));

        Assert.DoesNotContain(result.SecurityErrors, e => e.Contains("value is required"));
        Assert.True(
            EmptyValueFilterSemantics.AllowsEmptyValue("AccountExpirationDate", "equals"));
        Assert.False(EmptyValueFilterSemantics.AllowsEmptyValue("manager", "equals"));
    }

    // --- Executor: all four operators return the populated subset. ---

    [Theory]
    [MemberData(nameof(NegationOperators))]
    public async Task NegationWithEmptyValue_ReturnsThePopulatedSubset(string op)
    {
        // Pre-fix this returned the empty set for three of the four operators: their base
        // operator matches everything on an empty needle, and the negation inverts it.
        var names = await ExecuteAsync(op, value: "");

        Assert.Equal(["HasManager", "MultiValued"], names);
    }

    [Theory]
    [MemberData(nameof(NegationOperators))]
    public async Task NegationWithWhitespaceValue_ReadsIdenticallyToEmpty(string op)
    {
        Assert.Equal(["HasManager", "MultiValued"], await ExecuteAsync(op, value: "  "));
    }

    [Fact]
    public async Task NegationWithARealValue_KeepsOrdinaryNegationSemantics()
    {
        // The populated reading must not swallow the normal case: not_equals with a value
        // still excludes only the records holding that value.
        var names = await ExecuteAsync("not_equals", value: "CN=Boss,DC=x");

        Assert.Equal(["Absent", "Blank", "Whitespace", "MultiValued"], names);
    }

    // --- Fixtures ---

    /// <summary>
    /// The attribute-shape matrix: absent, blank, whitespace-only, populated, multivalued.
    /// Whitespace-only counts as NOT populated, matching the validator's reading.
    /// </summary>
    private static IReadOnlyList<DirectoryRecord> Matrix()
    {
        var absent = Record("Absent");

        var blank = Record("Blank");
        blank[Attribute] = string.Empty;

        var whitespace = Record("Whitespace");
        whitespace[Attribute] = "   ";

        var populated = Record("HasManager");
        populated[Attribute] = "CN=Boss,DC=x";

        var multi = Record("MultiValued");
        multi[Attribute] = new[] { "  ", "CN=Other,DC=x" };

        return [absent, blank, whitespace, populated, multi];
    }

    private static DirectoryRecord Record(string name)
    {
        var record = new DirectoryRecord { DistinguishedName = $"CN={name},DC=x" };
        record["displayName"] = name;
        return record;
    }

    private static async Task<List<string?>> ExecuteAsync(string op, string value)
    {
        var plan = PlanWith(op, value);
        var executor = new DirectoryPlanExecutor(
            NullLogger<DirectoryPlanExecutor>.Instance,
            new PermissiveValidator(),
            new FixedDirectoryService(Matrix()));

        var result = await executor.ExecutePlanAsync(plan, CancellationToken.None);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        return result.Data.Select(row => row["Name"] as string).ToList();
    }

    /// <summary>
    /// The filter sits on the projection AND on the row step, so acceptance cannot depend
    /// on where in the plan it appears.
    /// </summary>
    private static DirectoryQueryPlan PlanWith(string op, string value, string attribute = Attribute)
    {
        DirectoryFilter Filter() => new() { Attribute = attribute, Operator = op, Value = value };

        return new DirectoryQueryPlan
        {
            Description = "empty-value negation",
            Steps =
            {
                new DirectoryPlanStep
                {
                    Step = 1,
                    Name = "row",
                    Operation = "search",
                    TargetType = DirectoryObjectType.User,
                    Attributes = { "displayName", attribute },
                    Filters = { Filter() },
                },
            },
            Projection = new ProjectionDefinition
            {
                RowStep = "row",
                Columns = { new ProjectionColumn { Name = "Name", Attribute = "displayName" } },
                Filter = Filter(),
            },
        };
    }

    private static Task<PlanSecurityResult> ValidateAsync(DirectoryQueryPlan plan)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();
        var policy = new DirectorySecurityPolicy(
            configuration,
            new BaseDirectoryEnvironment(),
            NullLogger<PlanValidator>.Instance);

        return new PlanValidator(NullLogger<PlanValidator>.Instance, configuration, policy)
            .ValidateSecurityAsync(plan);
    }

    /// <summary>
    /// Returns the matrix for the step search — the step filter is applied in-process by
    /// the projection path, so the fixture does not need to reimplement LDAP matching.
    /// </summary>
    private sealed class FixedDirectoryService : IActiveDirectoryService
    {
        private readonly IReadOnlyList<DirectoryRecord> _records;

        public FixedDirectoryService(IReadOnlyList<DirectoryRecord> records) => _records = records;

        public Task<IReadOnlyList<DirectoryRecord>> SearchAsync(
            DirectorySearchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_records);

        public Task<IReadOnlyList<DirectoryRecord>> ExpandGroupMembersAsync(
            IEnumerable<string> groupDistinguishedNames, bool recursive, IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DirectoryRecord>>([]);

        public Task<IReadOnlyList<DirectoryRecord>> LookupAsync(
            IEnumerable<string> distinguishedNames, DirectoryObjectType targetType, IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DirectoryRecord>>([]);

        public Task<IReadOnlyList<DirectoryRecord>> GetDirectReportsBatch(
            IEnumerable<string> managerDistinguishedNames, IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DirectoryRecord>>([]);
    }

    private sealed class PermissiveValidator : IPlanValidator
    {
        public Task<PlanSecurityResult> ValidateSecurityAsync(DirectoryQueryPlan plan)
            => Task.FromResult(new PlanSecurityResult());

        public bool ValidateHmac(DirectoryQueryPlan plan, string signature) => true;

        public bool ValidateComplexity(DirectoryQueryPlan plan) => true;
    }

    private sealed class BaseDirectoryEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "AdQueryOrchestrator.Tests";

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public string EnvironmentName { get; set; } = Environments.Development;

        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();

        public string WebRootPath { get; set; } = AppContext.BaseDirectory;
    }
}
