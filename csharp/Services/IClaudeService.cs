using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Service contract for integrating with Claude AI to generate directory plans.
/// </summary>
public interface IClaudeService
{
    /// <summary>
    /// Translate (F04-D1): the conversation becomes one <see cref="DirectoryQueryPlan"/>.
    /// <para>
    /// The server's configured result ceiling is deliberately not a parameter (slice3r2-or-1).
    /// The model translates the *user's* intent; a safety cap is not part of that intent, and
    /// describing one to the model as a count the user asked for makes the resulting
    /// <c>result_limit</c> indistinguishable from a real user request — which is the fact
    /// <see cref="PlanPreprocessor.EnsurePlanLimit"/> reads to decide whether a truncated
    /// answer is incomplete. The cap is applied deterministically after translation instead.
    /// </para>
    /// </summary>
    Task<ClaudeResponse> GenerateExecutionPlanAsync(
        string userQuery,
        string? context = null,
        CancellationToken cancellationToken = default,
        string? modelOverride = null);

    /// <summary>
    /// Narrate (F04 Slice 2, F04-D1): the second model call of a turn. Writes the answer
    /// text from an already-bounded server-built reduction — never rows, never the full
    /// result set. The caller owns the bound; this method only transmits what it is given.
    /// Failure is returned, never thrown, because Narrate must not fail the query.
    /// </summary>
    Task<ClaudeAnswerResponse> GenerateAnswerAsync(
        string reduction,
        CancellationToken cancellationToken = default,
        string? modelOverride = null);

    Task<ClaudeHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Response from the Narrate call (F04 Slice 2). Carries the model-authored answer text
/// only; the raw provider response is logged, never shipped to the browser.
/// </summary>
public class ClaudeAnswerResponse
{
    public bool Success { get; set; }

    /// <summary>The model-authored answer. Null on any failure.</summary>
    public string? Answer { get; set; }

    public string? ErrorMessage { get; set; }

    public TokenUsage TokenUsage { get; set; } = new();

    public long ResponseTimeMs { get; set; }

    public string? ModelUsed { get; set; }
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

    /// <summary>
    /// The model ID that was actually used to generate this response.
    /// </summary>
    public string? ModelUsed { get; set; }
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
