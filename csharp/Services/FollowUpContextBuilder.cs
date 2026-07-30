using System.Collections.Generic;
using System.Linq;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Builds the bounded follow-up context from a completed prior job (F01 Slice C2,
/// FOLLOWUP-D2; extended by F04 Slice 6a). The material is assembled server-side from a job
/// the caller has already resolved and ownership-checked, so provenance is server-verified
/// rather than client-asserted. The assembled components are bounded by the C1
/// <see cref="IFollowUpContextEnforcer.Compose"/> byte cap.
/// <para>
/// F04 Slice 6a: the <em>questions</em> component carries the thread, oldest to newest, up
/// to <see cref="FollowUpOptions.MaxPriorQuestions"/>. Results do not accumulate — the
/// DATA-D1 value slice and the executed-plan summary stay last-turn-bounded (F04-D6).
/// </para>
/// </summary>
public interface IFollowUpContextBuilder
{
    /// <summary>
    /// Assembles and byte-bounds the context for a turn following <paramref name="previousJob"/>:
    /// the thread's accumulated questions, the prior turn's plan-shape summary, and its
    /// DATA-D1 minimal value slice. Returns <c>null</c> when nothing composable survives.
    /// </summary>
    string? BuildFromPreviousTurn(QueryJob previousJob);
}

/// <inheritdoc />
public sealed class FollowUpContextBuilder : IFollowUpContextBuilder
{
    private readonly IFollowUpContextEnforcer _enforcer;
    private readonly IQueryJobStore _store;
    private readonly IOptions<FollowUpOptions> _options;
    private readonly IConfiguration _configuration;

    public FollowUpContextBuilder(
        IFollowUpContextEnforcer enforcer,
        IQueryJobStore store,
        IOptions<FollowUpOptions> options,
        IConfiguration configuration)
    {
        _enforcer = enforcer;
        _store = store;
        _options = options;
        _configuration = configuration;
    }

    // The DATA-D1 minimal value slice is bounded to the same row count the aggregation
    // UI displays (SummaryRowCount), so follow-up never carries more grouped values than
    // the user already saw on screen. Clamped to the bucket count the byte cap's derivation
    // assumes, so raising SummaryRowCount widens the on-screen table but not this slice.
    private int ValueSliceRowCap => System.Math.Min(
        _configuration.GetValue<int>("QueryDefaults:SummaryRowCount", FollowUpOptions.ValueSliceBuckets),
        FollowUpOptions.ValueSliceBuckets);

    public string? BuildFromPreviousTurn(QueryJob previousJob)
    {
        System.ArgumentNullException.ThrowIfNull(previousJob);

        // Compose (fixed assembly order, byte-capped, over-cap logged and dropped whole) is
        // the single bound; this builder only sources the components. Questions come from
        // the thread, the plan summary and value slice from the last turn alone.
        return _enforcer.Compose(new FollowUpContextComponents(
            Values: BuildValueSlice(
                previousJob.Aggregation,
                previousJob.Plan?.Projection?.Aggregation?.GroupBy?.Count ?? 1),
            PlanSummary: BuildPlanSummary(previousJob.Plan),
            PriorQuestion: BuildThreadQuestions(previousJob)));
    }

    /// <summary>
    /// Walks the thread back from <paramref name="previousJob"/> and renders its questions
    /// oldest-first. When the thread is longer than the knob allows, the <em>oldest</em>
    /// questions fall off: the walk simply stops, so the newest questions — the ones the
    /// current turn refines — are the ones retained (F04-D6).
    /// </summary>
    private string? BuildThreadQuestions(QueryJob previousJob)
    {
        var maxPriorQuestions = _options.Value.MaxPriorQuestions;
        if (maxPriorQuestions <= 0)
        {
            return null;
        }

        // Newest-first while walking, then reversed: the model reads the thread in the
        // order it was asked.
        var questions = new List<string>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        QueryJob? turn = previousJob;

        while (turn is not null && questions.Count < maxPriorQuestions)
        {
            // A cycle cannot arise from the controller's forward-only chaining, but the
            // chain is walked from stored state, so refuse to loop on a corrupted one.
            if (!seen.Add(turn.JobId))
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(turn.Query))
            {
                questions.Add(turn.Query.Trim());
            }

            turn = string.IsNullOrWhiteSpace(turn.PreviousJobId)
                ? null
                : _store.GetJob(turn.PreviousJobId);

            // The thread stays within one user by construction: every link was
            // ownership-checked at the controller when it was recorded.
            if (turn is not null && !turn.UserName.Equals(previousJob.UserName, System.StringComparison.OrdinalIgnoreCase))
            {
                break;
            }
        }

        if (questions.Count == 0)
        {
            return null;
        }

        questions.Reverse();

        return questions.Count == 1
            ? "Previous question: " + questions[0]
            : "Previous questions, oldest first:\n"
                + string.Join('\n', questions.Select(question => "- " + question));
    }

    private static string? BuildPlanSummary(DirectoryQueryPlan? plan)
    {
        if (plan is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(plan.Description))
        {
            parts.Add(Clip(plan.Description.Trim(), AnswerOptions.MaxDescriptionChars));
        }

        var groupBy = plan.Projection?.Aggregation?.GroupBy;
        if (groupBy is { Count: > 0 })
        {
            parts.Add("grouped by " + string.Join(", ", groupBy));
        }

        return parts.Count == 0 ? null : "Previous query: " + string.Join("; ", parts);
    }

    private string? BuildValueSlice(IReadOnlyDictionary<string, object>? aggregation, int groupByFieldCount)
    {
        if (aggregation is null ||
            !aggregation.TryGetValue("grouped_counts", out var raw) ||
            raw is not IDictionary<string, int> counts ||
            counts.Count == 0)
        {
            return null;
        }

        // Keys are decoded before they leave for the model (slice1r2-or-2): the escaped
        // composite is transport, and sending it would show the model altered AD values.
        var top = counts
            .OrderByDescending(pair => pair.Value)
            .Take(ValueSliceRowCap)
            .Select(pair =>
                $"{Clip(GroupKey.ToDisplay(pair.Key, groupByFieldCount), AnswerOptions.MaxValueChars)}: {pair.Value}");

        return "Previous results: " + string.Join(", ", top);
    }

    // The fixed-component budget the byte cap is derived from assumes these clips; without
    // them a model-authored description or an unusually long AD value could overrun it.
    private static string Clip(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars];
}
