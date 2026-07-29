using System.Collections.Generic;
using System.Linq;
using System.Text;
using AdQuery.Orchestrator.Controllers;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using ClosedXML.Excel;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F04 Slice 1 guard (F04-D2). The distinct-list guess-transform is gone: a plan whose
/// projection columns happen to equal its <c>group_by</c> fields settles like any other
/// grouped plan — the grouped aggregation survives and the rows are NOT rebuilt as one
/// row per distinct value. Its export is the value+count distribution as first-class
/// rows in every format, not a comment block or side sheet beneath a dump of the
/// underlying records.
/// </summary>
public sealed class GroupedResultSettlementTests
{
    private const string Attribute = "extensionAttribute1";

    // Two large buckets plus three singletons — 8 records, 5 distinct values.
    private static readonly string[] Values =
        ["CC-100", "CC-100", "CC-100", "CC-200", "CC-200", "CC-301", "CC-302", "CC-303"];

    /// <summary>
    /// The extensionAttribute1 shape that motivated F04-D2: projection column equals the
    /// single group_by field, values near-unique with a few large buckets.
    /// </summary>
    private static DirectoryQueryPlan DistinctListShapedPlan(string? columnName = null) => new()
    {
        Steps = { new DirectoryPlanStep { Name = "s1", Operation = "search" } },
        Projection = new ProjectionDefinition
        {
            RowStep = "s1",
            Columns = { new ProjectionColumn { Name = columnName ?? Attribute, Attribute = Attribute } },
            Aggregation = new AggregationDefinition { Count = true, GroupBy = { Attribute } },
        },
    };

    /// <summary>
    /// Rows shaped the way <c>DirectoryPlanExecutor.Project</c> shapes them: keyed by the
    /// projection column's <c>Name</c>, never by its source attribute (slice1-or-1).
    /// </summary>
    private static List<Dictionary<string, object?>> ProjectedRows(string columnName)
        => Values
            .Select(value => new Dictionary<string, object?>(System.StringComparer.OrdinalIgnoreCase)
            {
                [columnName] = value,
            })
            .ToList();

    private static List<Dictionary<string, object?>> DistinctListShapedRows()
        => ProjectedRows(Attribute);

    private static Dictionary<string, int> GroupedCounts(Dictionary<string, object>? aggregation)
    {
        Assert.NotNull(aggregation);
        Assert.True(aggregation!.ContainsKey("grouped_counts"));
        var counts = Assert.IsType<Dictionary<string, int>>(aggregation["grouped_counts"]);
        return counts;
    }

    // --- Settlement: the aggregation survives and the rows are untouched. ---

    [Fact]
    public void ProjectionMatchingGroupBy_RetainsAggregation_AndDoesNotExpandRows()
    {
        var plan = DistinctListShapedPlan();
        var rows = DistinctListShapedRows();

        var aggregation = QueryJobManager.ComputeSettledAggregation(plan, rows);

        // Pre-removal, the transform cleared the aggregation and rebuilt rows as one
        // row per distinct value (5 rows of value+Count). Both assertions fail there.
        var counts = GroupedCounts(aggregation);
        Assert.Equal(5, counts.Count);
        Assert.Equal(3, counts["CC-100"]);
        Assert.Equal(2, counts["CC-200"]);

        Assert.Equal(8, rows.Count);
        Assert.All(rows, row => Assert.False(row.ContainsKey("Count")));
    }

    [Fact]
    public void ProjectionNotMatchingGroupBy_SettlesIdentically()
    {
        // The transform's trigger condition no longer discriminates: a plan projecting a
        // second column settles exactly like the matching-columns plan above.
        var plan = DistinctListShapedPlan();
        plan.Projection!.Columns.Add(new ProjectionColumn { Name = "Name", Attribute = "displayName" });
        var rows = DistinctListShapedRows();

        var counts = GroupedCounts(QueryJobManager.ComputeSettledAggregation(plan, rows));

        Assert.Equal(5, counts.Count);
        Assert.Equal(8, rows.Count);
    }

    // --- slice1-or-1: group_by names an attribute; rows are keyed by column name. ---

    [Fact]
    public void AliasedProjectionColumn_GroupsByTheProjectedValue()
    {
        // group_by is "extensionAttribute1"; the projection emits it as "Cost Center".
        // Pre-fix, the attribute lookup missed every row and folded all 8 records into a
        // single "(empty)" bucket — a fabricated distribution reported as the answer.
        var plan = DistinctListShapedPlan(columnName: "Cost Center");
        var rows = ProjectedRows("Cost Center");
        var warnings = new List<string>();

        var counts = GroupedCounts(QueryJobManager.ComputeSettledAggregation(plan, rows, warnings));

        Assert.Equal(5, counts.Count);
        Assert.Equal(3, counts["CC-100"]);
        Assert.DoesNotContain("(empty)", counts.Keys);
        Assert.Empty(warnings);
    }

    [Fact]
    public void CaseVariantColumnName_StillResolves()
    {
        // The shipped prompt example projects 'department' as "Department"; rows are
        // case-insensitive, so the field resolves directly without alias mapping.
        var plan = DistinctListShapedPlan(columnName: Attribute.ToUpperInvariant());
        var rows = ProjectedRows(Attribute.ToUpperInvariant());

        Assert.Equal(5, GroupedCounts(QueryJobManager.ComputeSettledAggregation(plan, rows)).Count);
    }

    [Fact]
    public void UnprojectedGroupByField_WarnsRatherThanFabricatingAnEmptyBucket()
    {
        // Grouping on an attribute the projection never emits: the single "(empty)"
        // bucket is unavoidable, but it must not be reported as a silent real answer.
        var plan = DistinctListShapedPlan();
        plan.Projection!.Aggregation!.GroupBy.Clear();
        plan.Projection.Aggregation.GroupBy.Add("nowhere");
        var warnings = new List<string>();

        var counts = GroupedCounts(
            QueryJobManager.ComputeSettledAggregation(plan, DistinctListShapedRows(), warnings));

        Assert.Equal(new[] { "(empty)" }, counts.Keys);
        Assert.Contains(warnings, w => w.Contains("nowhere") && w.Contains("not present"));
    }

    [Fact]
    public void NoAggregationRequested_OrNoRows_SettlesWithNullAggregation()
    {
        var plainPlan = new DirectoryQueryPlan
        {
            Steps = { new DirectoryPlanStep { Name = "s1", Operation = "search" } },
            Projection = new ProjectionDefinition { RowStep = "s1" },
        };

        Assert.Null(QueryJobManager.ComputeSettledAggregation(plainPlan, DistinctListShapedRows()));
        Assert.Null(QueryJobManager.ComputeSettledAggregation(
            DistinctListShapedPlan(), new List<Dictionary<string, object?>>()));
    }

    // --- Export: the distribution is the exported table, in every format. ---

    private static (List<Dictionary<string, object?>> Rows, Dictionary<string, object> Aggregation, List<string> GroupBy) SettledGroupedResult()
    {
        var plan = DistinctListShapedPlan();
        var rows = DistinctListShapedRows();
        var aggregation = QueryJobManager.ComputeSettledAggregation(plan, rows)!;
        return (rows, aggregation, plan.Projection!.Aggregation!.GroupBy);
    }

    [Fact]
    public void GroupedCsvExport_IsValueCountRows_NotUnderlyingRowsWithCommentedCounts()
    {
        var (rows, aggregation, groupBy) = SettledGroupedResult();

        var csv = Encoding.UTF8.GetString(QueryController.GenerateFileContent(
            rows, QueryControllerHeaders(rows), "csv", aggregation, warnings: null, metadata: null, groupByFields: groupBy));

        var lines = csv.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();

        // Header + one row per distinct value, count descending — as data, not comments.
        Assert.Equal($"{Attribute},Count", lines[0]);
        Assert.Equal("CC-100,3", lines[1]);
        Assert.Equal("CC-200,2", lines[2]);
        Assert.Equal(6, lines.Count);

        // The fallback shape is gone: no comment-block summary, and the 8 underlying
        // single-column rows are not the exported table.
        Assert.DoesNotContain("# SUMMARY", csv);
        Assert.DoesNotContain("# Category,Count", csv);
    }

    [Fact]
    public void GroupedHtmlExport_HasDistributionAsTheDataTable_NotASummarySection()
    {
        var (rows, aggregation, groupBy) = SettledGroupedResult();

        var html = Encoding.UTF8.GetString(QueryController.GenerateFileContent(
            rows, QueryControllerHeaders(rows), "html", aggregation, warnings: null, metadata: null, groupByFields: groupBy));

        Assert.DoesNotContain("<h2>Summary</h2>", html);
        Assert.Contains("<h2>Data</h2>", html);
        Assert.Contains($"<th>{Attribute}</th>", html);
        Assert.Contains("<th>Count</th>", html);
        Assert.Contains("<td>CC-100</td>", html);
    }

    [Fact]
    public void GroupedExcelExport_PutsDistributionOnTheDataSheet_WithNoSeparateSummarySheet()
    {
        var (rows, aggregation, groupBy) = SettledGroupedResult();

        var bytes = QueryController.GenerateFileContent(
            rows, QueryControllerHeaders(rows), "excel", aggregation, warnings: null, metadata: null, groupByFields: groupBy);

        using var stream = new MemoryStream(bytes);
        using var workbook = new XLWorkbook(stream);

        Assert.False(workbook.Worksheets.Contains("Summary"));

        var data = workbook.Worksheet("Data");
        Assert.Equal(Attribute, data.Cell(1, 1).GetString());
        Assert.Equal("Count", data.Cell(1, 2).GetString());
        Assert.Equal("CC-100", data.Cell(2, 1).GetString());
        Assert.Equal(3, data.Cell(2, 2).GetDouble());
        Assert.True(data.Cell(7, 1).IsEmpty());
    }

    [Fact]
    public void MultiFieldGroupBy_SplitsTheCompositeKeyAcrossColumns()
    {
        var plan = new DirectoryQueryPlan
        {
            Steps = { new DirectoryPlanStep { Name = "s1", Operation = "search" } },
            Projection = new ProjectionDefinition
            {
                RowStep = "s1",
                Aggregation = new AggregationDefinition { Count = true, GroupBy = { "department", "city" } },
            },
        };

        plan.Projection!.Columns.Add(new ProjectionColumn { Name = "department", Attribute = "department" });
        plan.Projection.Columns.Add(new ProjectionColumn { Name = "city", Attribute = "city" });

        var rows = new List<Dictionary<string, object?>>
        {
            new(System.StringComparer.OrdinalIgnoreCase) { ["department"] = "IT", ["city"] = "Dublin" },
            new(System.StringComparer.OrdinalIgnoreCase) { ["department"] = "IT", ["city"] = "Dublin" },
            new(System.StringComparer.OrdinalIgnoreCase) { ["department"] = "HR", ["city"] = "Cork" },
        };

        var aggregation = QueryJobManager.ComputeSettledAggregation(plan, rows)!;
        var export = QueryController.BuildGroupedDistributionExport(aggregation, plan.Projection!.Aggregation!.GroupBy);

        Assert.NotNull(export);
        Assert.Equal(new[] { "department", "city", "Count" }, export!.Value.Headers);
        Assert.Equal("IT", export.Value.Rows[0]["department"]);
        Assert.Equal("Dublin", export.Value.Rows[0]["city"]);
        Assert.Equal(2, export.Value.Rows[0]["Count"]);
    }

    // --- slice1-or-2: the composite key encoding is reversible. ---

    [Fact]
    public void CompositeKeyWithDelimiterInAValue_DoesNotCollideOrShiftColumns()
    {
        // AD free-text attributes may contain the '|' the composite key joins on. Pre-fix,
        // these two distinct combinations both encoded to "R&D|Labs|Boston" and merged into
        // one bucket, and the export's unescaped split shifted "Boston" out of the row.
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
            new(System.StringComparer.OrdinalIgnoreCase) { ["department"] = "R&D|Labs", ["city"] = "Boston" },
            new(System.StringComparer.OrdinalIgnoreCase) { ["department"] = "R&D", ["city"] = "Labs|Boston" },
        };

        var aggregation = QueryJobManager.ComputeSettledAggregation(plan, rows)!;

        Assert.Equal(2, GroupedCounts(aggregation).Count);

        var export = QueryController.BuildGroupedDistributionExport(
            aggregation, plan.Projection!.Aggregation!.GroupBy);

        Assert.NotNull(export);
        Assert.Equal(2, export!.Value.Rows.Count);
        Assert.Contains(export.Value.Rows, row =>
            Equals(row["department"], "R&D|Labs") && Equals(row["city"], "Boston"));
        Assert.Contains(export.Value.Rows, row =>
            Equals(row["department"], "R&D") && Equals(row["city"], "Labs|Boston"));
    }

    [Theory]
    [InlineData("R&D|Labs", "Boston")]
    [InlineData(@"back\slash", "plain")]
    [InlineData(@"pipe|and\escape", @"\|")]
    [InlineData("", "|")]
    public void GroupKeyRoundTrips_ForValuesContainingDelimiterAndEscape(string first, string second)
    {
        var parts = GroupKey.Decompose(GroupKey.Compose([first, second]), 2);

        Assert.Equal([first, second], parts);
    }

    [Fact]
    public void NonGroupedResult_ExportsItsRowsUnchanged()
    {
        var rows = DistinctListShapedRows();

        var csv = Encoding.UTF8.GetString(QueryController.GenerateFileContent(
            rows, QueryControllerHeaders(rows), "csv"));

        var lines = csv.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();

        Assert.Equal(Attribute, lines[0]);
        Assert.Equal(rows.Count + 1, lines.Count);
    }

    private static List<string> QueryControllerHeaders(IEnumerable<Dictionary<string, object?>> rows)
        => rows.SelectMany(row => row.Keys).Distinct(System.StringComparer.OrdinalIgnoreCase).ToList();
}
