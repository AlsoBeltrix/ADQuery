using System.Collections.Generic;
using System.Linq;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// Guards F04 Slice 4's presentation rule: export is offered on every response that IS a
/// meaningful exportable artifact — a set or a table — and on nothing else (owner rule
/// 2026-07-28). Driven through the real <see cref="HeadlineClassifier"/> rather than
/// hand-built <see cref="HeadlineResult"/> literals, so a classifier change that moves a
/// plan shape between kinds is caught here too instead of the two drifting apart.
/// </summary>
public sealed class ExportAffordanceTests
{
    private static DirectoryQueryPlan SearchPlan() => new()
    {
        Steps = { new DirectoryPlanStep { Name = "s1", Operation = "search" } },
        Projection = new ProjectionDefinition { RowStep = "s1" },
    };

    private static DirectoryQueryPlan AggregationPlan(params string[] groupBy) => new()
    {
        Steps = { new DirectoryPlanStep { Name = "s1", Operation = "search" } },
        Projection = new ProjectionDefinition
        {
            RowStep = "s1",
            Aggregation = new AggregationDefinition { Count = true, GroupBy = groupBy.ToList() },
        },
    };

    private static Dictionary<string, object> GroupedAggregation(Dictionary<string, int> counts) =>
        new() { ["grouped_counts"] = counts };

    private static bool Decide(
        DirectoryQueryPlan? plan,
        int totalRows,
        IReadOnlyDictionary<string, object>? aggregation = null,
        IReadOnlyDictionary<string, object?>? firstRow = null)
    {
        var headline = HeadlineClassifier.Classify(plan, totalRows, aggregation, firstRow);
        return ExportAffordance.HasExportableArtifact(plan, totalRows, headline);
    }

    [Fact]
    public void GroupedAnswer_Exports_TheDistributionIsTheArtifact()
    {
        var exportable = Decide(
            AggregationPlan("department"),
            120,
            GroupedAggregation(new Dictionary<string, int> { ["Sales"] = 90, ["IT"] = 30 }));

        Assert.True(exportable);
    }

    [Fact]
    public void SingleBucketGroupedAnswer_StillExports()
    {
        // One bucket is still a distribution table, not a one-line answer.
        var exportable = Decide(
            AggregationPlan("department"),
            12,
            GroupedAggregation(new Dictionary<string, int> { ["Sales"] = 12 }));

        Assert.True(exportable);
    }

    [Fact]
    public void MultiRowListAnswer_Exports_TheRowsAreTheArtifact()
    {
        Assert.True(Decide(SearchPlan(), 42));
    }

    [Fact]
    public void PureCountOverManyRecords_Exports()
    {
        // F05-D1: export turns on how many RECORDS the result holds, never on how many lines
        // the answer occupies. "How many managers in Thailand" answers 43, and those 43 rows
        // are exactly what the user asks for next — the count summarises an exportable set
        // rather than replacing one. This previously asserted the opposite.
        Assert.True(Decide(AggregationPlan(), 27000));
    }

    [Fact]
    public void PureCountOverASingleRecord_DoesNotExport()
    {
        // The overshoot guard for F05-D1. One record is one record whether or not the plan
        // asked for a count of it, and F04-D2 was right about that case.
        Assert.False(Decide(AggregationPlan(), 1));
    }

    [Fact]
    public void SingleRecordAnswer_DoesNotExport()
    {
        var firstRow = new Dictionary<string, object?> { ["Name"] = "Ada Lovelace" };

        Assert.False(Decide(SearchPlan(), 1, firstRow: firstRow));
    }

    [Fact]
    public void SingleRowWithoutTheRecordItself_DoesNotExport()
    {
        // The row never reached the classifier (cache miss), so the headline degrades to a
        // count of 1. It is still one record — not a set — and must not offer a download.
        Assert.False(Decide(SearchPlan(), 1));
    }

    [Fact]
    public void EmptyAnswer_DoesNotExport()
    {
        Assert.False(Decide(SearchPlan(), 0));
        Assert.False(Decide(AggregationPlan("department"), 0));
    }

    [Fact]
    public void MissingPlan_WithRows_Exports()
    {
        // Legacy jobs carry no plan. Rows are still rows; the artifact exists.
        Assert.True(Decide(plan: null, totalRows: 42));
    }
}
