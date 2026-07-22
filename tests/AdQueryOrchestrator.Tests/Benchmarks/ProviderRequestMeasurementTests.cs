using Xunit;

namespace AdQuery.Orchestrator.Tests.Benchmarks;

/// <summary>
/// Verifies the provider-request measurement harness renders the real request through
/// the production service and confirms the non-obvious modeling fact the byte accounting
/// depends on: the provider request scales with header names, row count, and detected
/// patterns — never with row cell values. Runs in the normal suite; makes no live call.
/// </summary>
public sealed class ProviderRequestMeasurementTests
{
    [Fact]
    public void Measure_CapturesPositiveBodyAndConfiguredReserve()
    {
        var headers = CsvCapacityFixtures.BuildHeaders(4);
        var sample = ProviderRequestMeasurement.Measure(
            userQuery: "Add department and manager",
            csvHeaders: headers,
            rowCount: 100_000,
            columnPatterns: new Dictionary<string, string>
            {
                ["Employee"] = "short alphanumeric (8 chars or less) - use sAMAccountName",
            });

        Assert.True(sample.RequestBodyBytes > 0);
        Assert.Equal(4000, sample.OutputTokenReserve);
    }

    [Fact]
    public void Measure_RequestBytesAreInvariantToRowCount()
    {
        var headers = CsvCapacityFixtures.BuildHeaders(4);
        var patterns = new Dictionary<string, string>
        {
            ["Employee"] = "short alphanumeric (8 chars or less) - use sAMAccountName",
        };

        var small = ProviderRequestMeasurement.Measure("Add department", headers, 10, patterns);
        var large = ProviderRequestMeasurement.Measure("Add department", headers, 100_000, patterns);

        // The only row-scaled token in the prompt is the decimal rowCount, so the body
        // grows solely by the extra digits — proving row *data* is never sent.
        var digitDelta = 100_000.ToString().Length - 10.ToString().Length;
        Assert.Equal(digitDelta, large.RequestBodyBytes - small.RequestBodyBytes);
    }
}
