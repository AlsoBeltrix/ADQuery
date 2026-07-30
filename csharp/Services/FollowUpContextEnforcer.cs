using System.Text;
using AdQuery.Orchestrator.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// The three last-turn components that may compose follow-up context (F01 Slice C1,
/// FOLLOWUP-D2), in assembly order: prior question, plan summary, values.
/// <para>
/// Each component carries its own semantic bound and the byte cap is only a backstop
/// (F04-D6), so there is no drop priority among them — an over-cap composition means a
/// component bound is broken, not that the context needs trimming.
/// </para>
/// </summary>
public sealed record FollowUpContextComponents(
    string? Values,
    string? PlanSummary,
    string? PriorQuestion);

/// <summary>
/// The authoritative server-side bound on follow-up context (F01 Slice C1, FOLLOWUP-D1).
/// It never persists, logs, or transmits context above <c>FollowUp:MaxContextBytes</c>
/// UTF-8 bytes, and never emits a fragment or splits a UTF-8 code point.
/// </summary>
public interface IFollowUpContextEnforcer
{
    /// <summary>
    /// Assembles the components in fixed order and returns the result when it fits the
    /// byte cap.
    /// <para>
    /// F04 Slice 6a (F04-D6): the cap is a backstop, not a shaper. The component bounds —
    /// the max-prior-questions knob, the value slice's bucket bound, the headline's group
    /// bound — decide how much context is sent, and
    /// <see cref="Configuration.FollowUpOptionsValidator"/> rejects at startup any cap
    /// below what those bounds can compose. Overflow is therefore a broken component
    /// bound, so this <em>logs an error and returns <c>null</c></em> rather than quietly
    /// returning a smaller context. The earlier silent values → plan → question drop
    /// ladder is retired: it made the failure look routine.
    /// </para>
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
    private readonly ILogger<FollowUpContextEnforcer> _logger;

    public FollowUpContextEnforcer(
        IOptions<FollowUpOptions> options,
        ILogger<FollowUpContextEnforcer> logger)
    {
        _options = options;
        _logger = logger;
    }

    private int MaxBytes => _options.Value.MaxContextBytes;

    public string? Compose(FollowUpContextComponents components)
    {
        ArgumentNullException.ThrowIfNull(components);

        var assembled = Assemble(
        [
            Normalize(components.PriorQuestion),
            Normalize(components.PlanSummary),
            Normalize(components.Values),
        ]);

        if (assembled is null || FitsCap(assembled))
        {
            return assembled;
        }

        // F04-D6: startup validation sizes the cap above what the component bounds can
        // compose, so reaching here means one of those bounds is broken. Log it as the
        // defect it is — sizes only, never the context itself (FOLLOWUP-D1 governs what
        // may be logged) — and fail closed rather than send a quietly smaller context.
        _logger.LogError(
            "Follow-up context composed {ComposedBytes} UTF-8 bytes, above the {MaxContextBytes}-byte cap; a component bound is broken (question {QuestionBytes}, plan summary {PlanBytes}, values {ValuesBytes}). Context dropped.",
            Encoding.UTF8.GetByteCount(assembled),
            MaxBytes,
            ByteCount(components.PriorQuestion),
            ByteCount(components.PlanSummary),
            ByteCount(components.Values));

        return null;
    }

    private static int ByteCount(string? value)
        => value is null ? 0 : Encoding.UTF8.GetByteCount(value);

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
