using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Manages query job lifecycle: creation, queuing, and cleanup.
/// Execution happens in QueryJobExecutorHostedService with scoped dependencies.
/// </summary>
public class QueryJobManager : IQueryJobManager
{
    private readonly IQueryJobStore _store;
    private readonly IQueryJobQueue _queue;
    private readonly ILogger<QueryJobManager> _logger;

    public QueryJobManager(
        IQueryJobStore store,
        IQueryJobQueue queue,
        ILogger<QueryJobManager> logger)
    {
        _store = store;
        _queue = queue;
        _logger = logger;
    }

    public string CreateJob(string userName, string query)
    {
        var jobId = Guid.NewGuid().ToString();
        var job = new QueryJob
        {
            JobId = jobId,
            UserName = userName,
            Query = query,
            Status = JobStatus.Queued,
            CreatedAt = DateTime.UtcNow
        };

        _store.StoreJob(job);
        _queue.EnqueueAsync(jobId).AsTask().Wait();

        _logger.LogInformation("Job {JobId} created for user {UserName}", jobId, userName);

        return jobId;
    }

    public QueryJob? GetJob(string jobId)
    {
        return _store.GetJob(jobId);
    }

    public List<QueryJob> GetQueuedJobs()
    {
        return _store.GetJobsByStatus(JobStatus.Queued);
    }

    public async Task ExecuteJobWithServicesAsync(
        string jobId,
        IClaudeService claude,
        IPlanValidator validator,
        IDirectoryPlanExecutor executor,
        IMemoryCache cache,
        CancellationToken cancellationToken)
    {
        var job = _store.GetJob(jobId);
        if (job == null)
        {
            _logger.LogWarning("Job {JobId} not found", jobId);
            return;
        }

        try
        {
            _store.UpdateStatus(jobId, JobStatus.Running);
            job.CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Generate plan
            var planResponse = await claude.GenerateExecutionPlanAsync(job.Query);
            if (!planResponse.Success || planResponse.Plan == null)
            {
                _store.UpdateStatus(jobId, JobStatus.Failed, planResponse.ErrorMessage ?? "Failed to generate plan");
                _logger.LogWarning("Job {JobId} plan generation failed: {Error}", jobId, planResponse.ErrorMessage);
                return;
            }

            job.Plan = planResponse.Plan;

            // Validate
            var validation = await validator.ValidateSecurityAsync(job.Plan);
            if (!validation.OperationsValid || validation.SecurityErrors.Any())
            {
                var errorMessage = string.Join("; ", validation.SecurityErrors);
                _store.UpdateStatus(jobId, JobStatus.Failed, errorMessage);
                _logger.LogWarning("Job {JobId} validation failed: {Errors}", jobId, errorMessage);
                return;
            }

            // Execute with progress callback
            var progress = new Progress<PlanProgressUpdate>(update =>
            {
                // Throttle updates: only publish if depth changed or nodes increased by 250+
                var currentJob = _store.GetJob(jobId);
                if (currentJob != null)
                {
                    var depthChanged = update.CurrentDepth != currentJob.CurrentDepth;
                    var significantNodeIncrease = update.NodesProcessed - currentJob.NodesProcessed >= 250;

                    if (depthChanged || significantNodeIncrease)
                    {
                        _store.UpdateProgress(jobId, update);
                        _logger.LogDebug(
                            "Job {JobId} progress: depth={Depth}, nodes={Nodes}, estimated={Estimated}",
                            jobId, update.CurrentDepth, update.NodesProcessed, update.EstimatedRemainingNodes);
                    }
                }
            });

            var result = await executor.ExecutePlanAsync(
                job.Plan,
                progress,
                job.CancellationSource.Token);

            if (!result.Success)
            {
                var errorMessage = string.Join("; ", result.Errors);
                _store.UpdateStatus(jobId, JobStatus.Failed, errorMessage);
                _logger.LogWarning("Job {JobId} execution failed: {Errors}", jobId, errorMessage);
                return;
            }

            // Store results in cache
            var resultsCacheKey = $"job_results_{jobId}";
            cache.Set(resultsCacheKey, result, TimeSpan.FromHours(2));

            // Extract aggregation if present
            Dictionary<string, object>? aggregation = null;
            if (job.Plan.Projection?.Aggregation != null && result.Data.Any())
            {
                aggregation = ComputeAggregation(result.Data, job.Plan.Projection.Aggregation);
            }

            _store.SetCompleted(
                jobId,
                result.Data.Count,
                aggregation,
                result.Warnings,
                resultsCacheKey);

            _logger.LogInformation(
                "Job {JobId} completed: {Rows} rows in {Duration}s",
                jobId,
                result.Data.Count,
                (DateTime.UtcNow - job.StartedAt.GetValueOrDefault()).TotalSeconds);
        }
        catch (OperationCanceledException)
        {
            _store.UpdateStatus(jobId, JobStatus.Cancelled);
            _logger.LogInformation("Job {JobId} cancelled", jobId);
        }
        catch (Exception ex)
        {
            _store.UpdateStatus(jobId, JobStatus.Failed, ex.Message);
            _logger.LogError(ex, "Job {JobId} failed with exception", jobId);
        }
    }

    public void CancelJob(string jobId)
    {
        var job = _store.GetJob(jobId);
        if (job != null)
        {
            job.CancellationSource?.Cancel();
            _logger.LogInformation("Job {JobId} cancellation requested", jobId);
        }
    }

    public List<QueryJob> GetUserJobs(string userName)
    {
        return _store.GetUserJobs(userName);
    }

    public void CleanupCompletedJobs(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var completedJobs = _store.GetJobsByStatus(JobStatus.Completed)
            .Where(j => j.CompletedAt.HasValue && j.CompletedAt.Value < cutoff)
            .ToList();

        foreach (var job in completedJobs)
        {
            _store.RemoveJob(job.JobId);
            _logger.LogDebug("Cleaned up completed job {JobId}", job.JobId);
        }
    }

    private Dictionary<string, object> ComputeAggregation(
        List<Dictionary<string, object?>> rows,
        AggregationDefinition aggregation)
    {
        var result = new Dictionary<string, object>();

        if (aggregation.Count && aggregation.GroupBy.Any())
        {
            var grouped = rows
                .GroupBy(row =>
                {
                    var keys = aggregation.GroupBy
                        .Select(field =>
                        {
                            row.TryGetValue(field, out var value);
                            return value?.ToString() ?? "(empty)";
                        })
                        .ToList();
                    return string.Join("|", keys);
                })
                .ToDictionary(g => g.Key, g => g.Count());

            result["grouped_counts"] = grouped;
        }

        return result;
    }
}
