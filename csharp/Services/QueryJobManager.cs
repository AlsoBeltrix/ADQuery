using System;
using System.Collections.Generic;
using System.IO;
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

    public async Task<string> CreateJobAsync(
        string userName,
        string query,
        string? context = null,
        int? requestedResultLimit = null,
        CancellationToken cancellationToken = default)
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
        await _queue.EnqueueAsync(jobId, cancellationToken);

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

        // Queue entries can outlive status transitions (for example, queued -> cancelled).
        // Only queued jobs should transition to running execution.
        if (job.Status == JobStatus.Cancelled)
        {
            _logger.LogInformation("Skipping execution for cancelled job {JobId}", jobId);
            return;
        }

        if (job.Status != JobStatus.Queued)
        {
            _logger.LogDebug("Skipping job {JobId} because status is {Status}", jobId, job.Status);
            return;
        }

        var jobCreatedAt = job.CreatedAt == default ? DateTime.UtcNow : job.CreatedAt;
        var userDirectory = QueryLogHelper.GetUserDirectory(job.UserName);
        var baseFileName = QueryLogHelper.BuildFileBaseName(job.UserName, jobCreatedAt);
        var logPath = Path.Combine(userDirectory, $"{baseFileName}.log");
        var outputPath = Path.Combine(userDirectory, $"{baseFileName}.csv");

        string? rawModelResponse = null;
        string? modelPlanJson = null;
        string? executedPlanJson = null;

        void WriteJobLog(
            bool success,
            int recordCount,
            IEnumerable<string>? warnings,
            string? errorMessage,
            string? overrideRaw = null,
            string? overrideModelPlan = null,
            string? overrideExecutedPlan = null)
        {
            try
            {
                QueryLogHelper.WriteQueryLog(
                    logPath,
                    DateTime.UtcNow,
                    job.JobId,
                    job.UserName,
                    job.Query,
                    job.Context,
                    success,
                    recordCount,
                    warnings,
                    errorMessage,
                    job.RequestedResultLimit,
                    success ? outputPath : null,
                    overrideRaw ?? rawModelResponse,
                    overrideModelPlan ?? modelPlanJson,
                    overrideExecutedPlan ?? executedPlanJson,
                    job.ModelUsed);
            }
            catch (Exception logEx)
            {
                _logger.LogWarning(logEx, "Failed to write log for job {JobId}", jobId);
            }
        }

        try
        {
            _store.UpdateStatus(jobId, JobStatus.Running);
            job.CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var jobToken = job.CancellationSource.Token;

            // Progress: Plan generation
            _store.UpdateProgress(jobId, new PlanProgressUpdate
            {
                NodesProcessed = 0,
                CurrentDepth = 0,
                EstimatedRemainingNodes = null,
                Phase = "generating-plan"
            });

            // Check for model override directive in context
            string? modelOverride = null;
            var contextToUse = job.Context;
            if (!string.IsNullOrWhiteSpace(contextToUse))
            {
                var forceModelMatch = System.Text.RegularExpressions.Regex.Match(
                    contextToUse,
                    @"\[FORCE_MODEL:\s*([^\]]+)\]");
                if (forceModelMatch.Success)
                {
                    modelOverride = forceModelMatch.Groups[1].Value.Trim();
                    // Remove the directive from context so it doesn't confuse the model
                    contextToUse = contextToUse.Replace(forceModelMatch.Value, "").Trim();
                    _logger.LogInformation("Job {JobId} using model override: {Model}", jobId, modelOverride);
                }
            }

            jobToken.ThrowIfCancellationRequested();

            // Generate plan with context and limit (same as sync endpoint)
            var planResponse = await claude.GenerateExecutionPlanAsync(
                job.Query,
                contextToUse,
                job.RequestedResultLimit,
                jobToken,
                modelOverride);

            // Track which model was actually used
            job.ModelUsed = planResponse.ModelUsed;

            rawModelResponse = planResponse.RawResponse;
            modelPlanJson = QueryLogHelper.SerializePlan(planResponse.Plan);

            if (!planResponse.Success || planResponse.Plan == null)
            {
                _store.UpdateStatus(jobId, JobStatus.Failed, planResponse.ErrorMessage ?? "Failed to generate plan");
                _logger.LogWarning("Job {JobId} plan generation failed: {Error}", jobId, planResponse.ErrorMessage);
                WriteJobLog(success: false, recordCount: 0, warnings: null, errorMessage: planResponse.ErrorMessage ?? "Failed to generate plan");
                return;
            }

            job.Plan = planResponse.Plan;
            _planPreprocessor.PrepareForExecution(job.Plan, job.RequestedResultLimit);
            executedPlanJson = QueryLogHelper.SerializePlan(job.Plan);

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
                WriteJobLog(success: false, recordCount: 0, warnings: null, errorMessage: errorMessage);
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
                WriteJobLog(success: false, recordCount: 0, warnings: result.Warnings, errorMessage: errorMessage);
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

            WriteJobLog(success: true, recordCount: result.Data.Count, warnings: result.Warnings, errorMessage: null);

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
            WriteJobLog(success: false, recordCount: 0, warnings: null, errorMessage: "Job cancelled");
        }
        catch (Exception ex)
        {
            _store.UpdateStatus(jobId, JobStatus.Failed, ex.Message);
            _logger.LogError(ex, "Job {JobId} failed with exception", jobId);
            WriteJobLog(success: false, recordCount: 0, warnings: null, errorMessage: ex.Message);
        }
    }

    public void CancelJob(string jobId)
    {
        var job = _store.GetJob(jobId);
        if (job != null)
        {
            if (job.Status == JobStatus.Queued)
            {
                _store.UpdateStatus(jobId, JobStatus.Cancelled, "Job cancelled before execution");
                _logger.LogInformation("Queued job {JobId} cancelled before execution", jobId);
                return;
            }

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
