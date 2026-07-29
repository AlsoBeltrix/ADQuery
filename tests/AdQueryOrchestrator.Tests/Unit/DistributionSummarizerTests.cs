using System.Collections.Generic;
using AdQuery.Orchestrator.Services;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F04 Slice 2 guard (f04-or-3). The distribution scalars are computed over the whole
/// aggregation, not the headline's top ten, which is the only reason they can express
/// near-uniqueness at all.
/// </summary>
public sealed class DistributionSummarizerTests
{
    private static Dictionary<string, object> Counts(Dictionary<string, int> counts)
        => new() { ["grouped_counts"] = counts };

    [Fact]
    public void CountsEveryBucket_NotOnlyTheHeadlineTopTen()
    {
        var counts = new Dictionary<string, int> { ["big"] = 100 };
        for (var i = 0; i < 50; i++)
        {
            counts[$"one-{i}"] = 1;
        }

        var summary = DistributionSummarizer.Summarize(Counts(counts), 1, totalRows: 150);

        Assert.NotNull(summary);
        Assert.Equal(51, summary!.DistinctBuckets);
        Assert.Equal(50, summary.SingletonBuckets);
        Assert.Equal(150, summary.TotalRows);
        Assert.Equal(0, summary.BlankRows);
    }

    [Fact]
    public void BlankRows_CountTheEmptyBucket()
    {
        var summary = DistributionSummarizer.Summarize(
            Counts(new Dictionary<string, int> { ["Finance"] = 3, ["(empty)"] = 7 }), 1, totalRows: 10);

        Assert.Equal(7, summary!.BlankRows);
        Assert.Equal(2, summary.DistinctBuckets);
    }

    [Fact]
    public void CompositeKey_IsBlankOnlyWhenEveryComponentIsBlank()
    {
        // A partially-populated composite is a real bucket, not a blank one: reporting it
        // as blank would understate how much of the directory actually carries a value.
        var bothBlank = GroupKey.Compose(["(empty)", "(empty)"]);
        var onePopulated = GroupKey.Compose(["Finance", "(empty)"]);

        var summary = DistributionSummarizer.Summarize(
            Counts(new Dictionary<string, int> { [bothBlank] = 4, [onePopulated] = 6 }),
            groupByFieldCount: 2,
            totalRows: 10);

        Assert.Equal(4, summary!.BlankRows);
        Assert.Equal(2, summary.DistinctBuckets);
    }

    [Fact]
    public void NonGroupedResult_HasNoDistribution()
    {
        Assert.Null(DistributionSummarizer.Summarize(null, 1, 5));
        Assert.Null(DistributionSummarizer.Summarize(new Dictionary<string, object>(), 1, 5));
        Assert.Null(DistributionSummarizer.Summarize(
            Counts(new Dictionary<string, int>()), 1, 5));
    }
}
