# Recursive Org Summary - Async Implementation Workflow

## Overview

Add `expand_reports` operation with async job execution for recursive organizational hierarchy traversal. When users ask "show everyone under [person]", the system starts a background job, polls for progress, and downloads results when complete.

**Architecture**: Async job pattern with polling
- POST /api/query/execute → 202 Accepted with jobId
- GET /api/query/jobs/{jobId} → status/progress/results
- Download when complete

---

## Why Async-First

**Scale Reality**:
- 40,000 total users
- "Entire org" queries take 2-5 minutes
- Multi-minute synchronous requests are fragile

**Async Benefits**:
- ✅ UI doesn't freeze - shows progress ("17,453 of ~40,000 processed")
- ✅ Network hiccups don't kill entire query
- ✅ Doesn't tie up IIS worker threads
- ✅ Easy to throttle concurrent large queries
- ✅ Can cancel/resume long-running jobs
- ✅ Better monitoring and diagnostics

**Tradeoff**: More complexity (job manager, polling, storage) but builds robust foundation.

---

## Implementation Phases

### Phase 1: Schema & Model Updates
**Duration**: 1-2 days
**Files**: `csharp/Models/DirectoryQueryPlan.cs`, `csharp/Models/QueryJob.cs`

#### 1.1 Add Fields to DirectoryPlanStep

```csharp
/// <summary>
/// Maximum recursion depth (1-100). Safety valve against infinite cycles.
/// Defaults to 10 if omitted.
/// </summary>
[JsonPropertyName("max_depth")]
public int? MaxDepth { get; set; }

/// <summary>
/// Maximum total nodes to retrieve. Defaults to 10,000 if omitted.
/// </summary>
[JsonPropertyName("max_nodes")]
public int? MaxNodes { get; set; }
```

#### 1.2 Add AggregationDefinition to ProjectionDefinition

```csharp
/// <summary>
/// Optional aggregation rules for summary data.
/// </summary>
[JsonPropertyName("aggregation")]
public AggregationDefinition? Aggregation { get; set; }
```

#### 1.3 Create AggregationDefinition Class

```csharp
public class AggregationDefinition
{
    [JsonPropertyName("group_by")]
    public List<string> GroupBy { get; set; } = new();

    [JsonPropertyName("count")]
    public bool Count { get; set; }

    [JsonPropertyName("include_level_metadata")]
    public bool IncludeLevelMetadata { get; set; }
}
```

#### 1.4 Create QueryJob Model (NEW - Job Infrastructure)

```csharp
// New file: csharp/Models/QueryJob.cs
namespace AdQuery.Orchestrator.Models;

public class QueryJob
{
    public string JobId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public DirectoryQueryPlan? Plan { get; set; }

    public JobStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Progress tracking
    public int NodesProcessed { get; set; }
    public int CurrentDepth { get; set; }
    public int EstimatedTotal { get; set; } // Optional heuristic

    // Results
    public string? ResultsCacheKey { get; set; }
    public int? TotalRows { get; set; }
    public Dictionary<string, object>? Aggregation { get; set; }
    public List<string> Warnings { get; set; } = new();

    // Error handling
    public string? ErrorMessage { get; set; }

    // Cancellation
    public CancellationTokenSource? CancellationSource { get; set; }
}

public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}
```

**Acceptance**:
- Models compile and serialize correctly
- Job infrastructure ready for background execution

---

### Phase 2: Job Manager Service
**Duration**: 2-3 days
**Files**: `csharp/Services/QueryJobManager.cs` (NEW)

#### 2.1 Create IQueryJobManager Interface

```csharp
public interface IQueryJobManager
{
    string CreateJob(string userName, string query);
    QueryJob? GetJob(string jobId);
    Task ExecuteJobAsync(string jobId, CancellationToken cancellationToken);
    void CancelJob(string jobId);
    List<QueryJob> GetUserJobs(string userName);
    void CleanupCompletedJobs(TimeSpan olderThan);
}
```

#### 2.2 Implement QueryJobManager

```csharp
public class QueryJobManager : IQueryJobManager
{
    private readonly ConcurrentDictionary<string, QueryJob> _jobs = new();
    private readonly IClaudeService _claude;
    private readonly IDirectoryPlanExecutor _executor;
    private readonly IPlanValidator _validator;
    private readonly IMemoryCache _cache;
    private readonly ILogger<QueryJobManager> _logger;

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

        _jobs[jobId] = job;
        _logger.LogInformation("Job {JobId} created for user {UserName}", jobId, userName);

        return jobId;
    }

    public QueryJob? GetJob(string jobId)
    {
        _jobs.TryGetValue(jobId, out var job);
        return job;
    }

    public async Task ExecuteJobAsync(string jobId, CancellationToken cancellationToken)
    {
        if (!_jobs.TryGetValue(jobId, out var job))
        {
            _logger.LogWarning("Job {JobId} not found", jobId);
            return;
        }

        try
        {
            job.Status = JobStatus.Running;
            job.StartedAt = DateTime.UtcNow;
            job.CancellationSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Generate plan
            var planResponse = await _claude.GenerateExecutionPlanAsync(job.Query);
            job.Plan = planResponse.Plan;

            // Validate
            var validation = _validator.Validate(job.Plan);
            if (!validation.IsValid)
            {
                job.Status = JobStatus.Failed;
                job.ErrorMessage = string.Join("; ", validation.SecurityErrors);
                _logger.LogWarning("Job {JobId} validation failed: {Errors}", jobId, job.ErrorMessage);
                return;
            }

            // Execute with progress callback
            var result = await _executor.ExecuteAsync(
                job.Plan,
                progress => UpdateProgress(job, progress),
                job.CancellationSource.Token
            );

            // Store results
            var resultsCacheKey = $"job_results_{jobId}";
            _cache.Set(resultsCacheKey, result, TimeSpan.FromHours(2));

            job.ResultsCacheKey = resultsCacheKey;
            job.TotalRows = result.Rows.Count;
            job.Aggregation = result.Aggregation;
            job.Warnings = result.Warnings ?? new();
            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;

            _logger.LogInformation(
                "Job {JobId} completed: {Rows} rows in {Duration}s",
                jobId,
                result.Rows.Count,
                (job.CompletedAt.Value - job.StartedAt.Value).TotalSeconds
            );
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            _logger.LogInformation("Job {JobId} cancelled", jobId);
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Job {JobId} failed", jobId);
        }
    }

    private void UpdateProgress(QueryJob job, ExecutionProgress progress)
    {
        job.NodesProcessed = progress.NodesProcessed;
        job.CurrentDepth = progress.CurrentDepth;
        job.EstimatedTotal = progress.EstimatedTotal;
    }

    public void CancelJob(string jobId)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.CancellationSource?.Cancel();
            _logger.LogInformation("Job {JobId} cancellation requested", jobId);
        }
    }

    public List<QueryJob> GetUserJobs(string userName)
    {
        return _jobs.Values
            .Where(j => j.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.CreatedAt)
            .ToList();
    }

    public void CleanupCompletedJobs(TimeSpan olderThan)
    {
        var cutoff = DateTime.UtcNow - olderThan;
        var toRemove = _jobs.Values
            .Where(j => j.Status == JobStatus.Completed && j.CompletedAt < cutoff)
            .Select(j => j.JobId)
            .ToList();

        foreach (var jobId in toRemove)
        {
            _jobs.TryRemove(jobId, out _);
            _logger.LogDebug("Cleaned up job {JobId}", jobId);
        }
    }
}
```

#### 2.3 Add ExecutionProgress Model

```csharp
public class ExecutionProgress
{
    public int NodesProcessed { get; set; }
    public int CurrentDepth { get; set; }
    public int EstimatedTotal { get; set; }
}
```

#### 2.4 Register Services in Program.cs

```csharp
// Add to DI container
builder.Services.AddSingleton<IQueryJobManager, QueryJobManager>();

// Add background job executor
builder.Services.AddHostedService<QueryJobExecutorService>();
```

**Acceptance**:
- Job manager creates and tracks jobs
- Jobs can be queried by ID
- User can retrieve their own jobs
- Cleanup removes old completed jobs

---

### Phase 3: Background Job Executor
**Duration**: 2 days
**Files**: `csharp/Services/QueryJobExecutorService.cs` (NEW)

#### 3.1 Create Background Service

```csharp
public class QueryJobExecutorService : BackgroundService
{
    private readonly IQueryJobManager _jobManager;
    private readonly ILogger<QueryJobExecutorService> _logger;
    private readonly SemaphoreSlim _concurrencySemaphore;

    public QueryJobExecutorService(
        IQueryJobManager jobManager,
        IConfiguration config,
        ILogger<QueryJobExecutorService> logger)
    {
        _jobManager = jobManager;
        _logger = logger;

        var maxConcurrent = config.GetValue<int>("Jobs:MaxConcurrentJobs", 3);
        _concurrencySemaphore = new SemaphoreSlim(maxConcurrent);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Query job executor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Find queued jobs
                var queuedJobs = _jobManager.GetAllJobs()
                    .Where(j => j.Status == JobStatus.Queued)
                    .OrderBy(j => j.CreatedAt)
                    .ToList();

                foreach (var job in queuedJobs)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    // Acquire semaphore slot
                    await _concurrencySemaphore.WaitAsync(stoppingToken);

                    // Execute job in background
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _jobManager.ExecuteJobAsync(job.JobId, stoppingToken);
                        }
                        finally
                        {
                            _concurrencySemaphore.Release();
                        }
                    }, stoppingToken);
                }

                // Cleanup old jobs (older than 24 hours)
                _jobManager.CleanupCompletedJobs(TimeSpan.FromHours(24));

                // Poll interval
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in job executor loop");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("Query job executor stopped");
    }
}
```

#### 3.2 Add Job Configuration

```json
// appsettings.json
"Jobs": {
  "MaxConcurrentJobs": 3,           // Max large queries running simultaneously
  "CompletedJobRetentionHours": 24, // Keep results for 24 hours
  "MaxJobsPerUser": 10              // Prevent abuse
}
```

**Acceptance**:
- Background service starts on app launch
- Picks up queued jobs automatically
- Respects concurrency limits (3 simultaneous large queries)
- Cleans up old completed jobs

---

### Phase 4: Executor with Progress Callbacks
**Duration**: 3-4 days
**Files**: `csharp/Services/DirectoryPlanExecutor.cs`

#### 4.1 Update IDirectoryPlanExecutor Interface

```csharp
public interface IDirectoryPlanExecutor
{
    // Existing sync method (for simple queries)
    Task<RuntimeResult> ExecuteAsync(
        DirectoryQueryPlan plan,
        CancellationToken cancellationToken);

    // NEW: Async with progress callbacks
    Task<RuntimeResult> ExecuteAsync(
        DirectoryQueryPlan plan,
        Action<ExecutionProgress> onProgress,
        CancellationToken cancellationToken);
}
```

#### 4.2 Update ExecuteExpandReports with Progress

```csharp
private async Task<StepRuntimeState> ExecuteExpandReports(
    DirectoryPlanStep step,
    DirectoryPlanRuntime runtime,
    Action<ExecutionProgress>? onProgress,
    CancellationToken cancellationToken)
{
    var maxDepth = step.MaxDepth ?? 10;
    var maxNodes = step.MaxNodes ?? 10000;
    var visitedDNs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var levelResults = new Dictionary<int, List<DirectoryRecord>>();
    var warnings = new List<string>();

    // Get seed DNs from source step
    var sourceState = runtime.StepStates[step.Source];
    var seedDNs = sourceState.Records
        .Select(r => r.GetAttributeValue("distinguishedName"))
        .Where(dn => !string.IsNullOrEmpty(dn))
        .ToList();

    levelResults[0] = sourceState.Records;

    // Breadth-first traversal
    var currentLevelDNs = seedDNs;
    for (int depth = 1; depth <= maxDepth; depth++)
    {
        if (!currentLevelDNs.Any()) break;

        // Report progress
        var totalProcessed = levelResults.Values.Sum(list => list.Count);
        onProgress?.Invoke(new ExecutionProgress
        {
            NodesProcessed = totalProcessed,
            CurrentDepth = depth,
            EstimatedTotal = EstimateTotal(totalProcessed, depth) // Heuristic
        });

        // Mark as visited
        foreach (var dn in currentLevelDNs)
        {
            visitedDNs.Add(dn);
        }

        // Batch query: find all direct reports for this level
        var directReports = await _adService.GetDirectReportsBatch(
            currentLevelDNs,
            step.Attributes,
            cancellationToken
        );

        if (!directReports.Any())
        {
            // Natural end of org tree
            _logger.LogDebug("Recursion ended naturally at depth {Depth}", depth);
            break;
        }

        // Check node limit
        var newTotal = totalProcessed + directReports.Count;
        if (newTotal > maxNodes)
        {
            var remaining = maxNodes - totalProcessed;
            warnings.Add($"Stopped at {maxNodes} nodes (truncated)");
            directReports = directReports.Take(remaining).ToList();
            levelResults[depth] = directReports;
            break;
        }

        levelResults[depth] = directReports;

        // Prepare next level DNs (excluding cycles)
        currentLevelDNs = directReports
            .Select(r => r.GetAttributeValue("distinguishedName"))
            .Where(dn => !string.IsNullOrEmpty(dn) && !visitedDNs.Contains(dn))
            .ToList();

        // Hit max depth with nodes remaining
        if (depth == maxDepth && currentLevelDNs.Any())
        {
            warnings.Add($"Stopped at depth {maxDepth} (safety limit)");
        }
    }

    // Final progress update
    var finalTotal = levelResults.Values.Sum(list => list.Count);
    onProgress?.Invoke(new ExecutionProgress
    {
        NodesProcessed = finalTotal,
        CurrentDepth = levelResults.Keys.Max(),
        EstimatedTotal = finalTotal
    });

    // Flatten all levels
    var allRecords = levelResults
        .Where(kvp => kvp.Key > 0)
        .SelectMany(kvp => kvp.Value)
        .ToList();

    return new StepRuntimeState
    {
        Records = allRecords,
        LevelMetadata = levelResults.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Count),
        Warnings = warnings
    };
}

private int EstimateTotal(int processed, int currentDepth)
{
    // Simple heuristic: assume similar growth rate
    if (currentDepth == 0) return processed;
    var avgGrowth = processed / currentDepth;
    return processed + (avgGrowth * 2); // Rough estimate for remaining levels
}
```

**Acceptance**:
- Progress callbacks invoked after each level
- Executor can work with or without progress (backward compat)
- Cancellation tokens respected throughout

---

### Phase 5: API Endpoints (Async Pattern)
**Duration**: 2 days
**Files**: `csharp/Controllers/QueryController.cs`

#### 5.1 Update Execute Endpoint (Returns Job)

```csharp
[HttpPost("execute")]
public IActionResult Execute(
    [FromBody] QueryExecuteRequest request,
    HttpContext httpContext)
{
    var userName = httpContext.User.Identity?.Name ?? "unknown";

    // Create job
    var jobId = _jobManager.CreateJob(userName, request.Query);

    // Return immediately with job ID
    return Accepted(new
    {
        jobId = jobId,
        statusUrl = $"/api/query/jobs/{jobId}",
        message = "Query job created. Poll status endpoint for progress."
    });
}
```

#### 5.2 Create Job Status Endpoint (NEW)

```csharp
[HttpGet("jobs/{jobId}")]
public IActionResult GetJobStatus(string jobId)
{
    var job = _jobManager.GetJob(jobId);
    if (job == null)
    {
        return NotFound(new { error = "Job not found" });
    }

    // Don't return sensitive data to non-owners
    var userName = HttpContext.User.Identity?.Name ?? "unknown";
    if (!job.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
    {
        return Forbid();
    }

    // Build response based on status
    var response = new
    {
        jobId = job.JobId,
        status = job.Status.ToString().ToLower(),
        createdAt = job.CreatedAt,
        startedAt = job.StartedAt,
        completedAt = job.CompletedAt,
        progress = job.Status == JobStatus.Running ? new
        {
            nodesProcessed = job.NodesProcessed,
            currentDepth = job.CurrentDepth,
            estimatedTotal = job.EstimatedTotal,
            percentComplete = job.EstimatedTotal > 0
                ? (int)((job.NodesProcessed / (double)job.EstimatedTotal) * 100)
                : 0
        } : null,
        result = job.Status == JobStatus.Completed ? new
        {
            totalRows = job.TotalRows,
            aggregation = BuildAggregationSummary(job),
            warnings = job.Warnings.Any() ? job.Warnings : null,
            downloadUrl = $"/api/query/download/csv/{job.JobId}"
        } : null,
        error = job.Status == JobStatus.Failed ? job.ErrorMessage : null
    };

    return Ok(response);
}

private AggregationSummary? BuildAggregationSummary(QueryJob job)
{
    if (job.Aggregation == null) return null;

    var summary = new AggregationSummary();

    if (job.Aggregation.ContainsKey("grouped_counts"))
    {
        summary.GroupedCounts = (Dictionary<string, int>)job.Aggregation["grouped_counts"];
    }

    if (job.Aggregation.ContainsKey("level_metadata"))
    {
        summary.LevelMetadata = (Dictionary<int, int>)job.Aggregation["level_metadata"];
    }

    if (job.Plan?.Projection?.Aggregation != null)
    {
        summary.GroupByFields = job.Plan.Projection.Aggregation.GroupBy;
    }

    return summary;
}
```

#### 5.3 Create Cancel Endpoint (NEW)

```csharp
[HttpPost("jobs/{jobId}/cancel")]
public IActionResult CancelJob(string jobId)
{
    var job = _jobManager.GetJob(jobId);
    if (job == null) return NotFound();

    var userName = HttpContext.User.Identity?.Name ?? "unknown";
    if (!job.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
    {
        return Forbid();
    }

    if (job.Status != JobStatus.Running && job.Status != JobStatus.Queued)
    {
        return BadRequest(new { error = "Job is not running" });
    }

    _jobManager.CancelJob(jobId);
    return Ok(new { message = "Cancellation requested" });
}
```

#### 5.4 Update Download Endpoint (Use JobId)

```csharp
[HttpGet("download/csv/{jobId}")]
public IActionResult DownloadCsv(string jobId)
{
    var job = _jobManager.GetJob(jobId);
    if (job == null) return NotFound(new { error = "Job not found" });

    // Verify ownership
    var userName = HttpContext.User.Identity?.Name ?? "unknown";
    if (!job.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
    {
        return Forbid();
    }

    if (job.Status != JobStatus.Completed)
    {
        return BadRequest(new { error = $"Job status is {job.Status}, not completed" });
    }

    if (!_cache.TryGetValue(job.ResultsCacheKey, out RuntimeResult result))
    {
        return NotFound(new { error = "Results expired" });
    }

    var csv = new StringBuilder();

    // Header
    if (result.Rows.Any())
    {
        csv.AppendLine(string.Join(",", result.Rows[0].Keys.Select(EscapeCsv)));
    }

    // Data rows
    foreach (var row in result.Rows)
    {
        csv.AppendLine(string.Join(",", row.Values.Select(EscapeCsv)));
    }

    // Aggregation summary as comments
    if (result.Aggregation != null)
    {
        csv.AppendLine();
        csv.AppendLine("# SUMMARY");

        if (result.Aggregation.ContainsKey("grouped_counts"))
        {
            var counts = (Dictionary<string, int>)result.Aggregation["grouped_counts"];
            csv.AppendLine("# Category,Count");
            foreach (var (key, count) in counts)
            {
                csv.AppendLine($"# {EscapeCsv(key)},{count}");
            }
        }

        if (result.Aggregation.ContainsKey("level_metadata"))
        {
            csv.AppendLine("#");
            csv.AppendLine("# HIERARCHY DEPTH");
            var levels = (Dictionary<int, int>)result.Aggregation["level_metadata"];
            csv.AppendLine("# Level,Count");
            foreach (var (level, count) in levels.OrderBy(kvp => kvp.Key))
            {
                csv.AppendLine($"# Level {level},{count}");
            }
        }
    }

    // Warnings
    if (result.Warnings?.Any() == true)
    {
        csv.AppendLine();
        csv.AppendLine("# WARNINGS");
        foreach (var warning in result.Warnings)
        {
            csv.AppendLine($"# {warning}");
        }
    }

    return File(
        Encoding.UTF8.GetBytes(csv.ToString()),
        "text/csv",
        $"adquery_{userName}_{DateTime.UtcNow:yyyyMMddHHmmss}.csv"
    );
}
```

**Acceptance**:
- Background service processes queued jobs
- Concurrency limited (3 simultaneous large queries)
- Download endpoint retrieves completed results
- User isolation (can only access own jobs)

---

### Phase 6: Validator & Security
**Duration**: 1-2 days
**Files**: `csharp/Security/PlanValidator.cs`, `appsettings.json`

#### 6.1 Configuration

```json
"Security": {
  "MaxRecursionDepth": 100,              // Maximum allowed
  "MaxNodesPerRecursion": 50000,         // Above total user count (40K)
  "DefaultRecursionDepth": 10,           // Conservative default
  "DefaultMaxNodes": 10000,              // Conservative default
  "EnableRecursiveQueries": true,        // Feature flag
  // ... existing
}
```

#### 6.2 Validator Rules (same as before, already explicit)

```csharp
private PlanSecurityResult ValidateExpandReports(DirectoryPlanStep step)
{
    var errors = new List<string>();

    // Feature disabled
    if (!_config.GetValue<bool>("Security:EnableRecursiveQueries"))
    {
        errors.Add($"Step '{step.Name}': recursive queries are disabled");
        return new PlanSecurityResult { IsValid = false, SecurityErrors = errors };
    }

    // Missing max_depth (allowed - will use default)
    // max_depth out of range
    if (step.MaxDepth.HasValue)
    {
        if (step.MaxDepth.Value < 1)
        {
            errors.Add($"Step '{step.Name}': max_depth must be >= 1 (got {step.MaxDepth.Value})");
        }
        if (step.MaxDepth.Value > _config.GetValue<int>("Security:MaxRecursionDepth"))
        {
            errors.Add($"Step '{step.Name}': max_depth {step.MaxDepth.Value} exceeds limit of {_config.GetValue<int>("Security:MaxRecursionDepth")}");
        }
    }

    // max_nodes out of range
    if (step.MaxNodes.HasValue)
    {
        var limit = _config.GetValue<int>("Security:MaxNodesPerRecursion");
        if (step.MaxNodes.Value < 1)
        {
            errors.Add($"Step '{step.Name}': max_nodes must be >= 1 (got {step.MaxNodes.Value})");
        }
        if (step.MaxNodes.Value > limit)
        {
            errors.Add($"Step '{step.Name}': max_nodes {step.MaxNodes.Value} exceeds limit of {limit}");
        }
    }

    // Missing source
    if (string.IsNullOrEmpty(step.Source))
    {
        errors.Add($"Step '{step.Name}': expand_reports requires 'source' field");
    }

    // Wrong target_type
    if (step.TargetType != DirectoryObjectType.User)
    {
        errors.Add($"Step '{step.Name}': expand_reports only supports target_type 'User' (got '{step.TargetType}')");
    }

    // Empty attributes
    if (step.Attributes == null || !step.Attributes.Any())
    {
        errors.Add($"Step '{step.Name}': expand_reports requires at least one attribute");
    }

    return new PlanSecurityResult
    {
        IsValid = !errors.Any(),
        SecurityErrors = errors
    };
}

private PlanSecurityResult ValidateAggregation(ProjectionDefinition projection)
{
    if (projection.Aggregation == null) return PlanSecurityResult.Success();

    var errors = new List<string>();
    var agg = projection.Aggregation;

    // No grouping and no count
    if (!agg.GroupBy.Any() && !agg.Count)
    {
        errors.Add("Aggregation requires 'group_by' fields or 'count: true'");
    }

    // Empty group_by fields
    if (agg.GroupBy.Any(string.IsNullOrWhiteSpace))
    {
        errors.Add("Aggregation group_by contains empty field names");
    }

    // Validate fields in allow-list
    foreach (var field in agg.GroupBy.Where(f => !string.IsNullOrWhiteSpace(f)))
    {
        if (!IsAttributeAllowed(field, DirectoryObjectType.User))
        {
            errors.Add($"Aggregation field '{field}' is not in attribute allow-list");
        }
    }

    // Too many fields
    if (agg.GroupBy.Count > 5)
    {
        errors.Add($"Aggregation group_by has {agg.GroupBy.Count} fields; maximum is 5");
    }

    // Duplicates
    var duplicates = agg.GroupBy
        .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key)
        .ToList();

    if (duplicates.Any())
    {
        errors.Add($"Aggregation contains duplicate fields: {string.Join(", ", duplicates)}");
    }

    return new PlanSecurityResult
    {
        IsValid = !errors.Any(),
        SecurityErrors = errors
    };
}
```

**Test Cases** (same as sync version):
- [ ] expand_reports without max_depth → allowed (uses default)
- [ ] expand_reports with max_depth=0 → rejected
- [ ] expand_reports with max_depth=200 → rejected (exceeds 100)
- [ ] expand_reports with target_type=Group → rejected
- [ ] Aggregation with disallowed field → rejected
- [ ] Aggregation with duplicate fields → rejected

---

### Phase 7: Claude Prompt Updates
**Duration**: 1 day
**Files**: `csharp/Services/ClaudeService.cs`

(Exact same as async workflow - prompt contract doesn't change based on sync vs async execution)

---

### Phase 8: Frontend (Polling UI)
**Duration**: 2-3 days
**Files**: `csharp/wwwroot/js/app.js`, `csharp/wwwroot/css/styles.css`, `csharp/wwwroot/index.html`

#### 8.1 Update Execute Handler to Start Job

```javascript
// app.js
async function executeQuery(query) {
    try {
        // Submit query - returns immediately with jobId
        const response = await fetch('/api/query/execute', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ query })
        });

        if (response.status === 202) {
            const jobInfo = await response.json();
            showProgress('Query submitted. Processing...');
            pollJobStatus(jobInfo.jobId);
        } else {
            const error = await response.json();
            showError(error.message || 'Failed to submit query');
        }
    } catch (error) {
        showError('Network error: ' + error.message);
    }
}
```

#### 8.2 Add Polling Mechanism

```javascript
let pollInterval = null;

function pollJobStatus(jobId) {
    // Clear any existing poll
    if (pollInterval) {
        clearInterval(pollInterval);
    }

    // Poll every 2 seconds
    pollInterval = setInterval(async () => {
        try {
            const response = await fetch(`/api/query/jobs/${jobId}`);
            const job = await response.json();

            switch (job.status) {
                case 'queued':
                    showProgress('Query queued...');
                    break;

                case 'running':
                    const pct = job.progress?.percentComplete || 0;
                    const nodes = job.progress?.nodesProcessed || 0;
                    const est = job.progress?.estimatedTotal || '?';
                    const depth = job.progress?.currentDepth || 0;
                    showProgress(
                        `Processing level ${depth}... ${nodes} of ~${est} nodes (${pct}%)`
                    );
                    break;

                case 'completed':
                    clearInterval(pollInterval);
                    hideProgress();
                    displayResults(job.result, jobId);
                    break;

                case 'failed':
                    clearInterval(pollInterval);
                    hideProgress();
                    showError('Query failed: ' + job.error);
                    break;

                case 'cancelled':
                    clearInterval(pollInterval);
                    hideProgress();
                    showError('Query was cancelled');
                    break;
            }
        } catch (error) {
            clearInterval(pollInterval);
            hideProgress();
            showError('Failed to check job status: ' + error.message);
        }
    }, 2000); // Poll every 2 seconds
}
```

#### 8.3 Display Results from Job

```javascript
function displayResults(result, jobId) {
    // Show warnings
    displayWarnings(result.warnings);

    // Show aggregation summary
    displayAggregation(result.aggregation);

    // Show preview message
    document.getElementById('results-info').innerHTML =
        `<p>Found ${result.totalRows.toLocaleString()} total records. Showing preview below.</p>`;

    // Fetch and display preview rows
    fetch(`/api/query/jobs/${jobId}/preview`)
        .then(r => r.json())
        .then(preview => {
            displayTable(preview.rows);
            setupDownloadButtons(jobId);
        });
}
```

#### 8.4 Add Cancel Button

```html
<!-- index.html -->
<div id="query-controls">
    <button id="submitBtn" onclick="submitQuery()">Execute Query</button>
    <button id="cancelBtn" onclick="cancelQuery()" style="display:none;">Cancel</button>
</div>
```

```javascript
// app.js
let currentJobId = null;

function submitQuery() {
    const query = document.getElementById('queryInput').value;
    executeQuery(query);
}

function cancelQuery() {
    if (!currentJobId) return;

    fetch(`/api/query/jobs/${currentJobId}/cancel`, { method: 'POST' })
        .then(() => {
            showError('Query cancelled');
            document.getElementById('cancelBtn').style.display = 'none';
        });
}

function pollJobStatus(jobId) {
    currentJobId = jobId;
    document.getElementById('cancelBtn').style.display = 'inline-block';

    // ... existing polling code ...

    // On completion/failure/cancel:
    currentJobId = null;
    document.getElementById('cancelBtn').style.display = 'none';
}
```

#### 8.5 Add Progress Indicator Styling

```css
/* styles.css */
#progress-indicator {
    background: #44475a;
    border: 1px solid #6272a4;
    border-radius: 4px;
    padding: 20px;
    margin: 12px 0;
    text-align: center;
}

.progress-message {
    color: #f8f8f2;
    font-size: 16px;
    font-weight: 500;
}

.spinner {
    display: inline-block;
    margin-right: 8px;
    font-size: 20px;
}

#cancelBtn {
    background: #ff5555;
    margin-left: 12px;
}

#cancelBtn:hover {
    background: #ff6b6b;
}
```

**Acceptance**:
- Submit query → 202 response with jobId
- UI polls status every 2 seconds
- Progress updates show live (nodes, depth, percentage)
- Results display when complete
- Cancel button works during execution
- Download button appears on completion

---

### Phase 9: ActiveDirectory Batching
**Duration**: 1-2 days
**Files**: `csharp/Services/ActiveDirectoryService.cs`

(Same as sync workflow - batching logic doesn't change)

```csharp
public async Task<List<DirectoryRecord>> GetDirectReportsBatch(
    List<string> managerDNs,
    List<string> attributes,
    CancellationToken cancellationToken)
{
    if (!managerDNs.Any()) return new();

    // Build OR filter: (|(manager=DN1)(manager=DN2)...)
    var filterParts = managerDNs
        .Select(dn => $"(manager={LdapEscape(dn)})")
        .ToList();

    var filter = filterParts.Count == 1
        ? filterParts[0]
        : $"(|{string.Join("", filterParts)})";

    var combinedFilter = $"(&(objectClass=user){filter})";

    return await ExecuteSearch(new DirectorySearchRequest
    {
        Filter = combinedFilter,
        Attributes = EnsureRequiredAttributes(attributes),
        ObjectClass = "user"
    }, cancellationToken);
}

private List<string> EnsureRequiredAttributes(List<string> requested)
{
    var attributes = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
    attributes.Add("distinguishedName");
    attributes.Add("manager");
    return attributes.ToList();
}
```

---

### Phase 10: Testing
**Duration**: 3-4 days
**Files**: `csharp/Tests/`

#### 10.1 Job Manager Tests

```csharp
[Fact]
public void CreateJob_ReturnsValidJobId()
{
    var jobId = _jobManager.CreateJob("testuser", "test query");

    Assert.NotNull(jobId);
    var job = _jobManager.GetJob(jobId);
    Assert.Equal(JobStatus.Queued, job.Status);
}

[Fact]
public async Task ExecuteJob_UpdatesProgress()
{
    var jobId = _jobManager.CreateJob("testuser", "show everyone under CEO");

    await _jobManager.ExecuteJobAsync(jobId, CancellationToken.None);

    var job = _jobManager.GetJob(jobId);
    Assert.Equal(JobStatus.Completed, job.Status);
    Assert.True(job.NodesProcessed > 0);
}

[Fact]
public void CancelJob_StopsExecution()
{
    var jobId = _jobManager.CreateJob("testuser", "test query");

    var executeTask = _jobManager.ExecuteJobAsync(jobId, CancellationToken.None);
    _jobManager.CancelJob(jobId);

    // Should complete quickly with cancelled status
    var job = _jobManager.GetJob(jobId);
    Assert.Equal(JobStatus.Cancelled, job.Status);
}
```

#### 10.2 API Endpoint Tests

```csharp
[Fact]
public async Task Execute_Returns202WithJobId()
{
    var response = await _client.PostAsync("/api/query/execute",
        JsonContent.Create(new { query = "test query" }));

    Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

    var result = await response.Content.ReadFromJsonAsync<dynamic>();
    Assert.NotNull(result.jobId);
    Assert.NotNull(result.statusUrl);
}

[Fact]
public async Task GetJobStatus_ReturnsProgressDuringExecution()
{
    // Create job
    var createResponse = await _client.PostAsync("/api/query/execute",
        JsonContent.Create(new { query = "show everyone under test manager" }));
    var jobInfo = await createResponse.Content.ReadFromJsonAsync<dynamic>();

    // Poll status
    await Task.Delay(500); // Give it time to start
    var statusResponse = await _client.GetAsync($"/api/query/jobs/{jobInfo.jobId}");
    var status = await statusResponse.Content.ReadFromJsonAsync<dynamic>();

    Assert.True(status.status == "running" || status.status == "completed");
    if (status.status == "running")
    {
        Assert.NotNull(status.progress);
    }
}
```

#### 10.3 Stress Tests (CRITICAL)

```csharp
[Fact]
public async Task StressTest_FullOrg_40KNodes()
{
    var jobId = _jobManager.CreateJob("testuser", "show entire org under CEO");

    var sw = Stopwatch.StartNew();
    await _jobManager.ExecuteJobAsync(jobId, CancellationToken.None);
    sw.Stop();

    var job = _jobManager.GetJob(jobId);

    Assert.Equal(JobStatus.Completed, job.Status);
    Assert.True(job.TotalRows <= 50000);
    Assert.True(sw.Elapsed.TotalMinutes < 10, $"Took {sw.Elapsed.TotalMinutes} min");

    _logger.LogInformation(
        "Full org: {Nodes} nodes in {Seconds}s, peak memory {MB}MB",
        job.TotalRows,
        sw.Elapsed.TotalSeconds,
        GC.GetTotalMemory(false) / 1024 / 1024
    );
}

[Fact]
public async Task StressTest_ConcurrentLargeQueries()
{
    // 5 users query 10K nodes each simultaneously
    var jobIds = Enumerable.Range(0, 5)
        .Select(i => _jobManager.CreateJob($"user{i}", "large query"))
        .ToList();

    var tasks = jobIds.Select(id => _jobManager.ExecuteJobAsync(id, CancellationToken.None));

    await Task.WhenAll(tasks);

    // All should complete
    foreach (var jobId in jobIds)
    {
        var job = _jobManager.GetJob(jobId);
        Assert.Equal(JobStatus.Completed, job.Status);
    }
}
```

**Stress Test Requirements**:
- [ ] 40K node query completes in < 10 minutes
- [ ] Memory stays under 4GB per query
- [ ] 5 concurrent 10K queries complete without errors
- [ ] LDAP server load monitored (AD performance counters)
- [ ] No IIS worker thread exhaustion

**If stress tests fail**: Document limits, provide clear error messages, plan optimizations.

---

## Deployment Checklist

### Pre-Deployment
- [ ] All unit tests pass
- [ ] Integration tests pass
- [ ] **Stress tests with 40K nodes pass**
- [ ] IIS timeout configured (10+ minutes)
- [ ] Job cleanup cron verified
- [ ] Feature flag ready (EnableRecursiveQueries)
- [ ] Documentation updated

### Deployment

```powershell
cd D:\source\adquery\csharp
dotnet test --configuration Release
.\deploy.ps1 -Force
```

### Post-Deployment
- [ ] Health check: GET /health → 200 OK
- [ ] Submit test query → receives jobId
- [ ] Poll /api/query/jobs/{id} → shows progress
- [ ] Results complete and download works
- [ ] Cancel button works
- [ ] Monitor logs for errors (first hour)
- [ ] Monitor AD performance impact

### Rollback Plan
```powershell
Stop-WebAppPool adquery_pool
Remove-Item D:\inetpub\adquery -Recurse -Force
Copy-Item D:\inetpub\adquery.backup D:\inetpub\adquery -Recurse
Start-WebAppPool adquery_pool
```

---

## Timeline

| Phase | Duration | Can Parallel With |
|-------|----------|-------------------|
| 1. Schema | 1-2 days | - |
| 2. Job Manager | 2-3 days | Phase 6, 7, 9 |
| 3. Background Executor | 2 days | Phase 6, 7, 9 |
| 4. Executor + Progress | 3-4 days | Phase 6, 9 |
| 5. Validator | 1-2 days | Phase 6, 7, 9 |
| 6. Claude Prompt | 1 day | Phase 2, 3, 5 |
| 7. Frontend Polling | 2-3 days | Phase 2, 5, 9 |
| 8. API Endpoints | 2 days | Phase 9 |
| 9. AD Batching | 1-2 days | Phase 2, 3, 4 |
| 10. Testing | 3-4 days | After all |

**Total**: ~18-25 days with 1-2 engineers

**Critical Path**: 1 → 2 → 4 → 8 → 10

---

## Success Criteria

- [ ] Users can query "everyone under CEO" and get all 40K users
- [ ] UI shows live progress during execution
- [ ] Jobs complete in background without blocking other users
- [ ] 3 concurrent large queries run without issues
- [ ] Cancellation works reliably
- [ ] Aggregation summaries accurate for full org
- [ ] Warnings displayed when node limits hit
- [ ] Stress tests pass: 40K in < 10 minutes, < 4GB memory
- [ ] Backward compatibility: existing queries unchanged
- [ ] Zero production errors in first 48 hours

---

## Key Async Architecture Elements

### Job Lifecycle
```
1. POST /api/query/execute
   ↓ 202 Accepted, jobId
2. Background service picks up job
   ↓ ExecuteJobAsync with progress callbacks
3. Frontend polls GET /api/query/jobs/{id}
   ↓ Shows progress updates
4. Job completes
   ↓ Status = completed, results cached
5. User downloads
   ↓ GET /api/query/download/csv/{jobId}
```

### Data Flow
```
QueryController.Execute()
  ↓ creates QueryJob (status=Queued)
  ↓ returns 202 Accepted

QueryJobExecutorService (background)
  ↓ picks up queued job
  ↓ calls DirectoryPlanExecutor with progress callback

DirectoryPlanExecutor.ExecuteExpandReports()
  ↓ after each level: onProgress(nodes, depth, estimate)
  ↓ updates job.NodesProcessed, job.CurrentDepth

Frontend polls /api/query/jobs/{id}
  ↓ reads job.NodesProcessed, job.CurrentDepth
  ↓ displays "Processing level 5... 12,453 of ~40,000 nodes"
```

### Storage
- **Job metadata**: In-memory ConcurrentDictionary (survives app restarts = jobs lost, acceptable for v1)
- **Results**: IMemoryCache with 2-hour expiry
- **Future**: Persist jobs to database for durability

### Concurrency Control
- Semaphore limits concurrent jobs (default: 3)
- Prevents LDAP/memory exhaustion
- Queued jobs wait their turn

---

## Future Enhancements (Post-v1)

- [ ] Persist jobs to database (survive app restarts)
- [ ] SignalR for real-time progress (no polling)
- [ ] Email notification when large job completes
- [ ] Result streaming (return partial results while job runs)
- [ ] Job priority queue (admin queries first)
- [ ] Per-user rate limiting
- [ ] Job history UI (view past queries)

---

**Document Version**: 2.0 (Async Architecture)
**Last Updated**: 2025-10-20
**Status**: Ready for Implementation
