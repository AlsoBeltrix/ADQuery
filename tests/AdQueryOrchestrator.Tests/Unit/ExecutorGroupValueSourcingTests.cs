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
/// slice1r2-or-1 guard. The executor sources each row's <c>group_by</c> values from the
/// row-step directory record, not from the display projection. A plan may legitimately
/// group on an attribute it never shows, and an aliased column must not change how the
/// result is grouped — deriving the key from the projected row broke both.
/// </summary>
public sealed class ExecutorGroupValueSourcingTests
{
    private const string Group = "employeeType";

    private static DirectoryRecord Record(string name, string? employeeType, string? city = null)
    {
        var record = new DirectoryRecord { DistinguishedName = $"CN={name},DC=x" };
        record["displayName"] = name;
        record[Group] = employeeType;
        record["l"] = city;
        return record;
    }

    private static DirectoryQueryPlan PlanWith(
        IEnumerable<ProjectionColumn> columns,
        params string[] groupBy) => new()
        {
            Description = "group values sourcing",
            Steps = { new DirectoryPlanStep { Step = 1, Name = "s1", Operation = "search" } },
            Projection = new ProjectionDefinition
            {
                RowStep = "s1",
                Columns = columns.ToList(),
                Aggregation = new AggregationDefinition { Count = true, GroupBy = groupBy.ToList() },
            },
        };

    private static async Task<PlanExecutionResult> ExecuteAsync(
        DirectoryQueryPlan plan,
        params DirectoryRecord[] records)
    {
        var executor = new DirectoryPlanExecutor(
            NullLogger<DirectoryPlanExecutor>.Instance,
            new PermissiveValidator(),
            new FixedDirectoryService(records));

        return await executor.ExecutePlanAsync(plan, CancellationToken.None);
    }

    [Fact]
    public async Task GroupByAnUnprojectedAttribute_StillYieldsTheRealDistribution()
    {
        // Projection shows only the name; grouping is on employeeType. Reading the key
        // from the projected row found nothing and folded all three into "(empty)".
        var plan = PlanWith(
            [new ProjectionColumn { Name = "Name", Attribute = "displayName" }],
            Group);

        var result = await ExecuteAsync(
            plan,
            Record("Ann", "CWK"),
            Record("Bo", "CWK"),
            Record("Cy", "FTE"));

        Assert.True(result.Success);
        Assert.Equal(3, result.Data.Count);
        Assert.All(result.Data, row => Assert.False(row.ContainsKey(Group)));

        var counts = Assert.IsType<Dictionary<string, int>>(
            QueryJobManager.ComputeSettledAggregation(
                plan, result.Data, result.GroupValues, result.Warnings)!["grouped_counts"]);

        Assert.Equal(2, counts.Count);
        Assert.Equal(2, counts["CWK"]);
        Assert.Equal(1, counts["FTE"]);
        Assert.DoesNotContain("(empty)", counts.Keys);
    }

    [Fact]
    public async Task AliasedProjectionColumn_DoesNotChangeTheGrouping()
    {
        // The same attribute displayed under a friendly name: grouping is identical
        // because it never consults the projected row's key.
        var plan = PlanWith(
            [new ProjectionColumn { Name = "Worker Type", Attribute = Group }],
            Group);

        var result = await ExecuteAsync(plan, Record("Ann", "CWK"), Record("Bo", "FTE"));

        Assert.Equal(new[] { "CWK" }, result.GroupValues[0]);
        Assert.Equal(new[] { "FTE" }, result.GroupValues[1]);
    }

    [Fact]
    public async Task GroupValuesStayPositionalWithRows_IncludingUnsetAttributes()
    {
        // One entry per emitted row, in group_by order; an unset or blank attribute is a
        // null, so the settlement layer can tell it apart from a value.
        var plan = PlanWith(
            [new ProjectionColumn { Name = "Name", Attribute = "displayName" }],
            Group,
            "l");

        var result = await ExecuteAsync(
            plan,
            Record("Ann", "CWK", "Dublin"),
            Record("Bo", null, "Cork"),
            Record("Cy", "FTE", "   "));

        Assert.Equal(result.Data.Count, result.GroupValues.Count);
        Assert.Equal(new[] { "CWK", "Dublin" }, result.GroupValues[0]);
        Assert.Equal(new string?[] { null, "Cork" }, result.GroupValues[1]);
        Assert.Equal(new string?[] { "FTE", null }, result.GroupValues[2]);
    }

    [Fact]
    public async Task NoAggregation_ProducesNoGroupValues()
    {
        var plan = new DirectoryQueryPlan
        {
            Description = "no aggregation",
            Steps = { new DirectoryPlanStep { Step = 1, Name = "s1", Operation = "search" } },
            Projection = new ProjectionDefinition
            {
                RowStep = "s1",
                Columns = { new ProjectionColumn { Name = "Name", Attribute = "displayName" } },
            },
        };

        var result = await ExecuteAsync(plan, Record("Ann", "CWK"));

        Assert.Single(result.Data);
        Assert.Empty(result.GroupValues);
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
}
