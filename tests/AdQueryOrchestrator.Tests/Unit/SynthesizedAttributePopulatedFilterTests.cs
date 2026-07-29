using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// slice5-or-1 guard. The populated-attribute reading (F04-D3) must reach every attribute,
/// including the two the search layer synthesizes.
///
/// <c>Enabled</c> and <c>AccountExpirationDate</c> are not read verbatim from the directory —
/// <c>ActiveDirectoryService.MapToRecord</c> derives them from <c>userAccountControl</c> and
/// <c>accountExpires</c>, defaulting the latter to the literal "Never" — so both are populated
/// on every record. Before the fix their attribute-specific clause builders ran first and
/// answered a different question: a blank value reads as "disabled" to one and as "never
/// expires" to the other, so <c>not_equals ""</c> returned only enabled, or only already
/// expired, accounts. Because that decision is taken at the LDAP layer, the excluded records
/// never reach the process and no in-memory pass can restore them.
/// </summary>
public sealed class SynthesizedAttributePopulatedFilterTests
{
    public static TheoryData<string> NegationOperators =>
        ["not_equals", "not_contains", "not_starts_with", "not_ends_with"];

    public static TheoryData<string, string> SynthesizedAttributes =>
        new()
        {
            { "Enabled", "userAccountControl" },
            { "AccountExpirationDate", "accountExpires" },
        };

    [Theory]
    [MemberData(nameof(SynthesizedAttributes))]
    public void PopulatedFilter_OnASynthesizedAttribute_MatchesEveryRecord(
        string attribute, string underlyingAttribute)
    {
        var clause = ActiveDirectoryService.BuildFilterClause(
            new DirectoryFilter { Attribute = attribute, Operator = "not_equals", Value = "" });

        // Every record qualifies, so the clause must constrain nothing.
        Assert.Equal("(objectClass=*)", clause);

        // Specifically not the pre-fix clauses, which each answered a different question.
        Assert.DoesNotContain(underlyingAttribute, clause);
        Assert.DoesNotContain("!", clause);
    }

    [Theory]
    [MemberData(nameof(NegationOperators))]
    public void EveryNegationOperator_ReadsTheSameOnASynthesizedAttribute(string op)
    {
        // The four operators agreeing is the whole point of F04-D3; the special-cased
        // builders only ever handled equals/not_equals, so the other two fell through to
        // their catch-all "expired" clause.
        Assert.Equal(
            "(objectClass=*)",
            ActiveDirectoryService.BuildFilterClause(
                new DirectoryFilter { Attribute = "AccountExpirationDate", Operator = op, Value = "" }));

        Assert.Equal(
            "(objectClass=*)",
            ActiveDirectoryService.BuildFilterClause(
                new DirectoryFilter { Attribute = "Enabled", Operator = op, Value = "  " }));
    }

    [Theory]
    [MemberData(nameof(SynthesizedAttributes))]
    public async Task InMemoryEvaluation_AgreesWithTheLdapClause(string attribute, string _)
    {
        // A record the LDAP clause admits must survive the projection filter too — otherwise
        // the two layers disagree and the fix only relocates the defect. The records
        // deliberately carry no value for the attribute, which is exactly the state a search
        // that did not request it leaves behind: HasPopulatedValue alone would drop them.
        var records = new List<DirectoryRecord>();
        foreach (var name in new[] { "First", "Second" })
        {
            var record = new DirectoryRecord { DistinguishedName = $"CN={name},DC=x" };
            record["displayName"] = name;
            records.Add(record);

            Assert.False(EmptyValueFilterSemantics.HasPopulatedValue(record, attribute));
        }

        Assert.Equal(["First", "Second"], await ExecuteAsync(records, attribute, "not_equals"));
    }

    private static async Task<List<string?>> ExecuteAsync(
        IReadOnlyList<DirectoryRecord> records, string attribute, string op)
    {
        DirectoryFilter Filter() => new() { Attribute = attribute, Operator = op, Value = "" };

        var plan = new DirectoryQueryPlan
        {
            Description = "populated filter on a synthesized attribute",
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

        var executor = new DirectoryPlanExecutor(
            NullLogger<DirectoryPlanExecutor>.Instance,
            new PermissiveValidator(),
            new FixedDirectoryService(records));

        var result = await executor.ExecutePlanAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        return result.Data.Select(row => row["Name"] as string).ToList();
    }

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

    [Fact]
    public void OrdinaryAttributes_StillUsePresence_AndAreNotAlwaysPopulated()
    {
        // The over-removal sentinel: widening the synthesized pair must not turn every
        // populated filter into "match everything".
        Assert.Equal(
            "(manager=*)",
            ActiveDirectoryService.BuildFilterClause(
                new DirectoryFilter { Attribute = "manager", Operator = "not_equals", Value = "" }));

        Assert.False(EmptyValueFilterSemantics.IsAlwaysPopulatedAttribute("manager"));
        Assert.False(EmptyValueFilterSemantics.IsAlwaysPopulatedAttribute(null));
    }

    [Fact]
    public void FiltersCarryingARealValue_KeepTheirSpecialCasedClauses()
    {
        // The populated branch is entered only on an empty value, so the Enabled and
        // AccountExpirationDate builders must still own every other form.
        var enabled = ActiveDirectoryService.BuildFilterClause(
            new DirectoryFilter { Attribute = "Enabled", Operator = "equals", Value = "true" });

        Assert.Contains("userAccountControl", enabled);

        var expires = ActiveDirectoryService.BuildFilterClause(
            new DirectoryFilter { Attribute = "AccountExpirationDate", Operator = "equals", Value = "never" });

        Assert.Contains("accountExpires", expires);
    }

    [Fact]
    public void CompoundFilters_CarryThePopulatedReadingIntoChildren()
    {
        var clause = ActiveDirectoryService.BuildFilterClause(new DirectoryFilter
        {
            Operator = "and",
            Conditions = new List<DirectoryFilter>
            {
                new() { Attribute = "Enabled", Operator = "not_equals", Value = "" },
                new() { Attribute = "manager", Operator = "not_equals", Value = "" },
            },
        });

        Assert.Equal("(&(objectClass=*)(manager=*))", clause);
    }
}
