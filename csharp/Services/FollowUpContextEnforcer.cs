using System.Text;
using AdQuery.Orchestrator.Configuration;
using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// The three last-turn components that may compose follow-up context (F01 Slice C1,
/// FOLLOWUP-D2). Ordered here by drop priority: when the composition exceeds the byte
/// cap, whole components are dropped in the fixed order <see cref="Values"/> →
/// <see cref="PlanSummary"/> → <see cref="PriorQuestion"/>, so the prior question is
/// retained longest. Values (the DATA-D1 minimal AD slice) are the most sensitive and
/// least load-bearing for refinement, so they go first.
/// </summary>
public sealed record FollowUpContextComponents(
    string? Values,
    string? PlanSummary,
    string? PriorQuestion);

/// <summary>
/// The authoritative server-side bound on follow-up context (F01 Slice C1, FOLLOWUP-D1).
/// It never persists, logs, or transmits context above <c>FollowUp:MaxContextBytes</c>
/// UTF-8 bytes. It drops whole components in a fixed order and never splits a UTF-8 code
/// point; it never emits a fragment.
/// </summary>
public interface IFollowUpContextEnforcer
{
    /// <summary>
    /// Composes the largest prefix of components (in fixed keep-priority: prior
    /// question, then plan summary, then values) whose UTF-8 encoding fits the cap,
    /// dropping whole components — never a fragment, never a split code point. Returns
    /// <c>null</c> if even the highest-priority single component overflows the cap.
    /// </summary>
    string? Compose(FollowUpContextComponents components);

    /// <summary>
    /// Fail-closed backstop for an already-assembled opaque context string arriving at
    /// the job manager: returns it unchanged when within the cap, otherwise <c>null</c>.
    /// Component boundaries are unknown in an opaque string, so an over-cap value is
    /// dropped entirely rather than truncated into a fragment.
    /// </summary>
    string? EnforceStored(string? context);
}

/// <inheritdoc />
public sealed class FollowUpContextEnforcer : IFollowUpContextEnforcer
{
    private readonly IOptions<FollowUpOptions> _options;

    public FollowUpContextEnforcer(IOptions<FollowUpOptions> options)
    {
        _options = options;
    }

    private int MaxBytes => _options.Value.MaxContextBytes;

    public string? Compose(FollowUpContextComponents components)
    {
        ArgumentNullException.ThrowIfNull(components);

        // Assembly order is fixed and independent of which components survive: prior
        // question, then plan summary, then values. Drop order is the reverse (values
        // first), so we try the candidate subsets from most- to least-complete.
        var question = Normalize(components.PriorQuestion);
        var plan = Normalize(components.PlanSummary);
        var values = Normalize(components.Values);

        string?[] keepAll = { question, plan, values };
        string?[] dropValues = { question, plan };
        string?[] dropPlan = { question };

        foreach (var candidate in new[] { keepAll, dropValues, dropPlan })
        {
            var assembled = Assemble(candidate);
            if (assembled is not null && FitsCap(assembled))
            {
                return assembled;
            }
        }

        // Even the prior question alone overflows: drop context entirely.
        return null;
    }

    public string? EnforceStored(string? context)
    {
        var normalized = Normalize(context);
        if (normalized is null)
        {
            return null;
        }

        return FitsCap(normalized) ? normalized : null;
    }

    private bool FitsCap(string value) => Encoding.UTF8.GetByteCount(value) <= MaxBytes;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Assemble(string?[] components)
    {
        var present = components.Where(component => component is not null).ToArray();
        return present.Length == 0 ? null : string.Join('\n', present);
    }
}
