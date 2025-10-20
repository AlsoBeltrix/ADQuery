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
    void SetCompleted(string jobId, int totalRows, Dictionary<string, object>? aggregation, List<string> warnings, string resultsCacheKey);
    List<QueryJob> GetUserJobs(string userName);
    List<QueryJob> GetJobsByStatus(JobStatus status);
    bool RemoveJob(string jobId);
}
