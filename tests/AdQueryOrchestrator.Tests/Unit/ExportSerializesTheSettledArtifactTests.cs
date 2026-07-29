using System.Collections.Generic;
using System.Text;
using AdQuery.Orchestrator.Controllers;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F04 Slice 4, second half of the invariant (slice4-or-2): the exported bytes come from the
/// settled result — the rows and the aggregation the job already produced — and from nothing
/// else. <c>ExportIsModelFreeTests</c> proves the export path cannot reach a model or the plan
/// executor; this proves the positive claim that what it does reach is the artifact.
///
/// Drives the real serializer <c>QueryController.GenerateFileContent</c>, which is the whole
/// content-producing tail of <c>DownloadAsync</c> (that method's remaining work is auth, the
/// export-policy gate, the cache read, and writing the audit copy under the hard-coded
/// <c>E:\WWWOutput</c>, which is why the endpoint itself is not drivable on a build agent).
/// Every assertion is on a distinctive seeded value, so bytes sourced from anywhere but the
/// settled result fail rather than coincidentally match.
/// </summary>
public sealed class ExportSerializesTheSettledArtifactTests
{
    private const string SeededName = "SETTLED-ROW-SENTINEL-Ada";
    private const string SeededDepartment = "SETTLED-DEPT-SENTINEL";

    private static readonly IReadOnlyList<string> Headers = ["Name", "Department"];

    private static readonly IReadOnlyList<Dictionary<string, object?>> SettledRows =
    [
        new() { ["Name"] = SeededName, ["Department"] = SeededDepartment },
        new() { ["Name"] = "SETTLED-ROW-SENTINEL-Grace", ["Department"] = SeededDepartment },
    ];

    [Fact]
    public void ListExport_CarriesTheSettledRows()
    {
        var text = Utf8(QueryController.GenerateFileContent(SettledRows, Headers, "csv"));

        Assert.Contains(SeededName, text);
        Assert.Contains("SETTLED-ROW-SENTINEL-Grace", text);
        Assert.Contains(SeededDepartment, text);
    }

    [Fact]
    public void GroupedExport_CarriesTheSettledDistribution_NotTheUnderlyingRows()
    {
        // F04-D2: a grouped answer's artifact IS the value+count distribution, so the export
        // must be built from the settled aggregation rather than re-derived from rows.
        var aggregation = new Dictionary<string, object>
        {
            ["grouped_counts"] = new Dictionary<string, int>
            {
                ["SETTLED-BUCKET-SENTINEL"] = 91,
                ["SETTLED-OTHER-BUCKET"] = 7,
            },
        };

        var text = Utf8(QueryController.GenerateFileContent(
            SettledRows, Headers, "csv", aggregation, groupByFields: ["Department"]));

        Assert.Contains("SETTLED-BUCKET-SENTINEL,91", text);
        Assert.Contains("SETTLED-OTHER-BUCKET,7", text);

        // The distribution replaces the rows; the underlying records are not the artifact here.
        Assert.DoesNotContain(SeededName, text);
    }

    [Fact]
    public void ExportedMetadata_IsTheSettledJobsOwnProvenance()
    {
        // The header block must describe the query that produced this artifact — not a
        // regenerated or default one.
        var metadata = new QueryMetadata
        {
            Query = "SETTLED-QUERY-SENTINEL",
            User = "settled-user",
            Timestamp = new System.DateTime(2026, 7, 29, 12, 0, 0, System.DateTimeKind.Utc),
            RecordCount = SettledRows.Count,
            Model = "SETTLED-MODEL-SENTINEL",
        };

        var text = Utf8(QueryController.GenerateFileContent(
            SettledRows, Headers, "csv", metadata: metadata));

        Assert.Contains("SETTLED-QUERY-SENTINEL", text);
        Assert.Contains("SETTLED-MODEL-SENTINEL", text);
        Assert.Contains(SeededName, text);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("text")]
    public void EveryTextFormat_CarriesTheSameSettledRows(string format)
    {
        // Format is a rendering choice; the artifact behind every format is one settled result.
        // (The xlsx producer is covered by ExportCompatibilityTests, which opens the workbook.)
        var text = Utf8(QueryController.GenerateFileContent(SettledRows, Headers, format));

        Assert.Contains(SeededName, text);
        Assert.Contains(SeededDepartment, text);
    }

    private static string Utf8(byte[] bytes) => Encoding.UTF8.GetString(bytes);
}
