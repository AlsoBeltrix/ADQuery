using System.Collections.Generic;
using System.Text;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F01 Slice C2 guard: <see cref="FollowUpContextBuilder"/> assembles the bounded
/// last-turn context from a resolved prior job — only that job's question, plan-shape
/// summary, and minimal value slice (FOLLOWUP-D2), byte-bounded by the C1
/// <see cref="IFollowUpContextEnforcer.Compose"/> cap. It never carries a prior job's
/// own <c>Context</c> (which would accumulate across turns).
/// </summary>
public sealed class FollowUpContextBuilderTests
{
    private static FollowUpContextBuilder CreateBuilder(int maxBytes = 2000, int summaryRowCount = 20)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["QueryDefaults:SummaryRowCount"] = summaryRowCount.ToString(),
            })
            .Build();
        var enforcer = new FollowUpContextEnforcer(
            Options.Create(new FollowUpOptions { MaxContextBytes = maxBytes }),
            NullLogger<FollowUpContextEnforcer>.Instance);
        return new FollowUpContextBuilder(enforcer, configuration);
    }

    private static QueryJob CompletedJob(
        string query,
        DirectoryQueryPlan? plan = null,
        Dictionary<string, object>? aggregation = null,
        string? priorContext = null)
        => new()
        {
            JobId = "prev",
            UserName = "owner",
            Query = query,
            Plan = plan,
            Aggregation = aggregation,
            Context = priorContext,
            Status = JobStatus.Completed,
        };

    [Fact]
    public void BuildFromPreviousTurn_IncludesQuestionPlanAndValues()
    {
        var builder = CreateBuilder();
        var plan = new DirectoryQueryPlan
        {
            Description = "count users by department",
            Projection = new ProjectionDefinition
            {
                Aggregation = new AggregationDefinition { Count = true, GroupBy = ["department"] },
            },
        };
        var aggregation = new Dictionary<string, object>
        {
            ["grouped_counts"] = new Dictionary<string, int> { ["IT"] = 12, ["HR"] = 8 },
        };

        var context = builder.BuildFromPreviousTurn(
            CompletedJob("how many users per department", plan, aggregation));

        Assert.NotNull(context);
        Assert.Contains("how many users per department", context);
        Assert.Contains("count users by department", context);
        Assert.Contains("IT: 12", context);
        Assert.Contains("HR: 8", context);
    }

    [Fact]
    public void BuildFromPreviousTurn_DoesNotCarryPriorContext()
    {
        // FOLLOWUP-D2: the prior job's own Context (which may already be a prior turn's
        // summary) must never be forwarded, or context would accumulate across turns.
        var builder = CreateBuilder();

        var context = builder.BuildFromPreviousTurn(
            CompletedJob("who is jane", priorContext: "ACCUMULATED_TRANSCRIPT_SENTINEL"));

        Assert.NotNull(context);
        Assert.DoesNotContain("ACCUMULATED_TRANSCRIPT_SENTINEL", context);
    }

    [Fact]
    public void BuildFromPreviousTurn_QuestionOnly_WhenNoPlanOrValues()
    {
        var builder = CreateBuilder();

        var context = builder.BuildFromPreviousTurn(CompletedJob("who is in group X"));

        Assert.Equal("Previous question: who is in group X", context);
    }

    [Fact]
    public void BuildFromPreviousTurn_BoundedByByteCap()
    {
        // A tiny cap drops the whole composition (even the question overflows): fail-closed.
        var builder = CreateBuilder(maxBytes: 4);

        var context = builder.BuildFromPreviousTurn(CompletedJob("a very long prior question"));

        Assert.Null(context);
    }

    [Fact]
    public void BuildFromPreviousTurn_ValueSlice_BoundedToSummaryRowCount()
    {
        // The minimal value slice never carries more groups than the aggregation UI shows.
        var builder = CreateBuilder(summaryRowCount: 2);
        var grouped = new Dictionary<string, int>
        {
            ["A"] = 50,
            ["B"] = 40,
            ["C"] = 30,
            ["D"] = 20,
        };
        var aggregation = new Dictionary<string, object> { ["grouped_counts"] = grouped };

        var context = builder.BuildFromPreviousTurn(CompletedJob("group counts", aggregation: aggregation));

        Assert.NotNull(context);
        Assert.Contains("A: 50", context);
        Assert.Contains("B: 40", context);
        // Beyond the summary row cap, the lower-ranked groups are excluded.
        Assert.DoesNotContain("C: 30", context);
        Assert.DoesNotContain("D: 20", context);
    }

    [Fact]
    public void BuildFromPreviousTurn_DecodesCompositeGroupKeys()
    {
        // slice1r2-or-2: the escaped composite key is transport. Sent verbatim, the model
        // would be told the department is literally "R&D\|Labs|Boston" and could repeat
        // that back as a directory value.
        var builder = CreateBuilder();
        var plan = new DirectoryQueryPlan
        {
            Description = "count users by department and city",
            Projection = new ProjectionDefinition
            {
                Aggregation = new AggregationDefinition { Count = true, GroupBy = ["department", "city"] },
            },
        };
        var aggregation = new Dictionary<string, object>
        {
            ["grouped_counts"] = new Dictionary<string, int>
            {
                [GroupKey.Compose(["R&D|Labs", "Boston"])] = 9,
            },
        };

        var context = builder.BuildFromPreviousTurn(
            CompletedJob("how many per department and city", plan, aggregation));

        Assert.NotNull(context);
        Assert.Contains("R&D|Labs / Boston: 9", context);
        Assert.DoesNotContain(@"\|", context);
    }

    [Fact]
    public void BuildFromPreviousTurn_ResultFitsByteCap()
    {
        var builder = CreateBuilder(maxBytes: 200);
        var context = builder.BuildFromPreviousTurn(CompletedJob("who is in the finance team"));

        Assert.NotNull(context);
        Assert.True(Encoding.UTF8.GetByteCount(context!) <= 200);
    }
}
