using System.Collections.Generic;
using System.Linq;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// Guards F01 Slice B1: the headline classifier assigns exactly one deterministic
/// kind per completed job, with fixed precedence, and bounds grouped payloads per
/// DATA-D1. Table-driven across every kind including the empty and distinct-list
/// cases that carry no aggregation payload.
/// </summary>
public sealed class HeadlineClassifierTests
{
    // --- Plan builders that reproduce the real plan shapes B1 classifies. ---

    private static DirectoryQueryPlan SearchPlan() => new()
    {
        Steps = { new DirectoryPlanStep { Name = "s1", Operation = "search" } },
        Projection = new ProjectionDefinition { RowStep = "s1" },
    };

    private static DirectoryQueryPlan ExpansionPlan() => new()
    {
        Steps =
        {
            new DirectoryPlanStep { Name = "s1", Operation = "search" },
            new DirectoryPlanStep { Name = "s2", Operation = "expand_members", Recursive = true },
        },
        Projection = new ProjectionDefinition { RowStep = "s2" },
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

    // --- One case per headline kind (the classifier's total precedence). ---

    [Fact]
    public void ZeroRows_IsNone_RegardlessOfPlan()
    {
        var result = HeadlineClassifier.Classify(AggregationPlan("department"), 0, null, null);

        Assert.Equal(HeadlineKind.None, result.Kind);
        Assert.Null(result.Count);
        Assert.Null(result.Record);
        Assert.Null(result.Groups);
    }

    [Fact]
    public void GroupedAggregationPresent_IsGrouped()
    {
        var agg = GroupedAggregation(new Dictionary<string, int> { ["Dublin"] = 7, ["Cork"] = 3 });

        var result = HeadlineClassifier.Classify(AggregationPlan("city"), 10, agg, null);

        Assert.Equal(HeadlineKind.Grouped, result.Kind);
        Assert.Equal(10, result.Count);
        Assert.NotNull(result.Groups);
        // Deterministic order: count descending.
        Assert.Equal("Dublin", result.Groups![0].Key);
        Assert.Equal(7, result.Groups[0].Count);
        Assert.Equal("Cork", result.Groups[1].Key);
    }

    [Fact]
    public void DistinctList_ClearedAggregation_IsCount()
    {
        // Distinct-list transform fired: aggregation was cleared (null) but the
        // plan still requested aggregation; the rows are the answer.
        var result = HeadlineClassifier.Classify(AggregationPlan("title"), 42, null, null);

        Assert.Equal(HeadlineKind.Count, result.Kind);
        Assert.Equal(42, result.Count);
        Assert.Null(result.Groups);
    }

    [Fact]
    public void PureCount_EmptyGroupBy_IsCount()
    {
        // Aggregation requested with an empty group_by → scalar count.
        var result = HeadlineClassifier.Classify(AggregationPlan(), 128, null, null);

        Assert.Equal(HeadlineKind.Count, result.Kind);
        Assert.Equal(128, result.Count);
    }

    [Fact]
    public void SingleRow_NonExpansion_IsRecord()
    {
        var row = new Dictionary<string, object?> { ["displayName"] = "Ada Lovelace", ["enabled"] = false };

        var result = HeadlineClassifier.Classify(SearchPlan(), 1, null, row);

        Assert.Equal(HeadlineKind.Record, result.Kind);
        Assert.NotNull(result.Record);
        Assert.Equal("Ada Lovelace", result.Record!["displayName"]);
        Assert.Null(result.Count);
    }

    [Fact]
    public void SingleRow_FromExpansion_IsCount_NotRecord()
    {
        // A one-row result of a membership/recursive expansion is not a single
        // subject; SizeLimit/ResultLimit of 1 can seed a fan-out.
        var row = new Dictionary<string, object?> { ["displayName"] = "Only Member" };

        var result = HeadlineClassifier.Classify(ExpansionPlan(), 1, null, row);

        Assert.Equal(HeadlineKind.Count, result.Kind);
        Assert.Equal(1, result.Count);
        Assert.Null(result.Record);
    }

    [Fact]
    public void MultiRow_NoAggregation_IsCount()
    {
        var result = HeadlineClassifier.Classify(SearchPlan(), 57, null, null);

        Assert.Equal(HeadlineKind.Count, result.Kind);
        Assert.Equal(57, result.Count);
        Assert.Null(result.Record);
    }

    // --- DATA-D1 bounding of the grouped payload. ---

    [Fact]
    public void GroupedPayload_IsBoundedAndDeterministicallyOrdered()
    {
        // 15 categories; the ceiling is MaxHeadlineGroups (10).
        var counts = new Dictionary<string, int>();
        for (var i = 0; i < 15; i++)
        {
            counts[$"cat{i:D2}"] = i; // ascending counts 0..14
        }

        var result = HeadlineClassifier.Classify(
            AggregationPlan("cat"), 105, GroupedAggregation(counts), null);

        Assert.Equal(HeadlineKind.Grouped, result.Kind);
        Assert.NotNull(result.Groups);
        Assert.Equal(HeadlineClassifier.MaxHeadlineGroups, result.Groups!.Count);
        // Top by count descending: cat14 (14) first, cat05 (5) last of the top 10.
        Assert.Equal("cat14", result.Groups[0].Key);
        Assert.Equal(14, result.Groups[0].Count);
        Assert.Equal("cat05", result.Groups[9].Key);
    }

    // --- Non-vacuity: precedence actually discriminates. If the classifier were
    //     forced to a single branch, at least one of these expectations breaks. ---

    [Fact]
    public void Precedence_IsTotalAndDistinct_AcrossKinds()
    {
        var agg = GroupedAggregation(new Dictionary<string, int> { ["x"] = 1 });
        var row = new Dictionary<string, object?> { ["a"] = 1 };

        Assert.Equal(HeadlineKind.None, HeadlineClassifier.Classify(SearchPlan(), 0, agg, row).Kind);
        Assert.Equal(HeadlineKind.Grouped, HeadlineClassifier.Classify(AggregationPlan("g"), 3, agg, row).Kind);
        Assert.Equal(HeadlineKind.Count, HeadlineClassifier.Classify(AggregationPlan("g"), 3, null, row).Kind);
        Assert.Equal(HeadlineKind.Record, HeadlineClassifier.Classify(SearchPlan(), 1, null, row).Kind);
        Assert.Equal(HeadlineKind.Count, HeadlineClassifier.Classify(SearchPlan(), 9, null, null).Kind);
    }
}
