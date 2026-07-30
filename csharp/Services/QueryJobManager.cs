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
    private readonly IFollowUpContextEnforcer _followUpContextEnforcer;
    private readonly IAnswerReductionBuilder _answerReductionBuilder;
    private readonly IResultArtifactStore _resultArtifacts;
    private readonly int _maxJobsPerUser;

    // Server-generated internal control directive appended to a job's context to force a
    // model on the retry-with-alternate-model path; stripped before model transmission
    // and re-stripped before re-append so repeated retries do not chain directives.
    private static readonly System.Text.RegularExpressions.Regex ForceModelDirective =
        new(@"\[FORCE_MODEL:\s*([^\]]+)\]", System.Text.RegularExpressions.RegexOptions.Compiled);

    public QueryJobManager(
        IQueryJobStore store,
        IQueryJobQueue queue,
        ILogger<QueryJobManager> logger,
        IPlanPreprocessor planPreprocessor,
        IFollowUpContextEnforcer followUpContextEnforcer,
        IAnswerReductionBuilder answerReductionBuilder,
        IResultArtifactStore resultArtifacts,
        IConfiguration configuration)
    {
        _store = store;
        _queue = queue;
        _logger = logger;
        _planPreprocessor = planPreprocessor;
        _followUpContextEnforcer = followUpContextEnforcer;
        _answerReductionBuilder = answerReductionBuilder;
        _resultArtifacts = resultArtifacts;
        _maxJobsPerUser = Math.Max(0, configuration.GetValue<int>("Jobs:MaxJobsPerUser", 0));
    }

    public async Task<string> CreateJobAsync(
        string userName,
        string query,
        string? context = null,
        int? requestedResultLimit = null,
        string? previousJobId = null,
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

        // F01 Slice C1 (FOLLOWUP-D1): the authoritative follow-up-context bound, applied
        // before the job is persisted, logged, or handed to model transmission. An
        // over-cap opaque context is dropped whole (fail-closed) — never sent as a
        // fragment — regardless of what the client supplied.
        var boundedContext = _followUpContextEnforcer.EnforceStored(context);

        var jobId = Guid.NewGuid().ToString();
        var job = new QueryJob
        {
            JobId = jobId,
            UserName = userName,
            Query = query,
            Context = boundedContext,
            PreviousJobId = previousJobId,
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

        // F01 Slice C1 (FOLLOWUP-D1): this is the second client-reachable persistence
        // path (the retry-with-alternate-model endpoint), so it enforces the same
        // authoritative byte cap as CreateJobAsync. First strip any prior FORCE_MODEL
        // directive so repeated retries neither chain directives (unbounded growth) nor
        // let a stale directive count toward the cap; then bound the user context
        // (fail-closed: dropped whole if over cap). The FORCE_MODEL directive is
        // server-generated internal control metadata (stripped before model transmission
        // in ExecuteJobWithServicesAsync), so it is appended after enforcement.
        var boundedContext = _followUpContextEnforcer.EnforceStored(StripForceModelDirective(job.Context));

        if (!string.IsNullOrWhiteSpace(forceModel))
        {
            var directive = $"[FORCE_MODEL: {forceModel}]";
            boundedContext = string.IsNullOrEmpty(boundedContext)
                ? directive
                : boundedContext + "\n" + directive;
        }

        job.Context = boundedContext;

        _store.StoreJob(job);
        await _queue.EnqueueAsync(job.JobId);

        _logger.LogInformation("Job {JobId} enqueued for user {UserName} {Model}",
            job.JobId,
            job.UserName,
            string.IsNullOrWhiteSpace(forceModel) ? "" : $"with model {forceModel}");
    }

    // Removes any FORCE_MODEL directive (and surrounding whitespace) from a context so the
    // append path never chains directives and a stale directive never counts toward the cap.
    private static string? StripForceModelDirective(string? context)
    {
        if (string.IsNullOrEmpty(context))
        {
            return context;
        }

        var stripped = ForceModelDirective.Replace(context, "").Trim();
        return stripped.Length == 0 ? null : stripped;
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
                var forceModelMatch = ForceModelDirective.Match(contextToUse);
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

            // F04 Slice 7 (F04-D5): the only optimization. A turn whose complete plan is
            // byte-identical to an earlier turn's in the same thread reuses that turn's
            // artifact instead of re-traversing. Exact whole-plan equality only — steps and
            // projection, filters, aggregation, limit — because the artifact holds rows
            // already filtered and reduced to one projection's shape.
            var (reusedResult, reusedArtifactPath) = TryReuseThreadArtifact(job, executedPlanJson);

            var result = reusedResult ?? await executor.ExecutePlanAsync(
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

            // F04 Slice 7 (F04-D5): the artifact of record is written atomically *before* the
            // job is marked Completed, so every reader that acts on Completed finds it. The
            // mandatory 2h full-result IMemoryCache entry that used to live here is gone —
            // holding a 40k-row set resident is exactly what did not scale — and all four
            // readers (preview, single-record headline, download, cross-turn reuse) now read
            // the artifact instead.
            //
            // A reused artifact is pointed at, not rewritten: two jobs then share one file,
            // and retention keeps it until the last of them expires (IsArtifactStillOwned).
            job.ResultArtifactPath = reusedArtifactPath
                ?? await WriteResultArtifactAsync(job, result, jobToken);

            var aggregation = ComputeSettledAggregation(
                job.Plan, result.Data, result.GroupValues, result.Warnings);

            var answer = await NarrateAsync(job, claude, aggregation, result, modelOverride, jobToken);

            _store.SetCompleted(
                jobId,
                result.Data.Count,
                aggregation,
                result.Warnings,
                answer,
                job.ResultArtifactPath);

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

    /// <summary>
    /// Whole-plan artifact reuse (F04 Slice 7, F04-D5). Walks this job's thread and returns the
    /// nearest completed ancestor whose *serialized plan is byte-identical* to this turn's,
    /// together with that ancestor's artifact path.
    /// <para>
    /// Exact equality only, never fuzzy or semantic, and never membership-step equality:
    /// <c>DirectoryPlanExecutor.Project</c> stores rows already filtered and reduced to that
    /// turn's columns, so "everyone under Sanjay" → "only those with titles" would otherwise
    /// reuse an artifact with no Title column and unfiltered rows. The comparison reads the
    /// plan recorded *in the artifact*, not in-memory job state, so a rewritten or replaced
    /// file cannot be reused under a stale plan.
    /// </para>
    /// </summary>
    private (PlanExecutionResult? Result, string? ArtifactPath) TryReuseThreadArtifact(
        QueryJob job,
        string? executedPlanJson)
    {
        if (string.IsNullOrWhiteSpace(executedPlanJson))
        {
            return (null, null);
        }

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { job.JobId };
        var ancestorId = job.PreviousJobId;

        while (!string.IsNullOrWhiteSpace(ancestorId) && visited.Add(ancestorId))
        {
            var ancestor = _store.GetJob(ancestorId);
            if (ancestor == null)
            {
                return (null, null);
            }

            // Same guards the thread walk applies elsewhere: a foreign or incomplete turn is
            // not reusable material.
            if (ancestor.Status == JobStatus.Completed &&
                ancestor.UserName.Equals(job.UserName, StringComparison.OrdinalIgnoreCase))
            {
                var artifact = _resultArtifacts.Read(ancestor.ResultArtifactPath);
                if (artifact != null &&
                    string.Equals(artifact.PlanJson, executedPlanJson, StringComparison.Ordinal))
                {
                    _logger.LogInformation(
                        "Job {JobId} reuses the artifact of turn {AncestorId}: identical plan, no traversal",
                        job.JobId, ancestor.JobId);

                    return (
                        new PlanExecutionResult
                        {
                            Success = true,
                            Data = artifact.Rows,
                            GroupValues = artifact.GroupValues,
                            Warnings = artifact.Warnings,
                        },
                        ancestor.ResultArtifactPath);
                }
            }

            ancestorId = ancestor.PreviousJobId;
        }

        return (null, null);
    }

    /// <summary>
    /// Writes the completion-time artifact of record (F04 Slice 7, F04-D5), returning its path
    /// or null when the write failed. A failed artifact write degrades the job to the behavior
    /// it had before this slice — cache-backed, expiring — rather than failing a query whose
    /// directory work already succeeded.
    /// </summary>
    private async Task<string?> WriteResultArtifactAsync(
        QueryJob job,
        PlanExecutionResult result,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _resultArtifacts.WriteAsync(job, result, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Job {JobId} result artifact write failed; completing without one", job.JobId);
            return null;
        }
    }

    /// <summary>
    /// Narrate (F04 Slice 2, F04-D1): the second model call of a turn. Builds the bounded
    /// reduction server-side and asks the model to write the answer from it.
    ///
    /// Additive and isolated: every failure path — a null builder, an over-cap reduction,
    /// a provider error, a timeout, an unexpected exception — returns null, and the job
    /// completes with headline, table, and export exactly as it did before. Narrate is
    /// never a new way for a query to fail. Cancellation is the one exception: it belongs
    /// to the job and propagates.
    /// </summary>
    private async Task<string?> NarrateAsync(
        QueryJob job,
        IClaudeService claude,
        Dictionary<string, object>? aggregation,
        PlanExecutionResult result,
        string? modelOverride,
        CancellationToken cancellationToken)
    {
        try
        {
            var totalRows = result.Data.Count;
            var firstRow = totalRows == 1 && result.Data.Count > 0
                ? result.Data[0]
                : null;

            var headline = HeadlineClassifier.Classify(job.Plan, totalRows, aggregation, firstRow);
            var distribution = DistributionSummarizer.Summarize(
                aggregation,
                job.Plan?.Projection?.Aggregation?.GroupBy?.Count ?? 1,
                totalRows);

            var reduction = _answerReductionBuilder.Build(job.Query, job.Plan, headline, distribution);
            if (string.IsNullOrWhiteSpace(reduction))
            {
                _logger.LogWarning(
                    "Job {JobId} produced no composable answer reduction; completing without an answer", job.JobId);
                return null;
            }

            var response = await claude.GenerateAnswerAsync(reduction, cancellationToken, modelOverride);
            if (!response.Success || string.IsNullOrWhiteSpace(response.Answer))
            {
                _logger.LogWarning(
                    "Job {JobId} answer generation failed: {Error}", job.JobId, response.ErrorMessage);
                return null;
            }

            return response.Answer;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Job {JobId} answer generation threw; completing without an answer", job.JobId);
            return null;
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
        var allCompleted = _store.GetJobsByStatus(JobStatus.Completed);
        var expired = allCompleted
            .Where(j => j.CompletedAt.HasValue && j.CompletedAt.Value < cutoff)
            .ToList();

        var expiring = expired.Select(j => j.JobId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // F04 Slice 7 (f04-or-7): D5 moved results from a store with automatic expiry to one
        // with none, so retention has to delete the file too — and *before* RemoveJob drops
        // the metadata that names it, or the artifact is orphaned permanently.
        foreach (var job in expired)
        {
            if (!string.IsNullOrWhiteSpace(job.ResultArtifactPath) &&
                !IsArtifactStillOwned(job, expiring))
            {
                _resultArtifacts.Delete(job.ResultArtifactPath);
            }

            _store.RemoveJob(job.JobId);
            _logger.LogDebug("Cleaned up completed job {JobId}", job.JobId);
        }
    }

    /// <summary>
    /// Reuse ownership (f04-or-7): whole-plan reuse points a later turn's job at an earlier
    /// turn's artifact, so the originating job's expiry must not delete a file a surviving job
    /// still reads. The artifact outlives its writer for exactly as long as some job that is
    /// not itself expiring references the same path.
    /// </summary>
    private bool IsArtifactStillOwned(QueryJob expiring, HashSet<string> expiringJobIds) =>
        _store.GetUserJobs(expiring.UserName).Any(other =>
            !expiringJobIds.Contains(other.JobId) &&
            string.Equals(other.ResultArtifactPath, expiring.ResultArtifactPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Computes the aggregation a completed job settles with (F04 Slice 1, F04-D2).
    /// A grouped plan keeps its grouped counts regardless of how its projection columns
    /// relate to its <c>group_by</c> fields: a "unique list" plan is an ordinary grouped
    /// plan, so the distribution stays the answer and the rows stay the underlying records.
    /// Returns null when the plan requested no aggregation or produced no rows.
    /// </summary>
    /// <param name="groupValues">
    /// Per-row <c>group_by</c> values from the executor, read off the directory record
    /// (slice1r2-or-1). Positional against <paramref name="rows"/>. Absent or short — a
    /// legacy caller — degrades to no grouped counts plus a warning, never to a fabricated
    /// single bucket.
    /// </param>
    internal static Dictionary<string, object>? ComputeSettledAggregation(
        DirectoryQueryPlan plan,
        List<Dictionary<string, object?>> rows,
        IReadOnlyList<IReadOnlyList<string?>>? groupValues = null,
        ICollection<string>? warnings = null)
    {
        if (plan.Projection?.Aggregation == null || rows.Count == 0)
        {
            return null;
        }

        return ComputeAggregation(rows.Count, plan.Projection, groupValues, warnings);
    }

    private static Dictionary<string, object> ComputeAggregation(
        int rowCount,
        ProjectionDefinition projection,
        IReadOnlyList<IReadOnlyList<string?>>? groupValues,
        ICollection<string>? warnings)
    {
        var result = new Dictionary<string, object>();
        var aggregation = projection.Aggregation!;

        if (aggregation.Count && aggregation.GroupBy.Any())
        {
            if (groupValues == null || groupValues.Count != rowCount)
            {
                // Reporting nothing beats reporting a distribution built from values the
                // executor never supplied.
                warnings?.Add(
                    "Grouped counts are unavailable: the executor supplied no group values for this result.");
                return result;
            }

            var keys = groupValues
                .Select(values => GroupKey.Compose(aggregation.GroupBy
                    .Select((_, i) => (i < values.Count ? values[i] : null) ?? "(empty)")
                    .ToList()))
                .ToList();

            // The fold goes on GroupBy, never on the result dictionary's comparer
            // (f04-or-4): an ordinal GroupBy followed by a case-insensitive ToDictionary
            // throws on the first pair of case-variant keys.
            var comparer = aggregation.CaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;
            var grouped = new Dictionary<string, int>();
            var spellings = new Dictionary<string, int>();

            foreach (var fold in keys.GroupBy(key => key, comparer))
            {
                // The display key is the bucket's most frequent original spelling — a
                // property of the group's members, which is why the fold cannot be a
                // dictionary comparer. Ties break ordinally so the choice is stable.
                var bySpelling = fold
                    .GroupBy(key => key, StringComparer.Ordinal)
                    .OrderByDescending(g => g.Count())
                    .ThenBy(g => g.Key, StringComparer.Ordinal)
                    .ToList();

                var display = bySpelling[0].Key;
                grouped[display] = fold.Count();

                // Only multi-spelling buckets are carried: a "1" for every bucket would
                // duplicate the distribution at no information gain (a near-unique
                // attribute has tens of thousands of single-spelling buckets).
                if (bySpelling.Count > 1)
                {
                    spellings[display] = bySpelling.Count;
                }
            }

            result["grouped_counts"] = grouped;

            if (spellings.Count > 0)
            {
                result["grouped_spellings"] = spellings;
            }
        }

        return result;
    }
}
