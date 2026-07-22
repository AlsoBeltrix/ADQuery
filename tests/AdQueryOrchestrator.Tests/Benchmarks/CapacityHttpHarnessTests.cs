using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Benchmarks;

/// <summary>
/// Confirms the self-hosted capacity harness drives the real enrichment endpoint end
/// to end with the external dependencies faked: the request is authorized, a plan is
/// produced, synthetic directory records are merged, and the result is published to the
/// isolated temp writer — no live provider, directory, or production output root.
/// </summary>
public sealed class CapacityHttpHarnessTests : IDisposable
{
    private readonly string _outputRoot =
        Path.Combine(Path.GetTempPath(), "adquery-capacity-smoke", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CsvEnrich_DrivesRealEndpointEndToEnd()
    {
        string[] retrieve = ["displayName", "department"];
        using var harness = new CapacityHttpHarness(_outputRoot, retrieve);
        using var client = harness.CreateClient();

        var shape = new CsvFixtureShape(Rows: 20, Columns: 3, CellCodeUnits: 8, Content: CsvContentKind.Ascii);
        var request = new
        {
            query = "Add display name and department",
            csvHeaders = CsvCapacityFixtures.BuildHeaders(shape.Columns),
            csvData = CsvCapacityFixtures.BuildRows(shape),
        };

        var ct = TestContext.Current.CancellationToken;
        var response = await client.PostAsJsonAsync("/api/query/csv-enrich", request, ct);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        Assert.True(body.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(20, body.RootElement.GetProperty("totalRows").GetInt32());
        Assert.Equal(20, body.RootElement.GetProperty("matchedRows").GetInt32());
        Assert.Single(Directory.GetFiles(_outputRoot, "*.csv"));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_outputRoot))
            {
                Directory.Delete(_outputRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the isolated temp output root.
        }
    }
}
