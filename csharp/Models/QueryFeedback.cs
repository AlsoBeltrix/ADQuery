using System;

namespace AdQuery.Orchestrator.Models;

/// <summary>
/// User feedback on query results.
/// </summary>
public class QueryFeedback
{
    public string FeedbackId { get; set; } = Guid.NewGuid().ToString();
    public string JobId { get; set; } = string.Empty;
    public string UserName { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string ModelUsed { get; set; } = string.Empty;

    public FeedbackSentiment Sentiment { get; set; }
    public string? Comment { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    // Retry tracking
    public string? OriginalJobId { get; set; }  // If this is an Opus retry
    public bool UserRequestedRetry { get; set; }

    // Query metadata
    public int? ResultCount { get; set; }
    public int ResponseTimeMs { get; set; }
    public bool ValidationPassed { get; set; } = true;
}

/// <summary>
/// User sentiment about query results.
/// </summary>
public enum FeedbackSentiment
{
    Positive,
    Negative,
    Neutral
}

/// <summary>
/// Request to submit user feedback.
/// </summary>
public class SubmitFeedbackRequest
{
    public string JobId { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string ModelUsed { get; set; } = string.Empty;
    public FeedbackSentiment Sentiment { get; set; }
    public string? Comment { get; set; }
    public string? OriginalJobId { get; set; }
    public bool UserRequestedRetry { get; set; }
    public int? ResultCount { get; set; }
    public int ResponseTimeMs { get; set; }
}

/// <summary>
/// Request to retry query with alternate model.
/// </summary>
public class RetryWithAlternateModelRequest
{
    public string OriginalJobId { get; set; } = string.Empty;
}
