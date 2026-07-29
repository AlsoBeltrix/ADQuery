using System.Collections.Generic;
using System.Linq;
using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Derives a deterministic <see cref="HeadlineResult"/> from a completed job's
/// plan shape and result (F01 Slice B1). Pure and side-effect free so the kind
/// precedence is table-testable in isolation. Async path only — the sync
/// <c>execute</c> endpoint is retired (SYNC-D1).
/// </summary>
public static class HeadlineClassifier
{
    /// <summary>
    /// DATA-D1 ceiling on how many grouped categories leave the server in a
    /// headline. Independent of the unbounded <c>PreviewRowCount</c> config.
    /// </summary>
    public const int MaxHeadlineGroups = 10;

    /// <summary>
    /// Classifies a completed job. Precedence is fixed and total:
    /// <list type="number">
    ///   <item>zero rows → <c>none</c></item>
    ///   <item>grouped aggregation present → <c>grouped</c> (bounded)</item>
    ///   <item>plan requested aggregation but no grouped payload (pure-count) → <c>count</c></item>
    ///   <item>exactly one row, no expansion/aggregation → <c>record</c></item>
    ///   <item>otherwise → <c>count</c></item>
    /// </list>
    /// </summary>
    /// <param name="plan">The executed plan (may be null for legacy jobs).</param>
    /// <param name="totalRows">Final result row count.</param>
    /// <param name="aggregation">
    /// The job's runtime aggregation dictionary. Null when the plan requested no
    /// aggregation or the result was empty.
    /// </param>
    /// <param name="firstRow">The first (only) result row, for the record kind.</param>
    public static HeadlineResult Classify(
        DirectoryQueryPlan? plan,
        int totalRows,
        IReadOnlyDictionary<string, object>? aggregation,
        IReadOnlyDictionary<string, object?>? firstRow)
    {
        // 1. Empty — zero rows, regardless of plan.
        if (totalRows <= 0)
        {
            return new HeadlineResult { Kind = HeadlineKind.None };
        }

        // 2. Grouped — the runtime aggregation carries grouped counts.
        var groups = ExtractGroups(aggregation);
        if (groups != null && groups.Count > 0)
        {
            return new HeadlineResult
            {
                Kind = HeadlineKind.Grouped,
                Count = totalRows,
                Groups = groups,
            };
        }

        // 3. The plan requested aggregation but no grouped payload reached here —
        //    a pure-count plan (empty group_by). The scalar count is the answer.
        if (plan?.Projection?.Aggregation != null)
        {
            return new HeadlineResult { Kind = HeadlineKind.Count, Count = totalRows };
        }

        // 4. Single-record — exactly one row and the plan is neither an
        //    expansion nor an aggregation.
        if (totalRows == 1 && firstRow != null && !HasExpansion(plan))
        {
            return new HeadlineResult { Kind = HeadlineKind.Record, Record = firstRow };
        }

        // 5. Multi-row (or single row from an expansion) — the count is the headline.
        return new HeadlineResult { Kind = HeadlineKind.Count, Count = totalRows };
    }

    /// <summary>
    /// Bounds the runtime <c>grouped_counts</c> to <see cref="MaxHeadlineGroups"/>
    /// with deterministic ordering (count descending, then key ascending).
    /// Returns null when no grouped counts are present.
    /// </summary>
    private static IReadOnlyList<HeadlineGroup>? ExtractGroups(
        IReadOnlyDictionary<string, object>? aggregation)
    {
        if (aggregation == null ||
            !aggregation.TryGetValue("grouped_counts", out var raw) ||
            raw is not IDictionary<string, int> counts ||
            counts.Count == 0)
        {
            return null;
        }

        return counts
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, System.StringComparer.Ordinal)
            .Take(MaxHeadlineGroups)
            .Select(kvp => new HeadlineGroup { Key = kvp.Key, Count = kvp.Value })
            .ToList();
    }

    /// <summary>
    /// True when any plan step is a membership/report expansion or is recursive —
    /// a <c>SizeLimit</c> or <c>ResultLimit</c> of 1 does not, by itself, prove a
    /// single subject (a seed step can fan out).
    /// </summary>
    private static bool HasExpansion(DirectoryQueryPlan? plan)
    {
        if (plan?.Steps == null)
        {
            return false;
        }

        return plan.Steps.Any(step =>
            step.Recursive ||
            step.Operation.Equals("expand_members", System.StringComparison.OrdinalIgnoreCase) ||
            step.Operation.Equals("expand_reports", System.StringComparison.OrdinalIgnoreCase));
    }
}
