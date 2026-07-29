using System.Collections.Generic;
using System.Linq;
using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Reduces a settled grouped aggregation to the scalars in <see cref="DistributionSummary"/>
/// (F04 Slice 2, f04-or-3). Pure and side-effect free so the shape classification is
/// table-testable. Returns null for any result that is not a grouped distribution — a
/// scalar count or single record has no distribution to describe.
/// </summary>
public static class DistributionSummarizer
{
    /// <summary>
    /// The placeholder <c>ComputeAggregation</c> substitutes for an absent or blank group
    /// value. A bucket counts as blank only when every one of its <c>group_by</c>
    /// components is this placeholder; a partially-populated composite is a real bucket.
    /// </summary>
    private const string EmptyBucket = "(empty)";

    public static DistributionSummary? Summarize(
        IReadOnlyDictionary<string, object>? aggregation,
        int groupByFieldCount,
        int totalRows)
    {
        if (aggregation == null ||
            !aggregation.TryGetValue("grouped_counts", out var raw) ||
            raw is not IDictionary<string, int> counts ||
            counts.Count == 0)
        {
            return null;
        }

        var fieldCount = groupByFieldCount < 1 ? 1 : groupByFieldCount;

        return new DistributionSummary
        {
            // The bucket counts are authoritative for the distribution; totalRows comes
            // from the job so a truncated or limited result still reports its own total.
            TotalRows = totalRows,
            DistinctBuckets = counts.Count,
            SingletonBuckets = counts.Values.Count(count => count == 1),
            BlankRows = counts
                .Where(pair => IsBlankKey(pair.Key, fieldCount))
                .Sum(pair => pair.Value),
        };
    }

    private static bool IsBlankKey(string key, int fieldCount)
        => GroupKey.Decompose(key, fieldCount).All(component => component == EmptyBucket);
}
