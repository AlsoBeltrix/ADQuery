using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using Microsoft.Extensions.Caching.Memory;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Service for managing query job lifecycle.
/// </summary>
public interface IQueryJobManager
{
    Task<string> CreateJobAsync(
        string userName,
        string query,
        string? context = null,
        int? requestedResultLimit = null,
        CancellationToken cancellationToken = default);
    Task EnqueueJobAsync(QueryJob job, string? forceModel = null);
    QueryJob? GetJob(string jobId);
    void CancelJob(string jobId);
    List<QueryJob> GetUserJobs(string userName);
    List<QueryJob> GetQueuedJobs();
    void CleanupCompletedJobs(TimeSpan olderThan);

    // Called by hosted service with scoped dependencies
    Task ExecuteJobWithServicesAsync(
        string jobId,
        IClaudeService claude,
        IPlanValidator validator,
        IDirectoryPlanExecutor executor,
        IMemoryCache cache,
        CancellationToken cancellationToken);
}
