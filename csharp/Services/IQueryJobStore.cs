using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Thread-safe storage for query job metadata and state.
/// </summary>
public interface IQueryJobStore
{
    QueryJob? GetJob(string jobId);
    void StoreJob(QueryJob job);
    void UpdateProgress(string jobId, PlanProgressUpdate progress);
    void UpdateStatus(string jobId, JobStatus status, string? errorMessage = null);
    /// <param name="answer">
    /// The Narrate answer (F04 Slice 2). Null when Narrate failed or was skipped; the job
    /// still completes. Set here rather than afterwards so a client that observes
    /// <see cref="JobStatus.Completed"/> never sees a job whose answer is still in flight.
    /// </param>
    /// <param name="resultArtifactPath">
    /// The completion-time artifact of record (F04 Slice 7). Set here, with the rest of the
    /// completed state, so a reader that observes <see cref="JobStatus.Completed"/> already
    /// sees the path — the artifact itself is written before this call. Null when the write
    /// failed; the job still completes.
    /// </param>
    /// <param name="resultIsIncomplete">
    /// True when the result stopped at a system limit (ci-or-1). Set here with the rest of the
    /// completed state so no reader can observe a completed job whose row count is a floor
    /// without also seeing that it is one.
    /// </param>
    void SetCompleted(string jobId, int totalRows, Dictionary<string, object>? aggregation, List<string> warnings, string? answer = null, string? resultArtifactPath = null, bool resultIsIncomplete = false);
    List<QueryJob> GetUserJobs(string userName);
    List<QueryJob> GetJobsByStatus(JobStatus status);
    bool RemoveJob(string jobId);
}
