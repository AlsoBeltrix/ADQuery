using System.Collections.Generic;
using System.Linq;
using System.Text;
using AdQuery.Orchestrator.Controllers;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F04 Slice 1b guard (F04-D4). Grouping folds case by default, so the variants of one
/// value are one bucket displayed under its most frequent spelling, with the distinct
/// spelling count carried so the inconsistency stays discoverable. A boolean on the
/// aggregation restores exact-case grouping for the questions that are about the
/// variants themselves.
/// </summary>
public sealed class CaseFoldedGroupingTests
{
    private const string Attribute = "employeeType";

    private static DirectoryQueryPlan GroupedPlan(bool caseSensitive = false) => new()
    {
        Steps = { new DirectoryPlanStep { Name = "s1", Operation = "search" } },
        Projection = new ProjectionDefinition
        {
            RowStep = "s1",
            Columns = { new ProjectionColumn { Name = Attribute, Attribute = Attribute } },
            Aggregation = new AggregationDefinition
            {
                Count = true,
                GroupBy = { Attribute },
                CaseSensitive = caseSensitive,
            },
        },
    };

    private static List<Dictionary<string, object?>> Rows(params string?[] values)
        => values
            .Select(value => new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase)
            {
                [Attribute] = value,
            })
            .ToList();

    /// <summary>
    /// The executor's per-row group values (slice1r2-or-1), one single-field entry per row.
    /// </summary>
    private static List<IReadOnlyList<string?>> GroupValues(params string?[] values)
        => values.Select(IReadOnlyList<string?> (value) => new[] { value }).ToList();

    /// <summary>
    /// Folds a case-grouped plan over <paramref name="values"/>, supplying both the rows
    /// and the matching record-sourced group values.
    /// </summary>
    private static Dictionary<string, object>? Fold(DirectoryQueryPlan plan, params string?[] values)
        => QueryJobManager.ComputeSettledAggregation(plan, Rows(values), GroupValues(values));

    private static Dictionary<string, int> Counts(Dictionary<string, object>? aggregation)
    {
        Assert.NotNull(aggregation);
        return Assert.IsType<Dictionary<string, int>>(aggregation!["grouped_counts"]);
    }

    private static Dictionary<string, int>? Spellings(Dictionary<string, object>? aggregation)
        => aggregation!.TryGetValue("grouped_spellings", out var raw)
            ? Assert.IsType<Dictionary<string, int>>(raw)
            : null;

    [Fact]
    public void CaseVariants_FoldIntoOneBucket_KeepingBlanksSeparate()
    {
        // Pre-fix, ordinal keying reported Contractor/contractor/CONTRACTOR as three
        // buckets, fragmenting what a human means by one value.
        var aggregation = Fold(GroupedPlan(), "Contractor", "contractor", "CONTRACTOR", null);

        var counts = Counts(aggregation);

        Assert.Equal(2, counts.Count);
        Assert.Equal(1, counts["(empty)"]);

        // All three spellings appear once, so the ordinal tie-break picks the display key.
        Assert.Equal(3, counts["CONTRACTOR"]);
        Assert.Equal(3, Spellings(aggregation)!["CONTRACTOR"]);
    }

    [Fact]
    public void DisplayKey_IsTheMostFrequentSpelling_TiesBrokenOrdinally()
    {
        // The display key is a property of the folded group's members, which is why the
        // fold cannot be a dictionary comparer: a comparer sees only the first key in.
        var byFrequency = Fold(GroupedPlan(), "cwk", "CWK", "CWK");

        Assert.Equal(new[] { "CWK" }, Counts(byFrequency).Keys);

        var byTie = Fold(GroupedPlan(), "cwk", "CWK");

        // Ordinal ordering puts uppercase first, so the tie resolves stably.
        Assert.Equal(new[] { "CWK" }, Counts(byTie).Keys);
    }

    [Fact]
    public void SingleSpellingBuckets_CarryNoSpellingPayload()
    {
        // A "1" per bucket would duplicate the distribution at no information gain — a
        // near-unique attribute has tens of thousands of single-spelling buckets.
        var aggregation = Fold(GroupedPlan(), "CWK", "FTE", "FTE");

        Assert.Equal(2, Counts(aggregation).Count);
        Assert.Null(Spellings(aggregation));
    }

    [Fact]
    public void CaseSensitiveFlag_YieldsSeparateBucketsPerSpelling()
    {
        // Prove the mode exists: ignoring the flag silently gives the folded answer.
        var aggregation = Fold(
            GroupedPlan(caseSensitive: true), "Contractor", "contractor", "CONTRACTOR", null);

        var counts = Counts(aggregation);

        Assert.Equal(4, counts.Count);
        Assert.Equal(1, counts["Contractor"]);
        Assert.Equal(1, counts["contractor"]);
        Assert.Equal(1, counts["CONTRACTOR"]);
        Assert.Null(Spellings(aggregation));
    }

    [Fact]
    public void MultiFieldGrouping_FoldsEveryComponent_WithoutColliding()
    {
        // The fold applies to the composed key, so it must not blur the field boundary
        // the composite encoding depends on (slice1-or-2).
        var plan = new DirectoryQueryPlan
        {
            Steps = { new DirectoryPlanStep { Name = "s1", Operation = "search" } },
            Projection = new ProjectionDefinition
            {
                RowStep = "s1",
                Columns =
                {
                    new ProjectionColumn { Name = "department", Attribute = "department" },
                    new ProjectionColumn { Name = "city", Attribute = "city" },
                },
                Aggregation = new AggregationDefinition { Count = true, GroupBy = { "department", "city" } },
            },
        };

        var rows = new List<Dictionary<string, object?>>
        {
            new(System.StringComparer.OrdinalIgnoreCase) { ["department"] = "IT", ["city"] = "Dublin" },
            new(System.StringComparer.OrdinalIgnoreCase) { ["department"] = "it", ["city"] = "DUBLIN" },
            new(System.StringComparer.OrdinalIgnoreCase) { ["department"] = "IT", ["city"] = "Cork" },
        };

        var groupValues = rows
            .Select(IReadOnlyList<string?> (row) => new[] { row["department"] as string, row["city"] as string })
            .ToList();

        var aggregation = QueryJobManager.ComputeSettledAggregation(plan, rows, groupValues)!;
        var counts = Counts(aggregation);

        Assert.Equal(2, counts.Count);
        Assert.Equal(2, counts[GroupKey.Compose(["IT", "Dublin"])]);
        Assert.Equal(1, counts[GroupKey.Compose(["IT", "Cork"])]);
    }

    [Fact]
    public void FoldedExport_CarriesTheSpellingCount_OnlyWhenABucketMergedSpellings()
    {
        var plan = GroupedPlan();
        var aggregation = Fold(plan, "Contractor", "Contractor", "contractor", "CONTRACTOR", "FTE")!;

        var csv = Encoding.UTF8.GetString(QueryController.GenerateFileContent(
            [], [], "csv", aggregation, warnings: null, metadata: null,
            groupByFields: plan.Projection!.Aggregation!.GroupBy));

        var lines = csv.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToList();

        Assert.Equal($"{Attribute},Count,Spellings", lines[0]);
        Assert.Equal("Contractor,4,3", lines[1]);
        Assert.Equal("FTE,1,1", lines[2]);

        // No merged bucket, no column: the header only appears where it says something.
        var uniform = Fold(plan, "CWK", "FTE")!;
        var uniformCsv = Encoding.UTF8.GetString(QueryController.GenerateFileContent(
            [], [], "csv", uniform, warnings: null, metadata: null,
            groupByFields: plan.Projection.Aggregation.GroupBy));

        Assert.DoesNotContain("Spellings", uniformCsv);
    }
}
