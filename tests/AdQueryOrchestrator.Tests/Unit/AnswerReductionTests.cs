using System.Collections.Generic;
using System.Linq;
using System.Text;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F04 Slice 2 guard (F04-D1, f04-or-3). The Narrate input is the bounded server-built
/// reduction and nothing else: it carries the question, the plan description, the B1
/// headline, and the distribution scalars — never result rows, never the full set — and
/// it is byte-capped with whole-component drops rather than truncated to a fragment.
///
/// The distribution scalars are load-bearing, not decorative: <c>HeadlineResult</c> keeps
/// only the ten largest buckets, so without them a near-unique distribution and a
/// concentrated one produce the same reduction and the model cannot tell them apart.
/// </summary>
public sealed class AnswerReductionTests
{
    private const string Question = "what is the most common value of extensionAttribute1?";

    private static AnswerReductionBuilder Builder(int? maxBytes = null)
        => new(Options.Create(maxBytes is null
            ? new AnswerOptions()
            : new AnswerOptions { MaxReductionBytes = maxBytes.Value }));

    private static DirectoryQueryPlan GroupedPlan(params string[] groupBy) => new()
    {
        Description = "Count all users by extensionAttribute1",
        Steps = { new DirectoryPlanStep { Step = 1, Name = "s1", Operation = "search" } },
        Projection = new ProjectionDefinition
        {
            RowStep = "s1",
            Columns = { new ProjectionColumn { Name = "Name", Attribute = "displayName" } },
            Aggregation = new AggregationDefinition { Count = true, GroupBy = groupBy.ToList() },
        },
    };

    /// <summary>
    /// The extensionAttribute1 shape from the F04-D2 evidence: 47,388 rows over ~27k
    /// buckets, 26,612 of them singletons, 7,150 blank, and one real bucket (Contractor)
    /// far larger than the rest. The headline sees only the top ten.
    /// </summary>
    private static (Dictionary<string, object> Aggregation, int TotalRows) NearUnique()
    {
        var counts = new Dictionary<string, int>
        {
            ["Contractor"] = 6100,
            ["(empty)"] = 7150,
        };

        for (var i = 0; i < 26612; i++)
        {
            counts[$"value-{i}"] = 1;
        }

        var accountedFor = counts.Values.Sum();
        counts["Employee"] = 47388 - accountedFor;

        return (new Dictionary<string, object> { ["grouped_counts"] = counts }, 47388);
    }

    private static HeadlineResult HeadlineFor(
        DirectoryQueryPlan plan, Dictionary<string, object> aggregation, int totalRows)
        => HeadlineClassifier.Classify(plan, totalRows, aggregation, firstRow: null);

    [Fact]
    public void NearUniqueGroupedResult_CarriesTheDistinguishingCounts_WithinTheCap()
    {
        var plan = GroupedPlan("extensionAttribute1");
        var (aggregation, totalRows) = NearUnique();
        var headline = HeadlineFor(plan, aggregation, totalRows);
        var distribution = DistributionSummarizer.Summarize(aggregation, 1, totalRows);

        var reduction = Builder().Build(Question, plan, headline, distribution);

        Assert.NotNull(reduction);
        Assert.Contains("26612 values occur exactly once", reduction);
        Assert.Contains("7150 rows blank", reduction);
        Assert.Contains("26615 distinct values", reduction);
        Assert.Contains("47388 rows", reduction);
        Assert.True(
            Encoding.UTF8.GetByteCount(reduction!) <= new AnswerOptions().MaxReductionBytes,
            "the reduction must fit the cap it is sent under");
    }

    [Fact]
    public void WithoutTheDistributionSummary_NearUniqueIsIndistinguishableFromConcentrated()
    {
        // The non-vacuity argument for the summary, stated as an assertion rather than
        // asserted about. Two distributions share an identical top ten and an identical
        // total, and differ only in the tail the headline discards: one is 26,612
        // singletons, the other is a few hundred mid-sized buckets. Without the scalars
        // the two reductions are byte-identical, so no model could distinguish them.
        var plan = GroupedPlan("extensionAttribute1");
        var (nearUnique, concentrated, totalRows) = SameTopTenDifferentTails();

        var withoutSummaryA = Builder().Build(
            Question, plan, HeadlineFor(plan, nearUnique, totalRows), distribution: null);
        var withoutSummaryB = Builder().Build(
            Question, plan, HeadlineFor(plan, concentrated, totalRows), distribution: null);

        Assert.NotNull(withoutSummaryA);
        Assert.Equal(withoutSummaryA, withoutSummaryB);

        // With the scalars they are distinguishable — and specifically on the shape that
        // decides the answer: singleton share and blank count.
        var withSummaryA = Builder().Build(
            Question,
            plan,
            HeadlineFor(plan, nearUnique, totalRows),
            DistributionSummarizer.Summarize(nearUnique, 1, totalRows));
        var withSummaryB = Builder().Build(
            Question,
            plan,
            HeadlineFor(plan, concentrated, totalRows),
            DistributionSummarizer.Summarize(concentrated, 1, totalRows));

        Assert.NotEqual(withSummaryA, withSummaryB);
        Assert.Contains("26612 values occur exactly once", withSummaryA);
        Assert.Contains("0 values occur exactly once", withSummaryB);
    }

    /// <summary>
    /// Two aggregations with the same total and the same ten largest buckets, differing
    /// only in the tail: 26,612 singletons versus 133 buckets of 200 plus a remainder.
    /// Every tail bucket is smaller than the smallest top-ten bucket, so the headline
    /// truncation yields the same ten pairs for both.
    /// </summary>
    private static (Dictionary<string, object> NearUnique, Dictionary<string, object> Concentrated, int TotalRows)
        SameTopTenDifferentTails()
    {
        const int singletons = 26612;

        Dictionary<string, int> Head() => new()
        {
            ["(empty)"] = 7150,
            ["Contractor"] = 6100,
            ["Employee"] = 5000,
            ["head-0"] = 500,
            ["head-1"] = 500,
            ["head-2"] = 500,
            ["head-3"] = 500,
            ["head-4"] = 500,
            ["head-5"] = 500,
            ["head-6"] = 500,
        };

        var nearUnique = Head();
        for (var i = 0; i < singletons; i++)
        {
            nearUnique[$"tail-{i}"] = 1;
        }

        var concentrated = Head();
        var remaining = singletons;
        for (var i = 0; remaining >= 200; i++, remaining -= 200)
        {
            concentrated[$"tail-{i}"] = 200;
        }

        if (remaining > 0)
        {
            concentrated["tail-remainder"] = remaining;
        }

        var totalRows = nearUnique.Values.Sum();
        Assert.Equal(totalRows, concentrated.Values.Sum());

        return (
            new Dictionary<string, object> { ["grouped_counts"] = nearUnique },
            new Dictionary<string, object> { ["grouped_counts"] = concentrated },
            totalRows);
    }

    [Fact]
    public void Reduction_NeverCarriesResultRows()
    {
        // The rows exist and are not passed in at all — the builder's signature admits no
        // row collection. This asserts the observable consequence: a value that appears
        // only in a row, never in the headline or the summary, cannot reach the model.
        var plan = GroupedPlan("department");
        var aggregation = new Dictionary<string, object>
        {
            ["grouped_counts"] = new Dictionary<string, int> { ["Finance"] = 2 },
        };
        var headline = HeadlineFor(plan, aggregation, 2);

        var reduction = Builder().Build(
            "who is in Finance?", plan, headline, DistributionSummarizer.Summarize(aggregation, 1, 2));

        Assert.NotNull(reduction);
        Assert.DoesNotContain("ROW_ONLY_SENTINEL", reduction);
        Assert.Contains("Finance: 2", reduction);
    }

    [Fact]
    public void OverCapReduction_DropsWholeComponents_KeepingTheQuestionLongest()
    {
        var plan = GroupedPlan("extensionAttribute1");
        var (aggregation, totalRows) = NearUnique();
        var headline = HeadlineFor(plan, aggregation, totalRows);
        var distribution = DistributionSummarizer.Summarize(aggregation, 1, totalRows);

        var full = Builder().Build(Question, plan, headline, distribution)!;

        // A cap just under the full composition drops the headline (the AD values) first.
        var tight = Builder(Encoding.UTF8.GetByteCount(full) - 1)
            .Build(Question, plan, headline, distribution);

        Assert.NotNull(tight);
        Assert.DoesNotContain("LARGEST VALUES", tight);
        Assert.Contains("DISTRIBUTION:", tight);
        Assert.Contains("QUESTION:", tight);
    }

    [Fact]
    public void CapTooSmallForAnyResultEvidence_YieldsNoReduction_RatherThanAFactlessQuestion()
    {
        // slice7-or-4. The plan description states what was asked of the directory, never
        // what came back, so {question, plan description} is a question and no facts to
        // answer it with — and Narrate treats any non-blank reduction as narratable, so the
        // model's invention would render above a correct headline and table. The ladder
        // therefore stops at the last rung carrying result evidence.
        var plan = GroupedPlan("extensionAttribute1");
        var (aggregation, totalRows) = NearUnique();
        var headline = HeadlineFor(plan, aggregation, totalRows);
        var distribution = DistributionSummarizer.Summarize(aggregation, 1, totalRows);

        // Exactly the size of the factless composition: enough for it, short of every
        // composition that carries the distribution or the headline.
        var factless = "QUESTION: " + Question + "\nQUERY RUN: " + plan.Description;

        Assert.Null(Builder(Encoding.UTF8.GetByteCount(factless))
            .Build(Question, plan, headline, distribution));
    }

    [Theory]
    [InlineData(HeadlineKind.Count)]
    [InlineData(HeadlineKind.Record)]
    public void NonGroupedResult_UnderACapThatExcludesTheHeadline_YieldsNoReduction(string kind)
    {
        // slice2-or-1. The evidence floor is a property of the composition, not of the rung
        // that produced it. DistributionSummarizer returns null for every result that is not
        // a grouped aggregation, so on a count or a single record the headline is the only
        // evidence there is — and dropping it leaves exactly the factless composition
        // slice7-or-4 ruled out, at a cap the validator accepts.
        var plan = new DirectoryQueryPlan
        {
            Description = "Count all contractors in Bangalore",
            Steps = { new DirectoryPlanStep { Step = 1, Name = "s1", Operation = "search" } },
        };

        var headline = string.Equals(kind, HeadlineKind.Count, System.StringComparison.Ordinal)
            ? new HeadlineResult { Kind = HeadlineKind.Count, Count = 412 }
            : new HeadlineResult
            {
                Kind = HeadlineKind.Record,
                Record = new Dictionary<string, object?> { ["displayName"] = "Priya Raman" },
            };

        const string Asked = "how many contractors are in Bangalore?";
        var factless = "QUESTION: " + Asked + "\nQUERY RUN: " + plan.Description;

        // Fits the question and the description; short of anything carrying the headline.
        var reduction = Builder(Encoding.UTF8.GetByteCount(factless))
            .Build(Asked, plan, headline, distribution: null);

        Assert.Null(reduction);
    }

    [Fact]
    public void CapTooSmallForEvenTheQuestion_YieldsNoReduction()
    {
        // Fail-closed: Narrate is skipped rather than sent a fragment.
        var plan = GroupedPlan("department");
        var headline = new HeadlineResult { Kind = HeadlineKind.Count, Count = 3 };

        Assert.Null(Builder(maxBytes: 4).Build(Question, plan, headline, distribution: null));
    }

    [Fact]
    public void EveryReduction_FitsTheCeilingByConstruction()
    {
        // The default cap is derived from the builder's own component maxima, so a
        // worst-case reduction — maximum question, maximum description, maximum record
        // fields at maximum width — must still fit it.
        var longQuestion = new string('q', AnswerOptions.QuestionTransportCodeUnitLimit);
        var plan = new DirectoryQueryPlan
        {
            Description = new string('d', AnswerOptions.MaxDescriptionChars * 4),
            Steps = { new DirectoryPlanStep { Step = 1, Name = "s1", Operation = "search" } },
        };

        var record = new Dictionary<string, object?>();
        for (var i = 0; i < AnswerOptions.MaxRecordFields * 3; i++)
        {
            record[new string((char)('a' + (i % 26)), AnswerOptions.MaxValueChars * 2)] =
                new string('v', AnswerOptions.MaxValueChars * 2);
        }

        var reduction = Builder().Build(
            longQuestion,
            plan,
            new HeadlineResult { Kind = HeadlineKind.Record, Record = record },
            new DistributionSummary { TotalRows = 1, DistinctBuckets = 1, SingletonBuckets = 1 });

        Assert.NotNull(reduction);
        Assert.True(
            Encoding.UTF8.GetByteCount(reduction!) <= AnswerOptions.ReductionCeilingBytes,
            $"a worst-case reduction ({Encoding.UTF8.GetByteCount(reduction!)} bytes) must fit the declared ceiling ({AnswerOptions.ReductionCeilingBytes})");
    }

    [Fact]
    public void GroupedReduction_NeverCarriesMoreThanTheHeadlineBucketBound()
    {
        var plan = GroupedPlan("department");
        var (aggregation, totalRows) = NearUnique();
        var headline = HeadlineFor(plan, aggregation, totalRows);

        var reduction = Builder().Build(Question, plan, headline, distribution: null)!;

        var listed = reduction[(reduction.IndexOf("LARGEST VALUES", System.StringComparison.Ordinal))..]
            .Split(',')
            .Length;

        Assert.True(listed <= AnswerOptions.MaxGroupBuckets, $"{listed} buckets exceeded the bound");
        Assert.Equal(AnswerOptions.MaxGroupBuckets, HeadlineClassifier.MaxHeadlineGroups);
    }

    [Fact]
    public void EmptyResult_ReducesToAPlainStatement()
    {
        var reduction = Builder().Build(
            "who is in Atlantis?",
            GroupedPlan("l"),
            new HeadlineResult { Kind = HeadlineKind.None },
            distribution: null);

        Assert.Contains("no matching records", reduction);
    }
}
