using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
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
    private readonly IPlanPreprocessor _planPreprocessor;
    private readonly int _maxJobsPerUser;

    public QueryJobManager(
        IQueryJobStore store,
        IQueryJobQueue queue,
        ILogger<QueryJobManager> logger,
        IPlanPreprocessor planPreprocessor,
        IConfiguration configuration)
    {
        _store = store;
        _queue = queue;
        _logger = logger;
        _planPreprocessor = planPreprocessor;
        _maxJobsPerUser = Math.Max(0, configuration.GetValue<int>("Jobs:MaxJobsPerUser", 0));
    }

    public string CreateJob(string userName, string query, string? context = null, int? requestedResultLimit = null)
    {
        if (_maxJobsPerUser > 0)
        {
            var activeJobs = _store.GetUserJobs(userName)
                .Count(job => job.Status == JobStatus.Queued || job.Status == JobStatus.Running);

            if (activeJobs >= _maxJobsPerUser)
            {
                throw new InvalidOperationException($"Maximum concurrent async jobs reached (limit {_maxJobsPerUser}). Cancel an existing job before starting a new one.");
            }
        }

        var jobId = Guid.NewGuid().ToString();
        var job = new QueryJob
        {
            JobId = jobId,
            UserName = userName,
            Query = query,
            Context = context,
            RequestedResultLimit = requestedResultLimit,
            Status = JobStatus.Queued,
            CreatedAt = DateTime.UtcNow
        };

        _store.StoreJob(job);
        _queue.EnqueueAsync(jobId).AsTask().Wait();

        _logger.LogInformation("Job {JobId} created for user {UserName}", jobId, userName);

        return jobId;
    }

    public async Task EnqueueJobAsync(QueryJob job, string? forceModel = null)
    {
        if (_maxJobsPerUser > 0)
        {
            var activeJobs = _store.GetUserJobs(job.UserName)
                .Count(j => j.Status == JobStatus.Queued || j.Status == JobStatus.Running);

            if (activeJobs >= _maxJobsPerUser)
            {
                throw new InvalidOperationException($"Maximum concurrent async jobs reached (limit {_maxJobsPerUser}). Cancel an existing job before starting a new one.");
            }
        }

        // Store model override in job context if provided
        if (!string.IsNullOrWhiteSpace(forceModel))
        {
            job.Context = (job.Context ?? "") + $"\n[FORCE_MODEL: {forceModel}]";
        }

        _store.StoreJob(job);
        await _queue.EnqueueAsync(job.JobId);

        _logger.LogInformation("Job {JobId} enqueued for user {UserName} {Model}",
            job.JobId,
            job.UserName,
            string.IsNullOrWhiteSpace(forceModel) ? "" : $"with model {forceModel}");
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

            // Progress: Plan generation
            _store.UpdateProgress(jobId, new PlanProgressUpdate
            {
                NodesProcessed = 0,
                CurrentDepth = 0,
                EstimatedRemainingNodes = null,
                Phase = "generating-plan"
            });

            // Generate plan with context and limit (same as sync endpoint)
            var planResponse = await claude.GenerateExecutionPlanAsync(
                job.Query,
                job.Context,
                job.RequestedResultLimit,
                cancellationToken);

            if (!planResponse.Success || planResponse.Plan == null)
            {
                _store.UpdateStatus(jobId, JobStatus.Failed, planResponse.ErrorMessage ?? "Failed to generate plan");
                _logger.LogWarning("Job {JobId} plan generation failed: {Error}", jobId, planResponse.ErrorMessage);
                return;
            }

            job.Plan = planResponse.Plan;
            _planPreprocessor.PrepareForExecution(job.Plan, job.RequestedResultLimit);

            // Progress: Validating
            _store.UpdateProgress(jobId, new PlanProgressUpdate
            {
                NodesProcessed = 0,
                CurrentDepth = 0,
                EstimatedRemainingNodes = null,
                Phase = "validating"
            });

            // Validate
            var validation = await validator.ValidateSecurityAsync(job.Plan);
            if (!validation.OperationsValid || validation.SecurityErrors.Any())
            {
                var errorMessage = string.Join("; ", validation.SecurityErrors);
                _store.UpdateStatus(jobId, JobStatus.Failed, errorMessage);
                _logger.LogWarning("Job {JobId} validation failed: {Errors}", jobId, errorMessage);
                return;
            }

            // Progress: Executing
            _store.UpdateProgress(jobId, new PlanProgressUpdate
            {
                NodesProcessed = 0,
                CurrentDepth = 0,
                EstimatedRemainingNodes = null,
                Phase = "executing"
            });

            // Execute with progress callback
            var progress = new Progress<PlanProgressUpdate>(update =>
            {
                // Update progress on every report (throttling removed for better UX)
                _store.UpdateProgress(jobId, update);
                _logger.LogDebug(
                    "Job {JobId} progress: depth={Depth}, nodes={Nodes}, estimated={Estimated}, phase={Phase}",
                    jobId, update.CurrentDepth, update.NodesProcessed, update.EstimatedRemainingNodes, update.Phase);
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

                // If projection columns exactly match group_by fields, user wants unique values as data
                var projectionColumns = job.Plan.Projection.Columns.Select(c => c.Attribute).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
                var groupByFields = job.Plan.Projection.Aggregation.GroupBy;

                if (projectionColumns.Count == groupByFields.Count &&
                    projectionColumns.All(col => groupByFields.Contains(col, StringComparer.OrdinalIgnoreCase)))
                {
                    // Transform aggregation into data rows
                    var counts = aggregation["grouped_counts"] as Dictionary<string, int>;
                    if (counts != null)
                    {
                        var uniqueRows = new List<Dictionary<string, object?>>();

                        foreach (var (key, count) in counts.OrderByDescending(kvp => kvp.Value))
                        {
                            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

                            if (groupByFields.Count == 1)
                            {
                                row[groupByFields[0]] = key;
                                row["Count"] = count;
                            }
                            else
                            {
                                var keyParts = key.Split('|');
                                for (int i = 0; i < groupByFields.Count && i < keyParts.Length; i++)
                                {
                                    row[groupByFields[i]] = keyParts[i];
                                }
                                row["Count"] = count;
                            }

                            uniqueRows.Add(row);
                        }

                        result.Data.Clear();
                        result.Data.AddRange(uniqueRows);

                        // Clear aggregation since it's now the data itself (avoid duplication in downloads)
                        aggregation = null;

                        _logger.LogInformation(
                            "Job {JobId}: Projection columns match group_by fields - returning {Count} unique values as data",
                            jobId,
                            uniqueRows.Count);
                    }
                }
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
