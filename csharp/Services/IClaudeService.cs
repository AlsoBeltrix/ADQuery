using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Service contract for integrating with Claude AI to generate directory plans.
/// </summary>
public interface IClaudeService
{
    Task<ClaudeResponse> GenerateExecutionPlanAsync(
        string userQuery,
        string? context = null,
        int? requestedResultLimit = null,
        CancellationToken cancellationToken = default);

    Task<ClaudeHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Response from Claude API containing a directory query plan.
/// </summary>
public class ClaudeResponse
{
    public bool Success { get; set; }

    public DirectoryQueryPlan? Plan { get; set; }

    public string RawResponse { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public TokenUsage TokenUsage { get; set; } = new();

    public long ResponseTimeMs { get; set; }
}

/// <summary>
/// Health check result for Claude service.
/// </summary>
public class ClaudeHealthResult
{
    public bool IsHealthy { get; set; }

    public long ResponseTimeMs { get; set; }

    public bool JsonParsingWorking { get; set; }

    public string? ErrorMessage { get; set; }

    public DateTime? LastSuccessfulResponse { get; set; }
}

/// <summary>
/// Token usage statistics from Claude API.
/// </summary>
public class TokenUsage
{
    public int InputTokens { get; set; }

    public int OutputTokens { get; set; }

    public int TotalTokens => InputTokens + OutputTokens;
}
