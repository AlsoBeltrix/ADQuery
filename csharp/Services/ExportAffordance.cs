using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Decides whether a completed job has a **meaningful exportable artifact** — a set or a
/// table worth putting in a file (owner rule 2026-07-28, F04 Slice 4). Export is a permanent
/// unobtrusive affordance on those answers and absent everywhere else: a one-line answer and
/// a single-record answer have nothing a file adds.
/// <para>
/// Computed server-side because the question is about plan shape, not about what the browser
/// happens to be displaying. Pure and side-effect free so the rule is table-testable, and it
/// reads the same inputs as <see cref="HeadlineClassifier"/> so the two cannot drift into
/// disagreeing about what the answer is.
/// </para>
/// </summary>
public static class ExportAffordance
{
    /// <summary>
    /// True when the job's answer is a set or table. By headline kind
    /// (<see cref="HeadlineClassifier.Classify"/> assigns exactly one):
    /// <list type="bullet">
    ///   <item><c>grouped</c> → yes: the value+count distribution is the artifact (F04-D2).</item>
    ///   <item><c>none</c> → no: zero rows.</item>
    ///   <item><c>record</c> → no: a single record is not a set.</item>
    ///   <item><c>count</c> → depends. A *pure-count* plan (the plan asked for an aggregation
    ///     and got no grouped payload) answers with one number and exports nothing. A plan that
    ///     asked for no aggregation lands on <c>count</c> because it returned rows — those rows
    ///     are the artifact, provided there is more than one.</item>
    /// </list>
    /// </summary>
    public static bool HasExportableArtifact(
        DirectoryQueryPlan? plan,
        int totalRows,
        HeadlineResult headline)
    {
        if (totalRows <= 0)
        {
            return false;
        }

        return headline.Kind switch
        {
            HeadlineKind.Grouped => true,
            HeadlineKind.Record => false,
            HeadlineKind.Count => plan?.Projection?.Aggregation == null && totalRows > 1,
            _ => false,
        };
    }
}
