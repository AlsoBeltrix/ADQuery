using System.Text;
using System.Text.Json;
using AdQuery.Orchestrator.Controllers;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Benchmarks;

/// <summary>
/// Cross-checks the closed-form <see cref="CsvCapacityByteModel"/> calculators
/// against the real encoders they model, at small scale, so the analytic formulas
/// applied at 100,000-row scale in the matrix are provably exact (JSON body, CSV
/// output) or a proven over-estimate (LDAP filter, BER request). These run in the
/// normal suite; they touch no live provider, directory, or output root.
/// </summary>
public sealed class CsvCapacityByteModelTests
{
    [Theory]
    [InlineData(CsvContentKind.Ascii, 8)]
    [InlineData(CsvContentKind.Quote, 8)]
    [InlineData(CsvContentKind.ThreeByteUtf8, 8)]
    [InlineData(CsvContentKind.ControlEscaped, 8)]
    public void JsonRequestBodyBytes_MatchesRealWebSerialization(CsvContentKind content, int cellCodeUnits)
    {
        var shape = new CsvFixtureShape(Rows: 5, Columns: 4, CellCodeUnits: cellCodeUnits, Content: content);
        const string Query = "Enrich these users with their department";
        var headers = CsvCapacityFixtures.BuildHeaders(shape.Columns);
        var rows = CsvCapacityFixtures.BuildRows(shape);

        // The real MVC pipeline serializes the request DTO with web (camelCase) defaults.
        var actual = new
        {
            query = Query,
            csvHeaders = headers,
            csvData = rows,
        };
        var actualBytes = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(actual, CsvCapacityByteModel.WebJson));

        var modeled = CsvCapacityByteModel.JsonRequestBodyBytes(Query, headers, rows[0], shape.Rows);

        Assert.Equal(actualBytes, modeled);
    }

    [Fact]
    public void JsonStringBytes_EscapesNonAsciiAndControlChars_MatchingWebEncoder()
    {
        foreach (var value in new[] { "plain", "中中中", "", "quote\"comma,", "a\nb" })
        {
            var expected = Encoding.UTF8.GetByteCount(
                JsonSerializer.Serialize(value, CsvCapacityByteModel.WebJson));
            Assert.Equal(expected, CsvCapacityByteModel.JsonStringBytes(value));
        }
    }

    [Theory]
    [InlineData(CsvContentKind.Ascii, 8)]
    [InlineData(CsvContentKind.Quote, 8)]
    [InlineData(CsvContentKind.ThreeByteUtf8, 8)]
    public void EnrichedCsvOutputBytes_MatchesRealExporter(CsvContentKind content, int cellCodeUnits)
    {
        var shape = new CsvFixtureShape(Rows: 6, Columns: 3, CellCodeUnits: cellCodeUnits, Content: content);
        var headers = CsvCapacityFixtures.BuildHeaders(shape.Columns);
        var rows = CsvCapacityFixtures.BuildRows(shape);

        // Drive the production exporter with the same grid (each row as the dictionary
        // shape it consumes) and no aggregation/warnings/metadata, exactly as the
        // enrichment endpoint calls it.
        var dictRows = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var dict = new Dictionary<string, object?>();
            for (var c = 0; c < headers.Count; c++)
            {
                dict[headers[c]] = row[c];
            }

            dictRows.Add(dict);
        }

        var actualBytes = QueryController.GenerateFileContent(dictRows, headers, "csv").LongLength;
        var modeled = CsvCapacityByteModel.EnrichedCsvOutputBytes(headers, rows[0], shape.Rows);

        Assert.Equal(actualBytes, modeled);
    }

    [Fact]
    public void RawCsvInputBytes_MatchesRealExporterForInputGrid()
    {
        var shape = new CsvFixtureShape(Rows: 4, Columns: 5, CellCodeUnits: 6, Content: CsvContentKind.ThreeByteUtf8);
        var headers = CsvCapacityFixtures.BuildHeaders(shape.Columns);
        var rows = CsvCapacityFixtures.BuildRows(shape);

        var dictRows = new List<Dictionary<string, object?>>(rows.Count);
        foreach (var row in rows)
        {
            var dict = new Dictionary<string, object?>();
            for (var c = 0; c < headers.Count; c++)
            {
                dict[headers[c]] = row[c];
            }

            dictRows.Add(dict);
        }

        var actualBytes = QueryController.GenerateFileContent(dictRows, headers, "csv").LongLength;
        var modeled = CsvCapacityByteModel.RawCsvInputBytes(headers, rows[0], shape.Rows);

        Assert.Equal(actualBytes, modeled);
    }

    [Fact]
    public void RenderedOrFilterBytes_ReproducesEscapedOrFilter()
    {
        var identifiers = new[] { "user0000001", "sur(name)", "star*", "back\\slash" };
        var expected = "(|"
            + "(sAMAccountName=user0000001)"
            + "(sAMAccountName=sur\\28name\\29)"
            + "(sAMAccountName=star\\2a)"
            + "(sAMAccountName=back\\5cslash)"
            + ")";

        var modeled = CsvCapacityByteModel.RenderedOrFilterBytes("sAMAccountName", identifiers);

        Assert.Equal(Encoding.UTF8.GetByteCount(expected), modeled);
    }

    [Fact]
    public void ConservativeBerRequestBytes_ExceedsRenderedFilterAndAttributes()
    {
        var attributes = new[] { "distinguishedName", "sAMAccountName", "displayName", "mail" };
        var filterBytes = CsvCapacityByteModel.RenderedOrFilterBytes(
            "sAMAccountName",
            new[] { "user0000001", "user0000002" });

        var ber = CsvCapacityByteModel.ConservativeBerRequestBytes(filterBytes, attributes);

        var rawAttributeBytes = attributes.Sum(a => (long)Encoding.UTF8.GetByteCount(a));
        // The BER estimate must be a strict over-estimate of the raw payload it wraps.
        Assert.True(ber > filterBytes + rawAttributeBytes);
    }

    [Fact]
    public void CanonicalNdjsonBytes_MatchesHandRolledNdjson()
    {
        var headers = new[] { "Employee", "AD_department", "AD_Status" };
        var row = new[] { "user0000001", "中中中", "Found" };

        var expectedPerRow = Encoding.UTF8.GetByteCount(
            JsonSerializer.Serialize(
                new Dictionary<string, string>
                {
                    ["Employee"] = row[0],
                    ["AD_department"] = row[1],
                    ["AD_Status"] = row[2],
                },
                CsvCapacityByteModel.WebJson)) + 1; // trailing newline

        var modeled = CsvCapacityByteModel.CanonicalNdjsonBytes(headers, row, rowCount: 3);

        Assert.Equal(expectedPerRow * 3, modeled);
    }
}
