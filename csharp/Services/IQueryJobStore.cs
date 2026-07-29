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
    void SetCompleted(string jobId, int totalRows, Dictionary<string, object>? aggregation, List<string> warnings, string resultsCacheKey, string? answer = null);
    List<QueryJob> GetUserJobs(string userName);
    List<QueryJob> GetJobsByStatus(JobStatus status);
    bool RemoveJob(string jobId);
}
