using System;
using System.Collections.Generic;
using System.Linq;
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
    private static FollowUpContextBuilder CreateBuilder(
        int maxBytes = 2000,
        int summaryRowCount = 20,
        int maxPriorQuestions = 1,
        IQueryJobStore? store = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["QueryDefaults:SummaryRowCount"] = summaryRowCount.ToString(),
            })
            .Build();
        var options = Options.Create(new FollowUpOptions
        {
            MaxContextBytes = maxBytes,
            MaxPriorQuestions = maxPriorQuestions,
        });
        var enforcer = new FollowUpContextEnforcer(options, NullLogger<FollowUpContextEnforcer>.Instance);

        return new FollowUpContextBuilder(
            enforcer, store ?? new InMemoryQueryJobStore(), options, configuration);
    }

    /// <summary>
    /// Stores a thread oldest-first and returns its newest turn, chained the way the
    /// controller chains one: each job records the id of the turn it followed.
    /// </summary>
    private static QueryJob StoreThread(IQueryJobStore store, params string[] questionsOldestFirst)
    {
        QueryJob? previous = null;
        for (var i = 0; i < questionsOldestFirst.Length; i++)
        {
            var job = CompletedJob(questionsOldestFirst[i]);
            job.JobId = $"turn-{i}";
            job.PreviousJobId = previous?.JobId;
            store.StoreJob(job);
            previous = job;
        }

        return previous!;
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

    [Fact]
    public void BuildFromPreviousTurn_CarriesTheWholeThread_OldestFirst()
    {
        // F04 Slice 6a: the third turn must still see the first turn's constraint, or a
        // refinement chain loses everything but its immediate predecessor.
        var store = new InMemoryQueryJobStore();
        var newest = StoreThread(store, "everyone under Sanjay", "only with titles");
        var builder = CreateBuilder(maxPriorQuestions: 5, store: store);

        var context = builder.BuildFromPreviousTurn(newest);

        Assert.NotNull(context);
        Assert.Contains("everyone under Sanjay", context);
        Assert.Contains("only with titles", context);
        Assert.True(
            context!.IndexOf("everyone under Sanjay", StringComparison.Ordinal)
                < context.IndexOf("only with titles", StringComparison.Ordinal),
            "the thread must read oldest to newest");
    }

    [Fact]
    public void BuildFromPreviousTurn_ThreadBeyondTheBound_DropsOldestFirst()
    {
        // The bound keeps the questions the current turn is refining, so the oldest fall
        // off — never the newest.
        var store = new InMemoryQueryJobStore();
        var newest = StoreThread(store, "oldest question", "middle question", "newest question");
        var builder = CreateBuilder(maxPriorQuestions: 2, store: store);

        var context = builder.BuildFromPreviousTurn(newest);

        Assert.NotNull(context);
        Assert.DoesNotContain("oldest question", context);
        Assert.Contains("middle question", context);
        Assert.Contains("newest question", context);
    }

    [Fact]
    public void BuildFromPreviousTurn_ThreadNeverCarriesAccumulatedResults()
    {
        // F04-D6: questions accumulate, results do not. Only the last turn's value slice
        // may appear, however long the thread.
        var store = new InMemoryQueryJobStore();

        var older = CompletedJob(
            "first question",
            aggregation: new Dictionary<string, object>
            {
                ["grouped_counts"] = new Dictionary<string, int> { ["OLDER_VALUE_SENTINEL"] = 3 },
            });
        older.JobId = "older";
        store.StoreJob(older);

        var newest = CompletedJob(
            "second question",
            aggregation: new Dictionary<string, object>
            {
                ["grouped_counts"] = new Dictionary<string, int> { ["NEWEST_VALUE"] = 7 },
            });
        newest.JobId = "newest";
        newest.PreviousJobId = "older";
        store.StoreJob(newest);

        var context = CreateBuilder(maxPriorQuestions: 5, store: store).BuildFromPreviousTurn(newest);

        Assert.NotNull(context);
        Assert.Contains("first question", context);
        Assert.Contains("NEWEST_VALUE: 7", context);
        Assert.DoesNotContain("OLDER_VALUE_SENTINEL", context);
    }

    [Fact]
    public void BuildFromPreviousTurn_ThreadStopsAtAForeignTurn()
    {
        // Every link was ownership-checked when it was recorded, so a foreign turn in the
        // chain means corrupted state: stop rather than compose another user's question.
        var store = new InMemoryQueryJobStore();

        var foreign = CompletedJob("FOREIGN_QUESTION_SENTINEL");
        foreign.JobId = "foreign";
        foreign.UserName = "someone-else";
        store.StoreJob(foreign);

        var mine = CompletedJob("my question");
        mine.JobId = "mine";
        mine.PreviousJobId = "foreign";
        store.StoreJob(mine);

        var context = CreateBuilder(maxPriorQuestions: 5, store: store).BuildFromPreviousTurn(mine);

        Assert.NotNull(context);
        Assert.Contains("my question", context);
        Assert.DoesNotContain("FOREIGN_QUESTION_SENTINEL", context);
    }

    [Fact]
    public void BuildFromPreviousTurn_CorruptedCycle_Terminates()
    {
        var store = new InMemoryQueryJobStore();
        var a = CompletedJob("question a");
        a.JobId = "a";
        a.PreviousJobId = "b";
        var b = CompletedJob("question b");
        b.JobId = "b";
        b.PreviousJobId = "a";
        store.StoreJob(a);
        store.StoreJob(b);

        var context = CreateBuilder(maxPriorQuestions: 50, store: store).BuildFromPreviousTurn(a);

        Assert.NotNull(context);
        Assert.Contains("question a", context);
        Assert.Contains("question b", context);
    }

    [Fact]
    public void BuildFromPreviousTurn_MaximumThread_ComposesWithinTheCap_NoComponentDropped()
    {
        // F04-D6's central claim: at the shipped bounds the byte cap never shapes a turn.
        // Every question at the transport maximum, the value slice at its bucket bound, and
        // a plan description at its clip must all still compose.
        var options = new FollowUpOptions();
        var store = new InMemoryQueryJobStore();
        var question = new string('q', AnswerOptions.QuestionTransportCodeUnitLimit);

        QueryJob? previous = null;
        for (var i = 0; i < options.MaxPriorQuestions; i++)
        {
            var job = CompletedJob($"{i:D4}{question[4..]}");
            job.JobId = $"turn-{i}";
            job.PreviousJobId = previous?.JobId;
            store.StoreJob(job);
            previous = job;
        }

        previous!.Plan = new DirectoryQueryPlan
        {
            Description = new string('d', AnswerOptions.MaxDescriptionChars * 2),
            Projection = new ProjectionDefinition
            {
                Aggregation = new AggregationDefinition { Count = true, GroupBy = ["department"] },
            },
        };
        previous.Aggregation = new Dictionary<string, object>
        {
            ["grouped_counts"] = Enumerable
                .Range(0, FollowUpOptions.ValueSliceBuckets)
                .ToDictionary(i => new string((char)('a' + i), AnswerOptions.MaxValueChars * 2), i => i),
        };
        store.StoreJob(previous);

        var builder = CreateBuilder(
            maxBytes: options.MaxContextBytes,
            maxPriorQuestions: options.MaxPriorQuestions,
            store: store);

        var context = builder.BuildFromPreviousTurn(previous);

        // Nothing dropped: every question, the plan summary, and the value slice survive.
        Assert.NotNull(context);
        Assert.Contains("Previous query:", context);
        Assert.Contains("Previous results:", context);
        for (var i = 0; i < options.MaxPriorQuestions; i++)
        {
            Assert.Contains($"{i:D4}", context);
        }
    }
}
