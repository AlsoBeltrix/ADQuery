using System.Linq;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F06 Slice 1 guard: a result that matched nothing must tell the user WHICH CONSTRAINTS
/// produced the zero, not merely that the count is zero.
///
/// Earned by live job `5c1a4abb` ("How many conference rooms in Chelmsford"). The plan
/// filtered `physicalDeliveryOfficeName contains "Chelmsford"` — an attribute populated on
/// exactly 0 of the 147 room mailboxes in this directory — so the filter could not match
/// whatever the true answer was. The user was told zero. A zero meaning "none exist" and a
/// zero meaning "I searched for the wrong thing" were presented identically.
///
/// The reduction already said `RESULT: no matching records.` and already carried the plan
/// description. Neither states the applied predicate, and the description is model-authored
/// prose about intent ("Count conference room objects located in Chelmsford") which restates
/// the question rather than exposing what the engine actually asked the directory. The
/// constraint list is the only component that can make a wrong search visible.
/// </summary>
public sealed class EmptyResultNamesItsConstraintsTests
{
    private static AnswerReductionBuilder Builder() =>
        new(Options.Create(new AnswerOptions()));

    /// <summary>The Chelmsford plan's shape: an AND of a populated-check and two contains.</summary>
    private static DirectoryQueryPlan ChelmsfordPlan() => new()
    {
        Description = "Count conference room objects located in Chelmsford (pure count)",
        Steps =
        {
            new DirectoryPlanStep
            {
                Name = "chelmsford_conference_rooms",
                Operation = "search",
                TargetType = DirectoryObjectType.User,
                Filters =
                {
                    new DirectoryFilter
                    {
                        Operator = "and",
                        Conditions =
                        [
                            new DirectoryFilter { Attribute = "msExchRecipientTypeDetails", Operator = "not_equals", Value = "" },
                            new DirectoryFilter { Attribute = "displayName", Operator = "contains", Value = "Conference" },
                            new DirectoryFilter { Attribute = "physicalDeliveryOfficeName", Operator = "contains", Value = "Chelmsford" },
                        ],
                    },
                },
            },
        },
        Projection = new ProjectionDefinition
        {
            RowStep = "chelmsford_conference_rooms",
            Aggregation = new AggregationDefinition { Count = true },
        },
    };

    private static string BuildFor(DirectoryQueryPlan? plan)
    {
        var headline = HeadlineClassifier.Classify(plan, 0, null, null);
        Assert.Equal(HeadlineKind.None, headline.Kind);

        var reduction = Builder().Build("How many conference rooms in Chelmsford", plan, headline, null);
        Assert.NotNull(reduction);
        return reduction!;
    }

    [Fact]
    public void AnEmptyResult_NamesEveryConstraintTheSearchApplied()
    {
        var reduction = BuildFor(ChelmsfordPlan());

        // The attribute that could not match is the whole point: seeing it is what lets a
        // user say "rooms don't have an office set" and correct the question in one turn.
        Assert.Contains("physicalDeliveryOfficeName", reduction, System.StringComparison.Ordinal);
        Assert.Contains("Chelmsford", reduction, System.StringComparison.Ordinal);

        // Nested conditions must be walked. A filter tree that reports only its top-level
        // "and" node would name nothing at all.
        Assert.Contains("displayName", reduction, System.StringComparison.Ordinal);
        Assert.Contains("msExchRecipientTypeDetails", reduction, System.StringComparison.Ordinal);

        // Labelled so the Narrate prompt can refer to it by name.
        Assert.Contains("CONSTRAINTS APPLIED:", reduction, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyResult_StillCarriesTheNoRecordsHeadline()
    {
        // The constraint list supplements the headline; it must not displace it.
        Assert.Contains("RESULT: no matching records.", BuildFor(ChelmsfordPlan()), System.StringComparison.Ordinal);
    }

    [Fact]
    public void ANonEmptyResult_CarriesNoConstraintList()
    {
        // The list exists to explain a zero. On a result that found something it is noise,
        // and it would spend byte budget the headline and distribution need.
        var plan = ChelmsfordPlan();
        var headline = HeadlineClassifier.Classify(plan, 42, null, null);
        var reduction = Builder().Build("How many conference rooms in Chelmsford", plan, headline, null);

        Assert.NotNull(reduction);
        Assert.DoesNotContain("CONSTRAINTS APPLIED:", reduction!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyResultFromAPlanWithNoFilters_SaysTheSearchWasUnfiltered()
    {
        // Zero rows with no predicate at all is a different fact, and saying "no constraints"
        // is more useful than omitting the line: it tells the user the object type itself
        // returned nothing.
        var plan = new DirectoryQueryPlan
        {
            Description = "All users",
            Steps = { new DirectoryPlanStep { Name = "s1", Operation = "search", TargetType = DirectoryObjectType.User } },
            Projection = new ProjectionDefinition { RowStep = "s1" },
        };

        var reduction = BuildFor(plan);

        Assert.Contains("CONSTRAINTS APPLIED: none", reduction, System.StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyResultWithNoPlan_DoesNotThrow()
    {
        // Legacy jobs carry no plan. The builder must degrade, not fault.
        var headline = HeadlineClassifier.Classify(null, 0, null, null);
        var reduction = Builder().Build("anything", null, headline, null);

        Assert.NotNull(reduction);
        Assert.Contains("RESULT: no matching records.", reduction!, System.StringComparison.Ordinal);
    }

    [Fact]
    public void TheConstraintList_IsBounded()
    {
        // An unbounded list would break the byte accounting AnswerOptions.ReductionCeilingBytes
        // derives from fixed component maxima — the same reasoning that keeps the executor's
        // free-text warnings out of the reduction (ci-or-1).
        var plan = ChelmsfordPlan();
        var root = plan.Steps[0].Filters[0];
        root.Conditions = Enumerable
            .Range(0, 200)
            .Select(i => new DirectoryFilter
            {
                Attribute = "attribute" + i,
                Operator = "contains",
                Value = new string('x', 500),
            })
            .ToList();

        var reduction = BuildFor(plan);

        Assert.Contains("CONSTRAINTS APPLIED:", reduction, System.StringComparison.Ordinal);
        Assert.True(
            System.Text.Encoding.UTF8.GetByteCount(reduction) <= AnswerOptions.ReductionCeilingBytes,
            $"reduction was {System.Text.Encoding.UTF8.GetByteCount(reduction)} bytes, above the derived ceiling.");
    }
}
