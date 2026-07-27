using System.Collections.Generic;
using System.Linq;
using AdQuery.Orchestrator.Models;
using Microsoft.Extensions.Configuration;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Builds the bounded last-turn follow-up context from a completed prior job
/// (F01 Slice C2, FOLLOWUP-D2). The material is assembled server-side from a job
/// the caller has already resolved and ownership-checked, so last-turn provenance
/// is server-verified rather than client-asserted. Only the single prior turn
/// contributes — never an accumulated transcript. The assembled components are
/// bounded by the C1 <see cref="IFollowUpContextEnforcer.Compose"/> byte cap.
/// </summary>
public interface IFollowUpContextBuilder
{
    /// <summary>
    /// Assembles and byte-bounds the last-turn context from <paramref name="previousJob"/>:
    /// its question, a plan-shape summary, and the DATA-D1 minimal value slice. Returns
    /// <c>null</c> when nothing composable survives the cap.
    /// </summary>
    string? BuildFromPreviousTurn(QueryJob previousJob);
}

/// <inheritdoc />
public sealed class FollowUpContextBuilder : IFollowUpContextBuilder
{
    private readonly IFollowUpContextEnforcer _enforcer;
    private readonly IConfiguration _configuration;

    public FollowUpContextBuilder(IFollowUpContextEnforcer enforcer, IConfiguration configuration)
    {
        _enforcer = enforcer;
        _configuration = configuration;
    }

    // The DATA-D1 minimal value slice is bounded to the same row count the aggregation
    // UI displays (SummaryRowCount), so follow-up never carries more grouped values than
    // the user already saw on screen.
    private int ValueSliceRowCap => _configuration.GetValue<int>("QueryDefaults:SummaryRowCount", 20);

    public string? BuildFromPreviousTurn(QueryJob previousJob)
    {
        System.ArgumentNullException.ThrowIfNull(previousJob);

        // Compose (fixed keep-priority prior-question → plan → values, byte-capped,
        // whole-component drop) is the single bound; this builder only sources the three
        // last-turn components from the resolved prior job.
        return _enforcer.Compose(new FollowUpContextComponents(
            Values: BuildValueSlice(previousJob.Aggregation),
            PlanSummary: BuildPlanSummary(previousJob.Plan),
            PriorQuestion: BuildPriorQuestion(previousJob.Query)));
    }

    private static string? BuildPriorQuestion(string? query)
        => string.IsNullOrWhiteSpace(query) ? null : $"Previous question: {query.Trim()}";

    private static string? BuildPlanSummary(DirectoryQueryPlan? plan)
    {
        if (plan is null)
        {
            return null;
        }

        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(plan.Description))
        {
            parts.Add(plan.Description.Trim());
        }

        var groupBy = plan.Projection?.Aggregation?.GroupBy;
        if (groupBy is { Count: > 0 })
        {
            parts.Add("grouped by " + string.Join(", ", groupBy));
        }

        return parts.Count == 0 ? null : "Previous query: " + string.Join("; ", parts);
    }

    private string? BuildValueSlice(IReadOnlyDictionary<string, object>? aggregation)
    {
        if (aggregation is null ||
            !aggregation.TryGetValue("grouped_counts", out var raw) ||
            raw is not IDictionary<string, int> counts ||
            counts.Count == 0)
        {
            return null;
        }

        var top = counts
            .OrderByDescending(pair => pair.Value)
            .Take(ValueSliceRowCap)
            .Select(pair => $"{pair.Key}: {pair.Value}");

        return "Previous results: " + string.Join(", ", top);
    }
}
