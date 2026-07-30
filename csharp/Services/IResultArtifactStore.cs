using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// The completion-time artifact of record for a job's full result (F04 Slice 7, F04-D5).
/// <para>
/// Before this store existed, a completed result lived only in a 2h <c>IMemoryCache</c> entry
/// and the sole disk write happened *inside* a download, after that download had already read
/// the cache. The artifact inverts that: the full result is written atomically before a job is
/// marked <see cref="JobStatus.Completed"/>, and every reader — preview, single-record
/// headline, download, cross-turn reuse — reads it instead of holding a 40k-row set resident.
/// </para>
/// </summary>
public interface IResultArtifactStore
{
    /// <summary>
    /// Writes the full result atomically (temp file then move) and returns the artifact path.
    /// The caller must persist the returned path on the job before marking it completed —
    /// an artifact whose path was never recorded is an orphan.
    /// </summary>
    Task<string> WriteAsync(
        QueryJob job,
        PlanExecutionResult result,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads at most <paramref name="maxRows"/> rows, stopping as soon as it has them. Null
    /// reads every row. Returns null when the artifact is missing or unreadable, which callers
    /// treat exactly as they treated an expired cache entry.
    /// </summary>
    ResultArtifact? Read(string? artifactPath, int? maxRows = null);

    /// <summary>
    /// Reads the header line and nothing else (slice4r2-or-1): the row count, warnings,
    /// completeness, and the serialized plan, with <see cref="ResultArtifact.Rows"/> empty.
    /// <para>
    /// This is what whole-plan reuse needs to *reject* a candidate ancestor. Reading rows to
    /// decide a question the header answers costs one deserialization per row of a result the
    /// caller then discards, and a thread can hold twenty ancestors. Carrying the plan on the
    /// header is why the format is line-oriented; this is the method that spends it.
    /// </para>
    /// </summary>
    ResultArtifact? ReadHeader(string? artifactPath);

    /// <summary>
    /// Removes an artifact. Safe to call for a path that is already gone.
    /// </summary>
    void Delete(string? artifactPath);

    /// <summary>
    /// Removes interrupted-write temp files and artifacts no live job points at
    /// (f04-or-7). Called at startup, where the live set is whatever survived the restart.
    /// </summary>
    int SweepOrphans(IReadOnlySet<string> livePaths);

    /// <summary>
    /// True when the artifact volume has room for another result. Checked before a query is
    /// accepted so exhaustion surfaces as a refusal rather than a write that fails partway.
    /// </summary>
    bool HasRoomForAnotherResult();
}

/// <summary>
/// A result read back from its artifact. <see cref="Rows"/> holds only what the reader asked
/// for; <see cref="TotalRows"/> is always the full count from the artifact header, so a
/// bounded read still reports the real size.
/// </summary>
public sealed class ResultArtifact
{
    public required int TotalRows { get; init; }

    public required List<Dictionary<string, object?>> Rows { get; init; }

    /// <summary>
    /// Per-row <c>group_by</c> values, positional against <see cref="Rows"/> and empty when
    /// the plan requested no grouping — the same contract as
    /// <see cref="PlanExecutionResult.GroupValues"/>, so a reused artifact can rebuild a
    /// result the aggregation path accepts unchanged.
    /// </summary>
    public required List<IReadOnlyList<string?>> GroupValues { get; init; }

    public required List<string> Warnings { get; init; }

    /// <summary>
    /// Whether the recorded result stopped at a system limit (ci-or-1). Persisted with the
    /// rows because whole-plan reuse rebuilds a later turn's result from this artifact: a
    /// reused partial set is still partial, and dropping the fact here would give the second
    /// turn a confident answer the first turn correctly caveated.
    /// </summary>
    public bool ResultIsIncomplete { get; init; }

    /// <summary>
    /// The serialized plan that produced these rows, as written at completion. Whole-plan
    /// reuse compares against this rather than trusting in-memory job state.
    /// </summary>
    public string? PlanJson { get; init; }
}
