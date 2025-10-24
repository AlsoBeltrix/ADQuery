using System;
using System.Collections.Generic;
using System.Threading;

namespace AdQuery.Orchestrator.Models;

/// <summary>
/// Represents an async query job for long-running directory operations.
/// </summary>
public class QueryJob
{
    public string JobId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string? Context { get; set; }
    public int? RequestedResultLimit { get; set; }
    public DirectoryQueryPlan? Plan { get; set; }

    public JobStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    // Progress tracking
    public int NodesProcessed { get; set; }
    public int CurrentDepth { get; set; }
    public int EstimatedTotal { get; set; }
    public string? Phase { get; set; }

    // Results
    public string? ResultsCacheKey { get; set; }
    public int? TotalRows { get; set; }
    public Dictionary<string, object>? Aggregation { get; set; }
    public List<string> Warnings { get; set; } = new();

    // Error handling
    public string? ErrorMessage { get; set; }

    // Cancellation (not serialized)
    [System.Text.Json.Serialization.JsonIgnore]
    public CancellationTokenSource? CancellationSource { get; set; }
}

/// <summary>
/// Job execution status.
/// </summary>
public enum JobStatus
{
    Queued,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Progress update from executor during job execution.
/// </summary>
public class PlanProgressUpdate
{
    public int NodesProcessed { get; set; }
    public int CurrentDepth { get; set; }
    public int? EstimatedRemainingNodes { get; set; }
    public string? Phase { get; set; }
}
