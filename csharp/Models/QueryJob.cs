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

    /// <summary>
    /// The job this one follows up on, or null when it opens a thread (F04 Slice 6a).
    /// Recorded server-side after the controller's ownership check, so the thread is walkable
    /// from any turn back to its first question without trusting a client-asserted chain.
    /// </summary>
    public string? PreviousJobId { get; set; }

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
    /// <summary>
    /// The completion-time artifact of record holding this job's full result (F04 Slice 7,
    /// F04-D5). Written atomically before the job is marked completed, so a reader that sees
    /// <see cref="JobStatus.Completed"/> sees a path that either resolves to a complete
    /// artifact or to nothing at all. Null when the write failed — the job still completes.
    /// </summary>
    public string? ResultArtifactPath { get; set; }
    public int? TotalRows { get; set; }
    public string? ModelUsed { get; set; }
    public Dictionary<string, object>? Aggregation { get; set; }

    /// <summary>
    /// The model-authored answer text from Narrate (F04 Slice 2, F04-D1). Null when Narrate
    /// failed, timed out, or was skipped — the job still completes with headline, table, and
    /// export, so an absent answer is a degraded presentation, never a failed query.
    /// </summary>
    public string? Answer { get; set; }

    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// True when this job's result stopped short of every matching record at a system limit
    /// (ci-or-1), so <see cref="TotalRows"/> and every figure derived from it is a floor.
    /// Shipped on the completed-job DTO because the chat surface caveats the answer from it
    /// deterministically — the model is told the same fact in the reduction, but a model
    /// instruction is not what stands between a truncated count and a confident sentence.
    /// </summary>
    public bool ResultIsIncomplete { get; set; }

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
