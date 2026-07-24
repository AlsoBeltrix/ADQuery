using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Controllers;
using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Security;

/// <summary>
/// Structural and dimensional validation of a bound <see cref="CsvEnrichmentRequest"/>
/// (P05 Slice 2). Runs immediately after model binding and before any file-path
/// creation, column-pattern detection, LLM call, LDAP call, output allocation, or
/// cache mutation. It is deliberately independent of transport and controller types
/// so P18's future ingestion path can reuse it.
///
/// The transport body-byte cap (configured on IIS/Kestrel from the same options)
/// guards total request size before model binding; this validator guards the parsed
/// shape. Errors carry a stable machine code and the applicable limit, never a cell
/// value or identifier.
/// </summary>
public interface ICsvEnrichmentRequestValidator
{
    CsvEnrichmentRequestValidationResult Validate(CsvEnrichmentRequest request);
}

public sealed class CsvEnrichmentRequestValidator : ICsvEnrichmentRequestValidator
{
    private readonly CsvEnrichmentLimitsOptions _limits;

    public CsvEnrichmentRequestValidator(IOptions<CsvEnrichmentLimitsOptions> limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        _limits = limits.Value;
    }

    public CsvEnrichmentRequestValidationResult Validate(CsvEnrichmentRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var headers = request.CsvHeaders;
        var data = request.CsvData;

        if (headers is null || headers.Count == 0)
        {
            return Invalid(CsvEnrichmentRejectionCodes.InvalidShape, "CSV headers are required.");
        }

        if (data is null || data.Count == 0)
        {
            return Invalid(CsvEnrichmentRejectionCodes.InvalidShape, "CSV data rows are required.");
        }

        if (headers.Count > _limits.MaxColumns)
        {
            return LimitExceeded(
                CsvEnrichmentRejectionCodes.ColumnLimitExceeded,
                "The CSV has more columns than the configured limit.",
                _limits.MaxColumns,
                headers.Count);
        }

        var seenHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in headers)
        {
            if (string.IsNullOrWhiteSpace(header))
            {
                return Invalid(
                    CsvEnrichmentRejectionCodes.InvalidShape,
                    "CSV headers must not be null, empty, or whitespace.");
            }

            if (header.Length > _limits.MaxFieldCodeUnits)
            {
                return LimitExceeded(
                    CsvEnrichmentRejectionCodes.InputFieldLimitExceeded,
                    "A CSV header exceeds the configured per-field length limit.",
                    _limits.MaxFieldCodeUnits,
                    header.Length);
            }

            if (!seenHeaders.Add(header))
            {
                return Invalid(
                    CsvEnrichmentRejectionCodes.DuplicateHeader,
                    "CSV headers must be unique (case-insensitive).");
            }
        }

        if (data.Count > _limits.MaxDataRows)
        {
            return LimitExceeded(
                CsvEnrichmentRejectionCodes.RowLimitExceeded,
                "The CSV has more data rows than the configured limit.",
                _limits.MaxDataRows,
                data.Count);
        }

        // Rectangular grid budget, checked so a large row × column product cannot
        // overflow silently. Uses the header count as the column dimension.
        long gridCells = checked((long)data.Count * headers.Count);
        if (gridCells > _limits.MaxGridCells)
        {
            return LimitExceeded(
                CsvEnrichmentRejectionCodes.InputGridLimitExceeded,
                "The CSV input grid exceeds the configured cell budget.",
                _limits.MaxGridCells,
                gridCells);
        }

        foreach (var row in data)
        {
            if (row is null)
            {
                return Invalid(
                    CsvEnrichmentRejectionCodes.InvalidShape,
                    "CSV data rows must not be null.");
            }

            // Rows wider than the header set are rejected; shorter rows keep the
            // existing missing-trailing-value interpretation until P18 settles
            // canonical ragged-row behavior.
            if (row.Count > headers.Count)
            {
                return Invalid(
                    CsvEnrichmentRejectionCodes.InvalidShape,
                    "A CSV data row is wider than the header set.");
            }

            foreach (var cell in row)
            {
                if (cell is null)
                {
                    return Invalid(
                        CsvEnrichmentRejectionCodes.InvalidShape,
                        "CSV cells must not be null.");
                }

                if (cell.Length > _limits.MaxFieldCodeUnits)
                {
                    return LimitExceeded(
                        CsvEnrichmentRejectionCodes.InputFieldLimitExceeded,
                        "A CSV cell exceeds the configured per-field length limit.",
                        _limits.MaxFieldCodeUnits,
                        cell.Length);
                }
            }
        }

        return CsvEnrichmentRequestValidationResult.Valid;
    }

    private static CsvEnrichmentRequestValidationResult Invalid(string code, string title)
        => new(false, StatusCodes.Status422UnprocessableEntity, code, title, Limit: null, Observed: null);

    private static CsvEnrichmentRequestValidationResult LimitExceeded(
        string code,
        string title,
        long limit,
        long observed)
        => new(false, StatusCodes.Status422UnprocessableEntity, code, title, limit, observed);
}

/// <summary>
/// Outcome of parsed-request validation. A valid result carries only
/// <see cref="IsValid"/>; a rejection carries the HTTP status, a stable machine
/// code, a human-readable title, and — when it does not expose content — the
/// applicable limit and observed count.
/// </summary>
public sealed record CsvEnrichmentRequestValidationResult(
    bool IsValid,
    int StatusCode,
    string? Code,
    string? Title,
    long? Limit,
    long? Observed)
{
    public static readonly CsvEnrichmentRequestValidationResult Valid =
        new(true, StatusCodes.Status200OK, Code: null, Title: null, Limit: null, Observed: null);
}

/// <summary>
/// Stable machine codes for CSV enrichment request rejections, mirroring the P05
/// API error contract. Codes are part of the client-facing contract; do not rename.
/// </summary>
public static class CsvEnrichmentRejectionCodes
{
    public const string BodyTooLarge = "csv_body_too_large";
    public const string RowLimitExceeded = "csv_row_limit_exceeded";
    public const string ColumnLimitExceeded = "csv_column_limit_exceeded";
    public const string InputGridLimitExceeded = "csv_input_grid_limit_exceeded";
    public const string InputFieldLimitExceeded = "csv_input_field_limit_exceeded";
    public const string InvalidShape = "csv_invalid_shape";
    public const string DuplicateHeader = "csv_duplicate_header";
}
