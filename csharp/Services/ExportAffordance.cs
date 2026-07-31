using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Decides whether a completed job has a **meaningful exportable artifact** — a set or a
/// table worth putting in a file (owner rule 2026-07-28, F04 Slice 4). Export is a permanent
/// unobtrusive affordance on those answers and absent everywhere else.
/// <para>
/// The test is how many **records the result holds**, never how many lines the answer
/// occupies (F05-D1). "Who's the CEO" is one record and exports nothing: the answer on screen
/// is the whole result. "How many managers in Thailand" is a one-line answer over a
/// many-record result, and those records are exactly what the user reaches for next — the
/// count summarises an exportable set rather than replacing one.
/// </para>
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
    ///   <item><c>count</c> → yes when the result holds more than one record, whether the count
    ///     came from a pure-count aggregation or from a plan that simply returned rows. The
    ///     records are the artifact either way (F05-D1). A single-record result is excluded by
    ///     the same <c>totalRows &gt; 1</c> test, so the case F04-D2 got right needs no
    ///     separate check.</item>
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
            HeadlineKind.Count => totalRows > 1,
            _ => false,
        };
    }
}
