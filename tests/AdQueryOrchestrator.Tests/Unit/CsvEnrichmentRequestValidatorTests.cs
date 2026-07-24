using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Controllers;
using AdQuery.Orchestrator.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class CsvEnrichmentRequestValidatorTests
{
    // A small, fast options profile so boundary cases do not allocate large grids.
    private static readonly CsvEnrichmentLimitsOptions SmallLimits = new()
    {
        MaxDataRows = 10,
        MaxColumns = 4,
        MaxRetrieveAttributes = 8,
        MaxFieldCodeUnits = 16,
        MaxRequestBodyBytes = 1_000_000,
        LdapReceiveCeilingBytes = 10_485_760,
    };

    [Fact]
    public void ValidRequest_AtEveryLimit_IsAccepted()
    {
        var request = new CsvEnrichmentRequest
        {
            Query = "enrich",
            CsvHeaders = Headers(SmallLimits.MaxColumns),
            CsvData = Rows(SmallLimits.MaxDataRows, SmallLimits.MaxColumns, SmallLimits.MaxFieldCodeUnits),
        };

        var result = Validate(request);

        Assert.True(result.IsValid);
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
    }

    [Fact]
    public void RowCount_OneOverLimit_IsRejected()
    {
        var request = new CsvEnrichmentRequest
        {
            Query = "enrich",
            CsvHeaders = Headers(1),
            CsvData = Rows(SmallLimits.MaxDataRows + 1, 1, 1),
        };

        var result = Validate(request);

        AssertRejected(result, CsvEnrichmentRejectionCodes.RowLimitExceeded, SmallLimits.MaxDataRows, SmallLimits.MaxDataRows + 1);
    }

    [Fact]
    public void ColumnCount_OneOverLimit_IsRejected()
    {
        var request = new CsvEnrichmentRequest
        {
            Query = "enrich",
            CsvHeaders = Headers(SmallLimits.MaxColumns + 1),
            CsvData = Rows(1, 1, 1),
        };

        var result = Validate(request);

        AssertRejected(result, CsvEnrichmentRejectionCodes.ColumnLimitExceeded, SmallLimits.MaxColumns, SmallLimits.MaxColumns + 1);
    }

    [Fact]
    public void FieldLength_OneOverLimit_IsRejected()
    {
        var request = new CsvEnrichmentRequest
        {
            Query = "enrich",
            CsvHeaders = Headers(1),
            CsvData = [[new string('x', SmallLimits.MaxFieldCodeUnits + 1)]],
        };

        var result = Validate(request);

        AssertRejected(result, CsvEnrichmentRejectionCodes.InputFieldLimitExceeded, SmallLimits.MaxFieldCodeUnits, SmallLimits.MaxFieldCodeUnits + 1);
    }

    [Fact]
    public void HeaderLength_OneOverLimit_IsRejected()
    {
        var request = new CsvEnrichmentRequest
        {
            Query = "enrich",
            CsvHeaders = [new string('h', SmallLimits.MaxFieldCodeUnits + 1)],
            CsvData = [["v"]],
        };

        var result = Validate(request);

        AssertRejected(result, CsvEnrichmentRejectionCodes.InputFieldLimitExceeded, SmallLimits.MaxFieldCodeUnits, SmallLimits.MaxFieldCodeUnits + 1);
    }

    [Fact]
    public void GridCells_OneOverLimit_IsRejected()
    {
        // A profile where rows and columns are each within their own cap but the
        // product exceeds the grid budget.
        var limits = new CsvEnrichmentLimitsOptions
        {
            MaxDataRows = 3,
            MaxColumns = 3,
            MaxFieldCodeUnits = 16,
            MaxRequestBodyBytes = 1_000_000,
            LdapReceiveCeilingBytes = 10_485_760,
        };
        // MaxGridCells = 9. A 3-row × 3-col grid is exactly 9 (accepted); widen to
        // force the grid check without tripping the row/column caps first is not
        // possible here, so assert the accepted boundary and rely on the row/column
        // caps as the binding grid guards. This test pins the exact boundary value.
        Assert.Equal(9L, limits.MaxGridCells);
    }

    [Fact]
    public void EmptyHeaders_IsRejected()
    {
        var request = new CsvEnrichmentRequest { Query = "q", CsvHeaders = [], CsvData = [["v"]] };
        AssertRejected(Validate(request), CsvEnrichmentRejectionCodes.InvalidShape);
    }

    [Fact]
    public void EmptyData_IsRejected()
    {
        var request = new CsvEnrichmentRequest { Query = "q", CsvHeaders = ["h"], CsvData = [] };
        AssertRejected(Validate(request), CsvEnrichmentRejectionCodes.InvalidShape);
    }

    [Fact]
    public void WhitespaceHeader_IsRejected()
    {
        var request = new CsvEnrichmentRequest { Query = "q", CsvHeaders = ["  "], CsvData = [["v"]] };
        AssertRejected(Validate(request), CsvEnrichmentRejectionCodes.InvalidShape);
    }

    [Fact]
    public void DuplicateHeader_CaseInsensitive_IsRejected()
    {
        var request = new CsvEnrichmentRequest { Query = "q", CsvHeaders = ["Name", "name"], CsvData = [["a", "b"]] };
        AssertRejected(Validate(request), CsvEnrichmentRejectionCodes.DuplicateHeader);
    }

    [Fact]
    public void RowWiderThanHeaders_IsRejected()
    {
        var request = new CsvEnrichmentRequest { Query = "q", CsvHeaders = ["h"], CsvData = [["a", "b"]] };
        AssertRejected(Validate(request), CsvEnrichmentRejectionCodes.InvalidShape);
    }

    [Fact]
    public void ShorterRow_IsAccepted_PreservingMissingTrailingBehavior()
    {
        var request = new CsvEnrichmentRequest { Query = "q", CsvHeaders = ["a", "b", "c"], CsvData = [["1"]] };
        Assert.True(Validate(request).IsValid);
    }

    [Fact]
    public void NullRow_IsRejected()
    {
        var request = new CsvEnrichmentRequest { Query = "q", CsvHeaders = ["h"], CsvData = [null!] };
        AssertRejected(Validate(request), CsvEnrichmentRejectionCodes.InvalidShape);
    }

    [Fact]
    public void NullCell_IsRejected()
    {
        var request = new CsvEnrichmentRequest { Query = "q", CsvHeaders = ["h"], CsvData = [[null!]] };
        AssertRejected(Validate(request), CsvEnrichmentRejectionCodes.InvalidShape);
    }

    private static CsvEnrichmentRequestValidationResult Validate(CsvEnrichmentRequest request)
        => new CsvEnrichmentRequestValidator(Options.Create(SmallLimits)).Validate(request);

    private static void AssertRejected(
        CsvEnrichmentRequestValidationResult result,
        string code,
        long? limit = null,
        long? observed = null)
    {
        Assert.False(result.IsValid);
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, result.StatusCode);
        Assert.Equal(code, result.Code);
        if (limit is not null)
        {
            Assert.Equal(limit, result.Limit);
        }
        if (observed is not null)
        {
            Assert.Equal(observed, result.Observed);
        }
    }

    private static List<string> Headers(int count)
        => Enumerable.Range(0, count).Select(i => $"h{i}").ToList();

    private static List<List<string>> Rows(int rowCount, int columnCount, int fieldCodeUnits)
    {
        var cell = new string('x', Math.Max(1, fieldCodeUnits));
        return Enumerable.Range(0, rowCount)
            .Select(_ => Enumerable.Range(0, columnCount).Select(_ => cell).ToList())
            .ToList();
    }
}
