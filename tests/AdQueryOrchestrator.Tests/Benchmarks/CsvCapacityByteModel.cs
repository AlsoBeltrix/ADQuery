using System.Text;
using System.Text.Json;

namespace AdQuery.Orchestrator.Tests.Benchmarks;

/// <summary>
/// Closed-form byte calculators for the P05 capacity evidence. Each formula is
/// cross-checked against the real encoder it models (System.Text.Json for the
/// request body, <c>QueryController.GenerateFileContent</c> for CSV output) in
/// <c>CsvCapacityByteModelTests</c>, then applied analytically at 100,000-row
/// scale so the matrix never has to materialize a 100 MB string twice.
///
/// The calculators assume a homogeneous grid: every data row has the same field
/// widths as a supplied sample row. The synthetic fixtures satisfy this because
/// the match-column identifier is a fixed-width ASCII token and every payload
/// cell is identical, so per-row encoded length is constant even though the
/// identifier value changes.
/// </summary>
internal static class CsvCapacityByteModel
{
    /// <summary>
    /// Serialization options that mirror the ASP.NET Core MVC JSON pipeline:
    /// camelCase names and the default (HTML-safe) escaper, which emits <c>\uXXXX</c>
    /// for non-ASCII and control characters. This is what makes the three-byte
    /// UTF-8 and control-escape families expand on the wire.
    /// </summary>
    public static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

    /// <summary>UTF-8 byte length of <see cref="Environment.NewLine"/> (2 on Windows).</summary>
    public static readonly long NewlineBytes = Encoding.UTF8.GetByteCount(Environment.NewLine);

    /// <summary>
    /// Exact UTF-8 byte length of a JSON-encoded string, including its surrounding
    /// quotes, using the same encoder the MVC pipeline uses.
    /// </summary>
    public static long JsonStringBytes(string value)
        => Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(value, WebJson));

    /// <summary>
    /// Exact UTF-8 byte length of one CSV field after the enrichment exporter's
    /// escaping rule (mirrors <c>QueryController.EscapeCsv</c>). Guarded by the
    /// whole-document cross-check against the real exporter.
    /// </summary>
    public static long CsvFieldBytes(string value)
        => Encoding.UTF8.GetByteCount(EscapeCsv(value));

    /// <summary>
    /// Byte length of the compact JSON request body the browser uploads:
    /// <c>{"query":...,"csvHeaders":[...],"csvData":[[...],...]}</c>. Every data
    /// row is assumed to have the field widths of <paramref name="sampleRow"/>.
    /// </summary>
    public static long JsonRequestBodyBytes(
        string query,
        IReadOnlyList<string> headers,
        IReadOnlyList<string> sampleRow,
        long rowCount)
    {
        var body = Utf8("{\"query\":");
        body += JsonStringBytes(query);
        body += Utf8(",\"csvHeaders\":");
        body += JsonStringArrayBytes(headers);
        body += Utf8(",\"csvData\":");
        body += JsonRowArrayBytes(sampleRow, rowCount);
        body += Utf8("}");
        return body;
    }

    /// <summary>
    /// Byte length of the input grid rendered as a raw CSV document (the shape a
    /// standards-compliant raw-file counter would see once P18 lands). Header row
    /// plus <paramref name="rowCount"/> homogeneous data rows.
    /// </summary>
    public static long RawCsvInputBytes(
        IReadOnlyList<string> headers,
        IReadOnlyList<string> sampleRow,
        long rowCount)
        => CsvDocumentBytes(headers, sampleRow, rowCount);

    /// <summary>
    /// Byte length of the enriched CSV the exporter writes: the output header row
    /// plus <paramref name="rowCount"/> homogeneous output rows. Callers pass the
    /// already-projected output header/field lists (original columns + AD_* columns
    /// + AD_Status).
    /// </summary>
    public static long EnrichedCsvOutputBytes(
        IReadOnlyList<string> outputHeaders,
        IReadOnlyList<string> sampleOutputRow,
        long rowCount)
        => CsvDocumentBytes(outputHeaders, sampleOutputRow, rowCount);

    /// <summary>
    /// Byte length of a canonical NDJSON export estimate: one JSON object per row,
    /// repeating every header name on every row, each terminated by <c>\n</c>.
    /// This models P07's object-shaped output; P07's writers are not landed, so it
    /// is a derived estimate rather than a measured value.
    /// </summary>
    public static long CanonicalNdjsonBytes(
        IReadOnlyList<string> outputHeaders,
        IReadOnlyList<string> sampleOutputRow,
        long rowCount)
    {
        if (outputHeaders.Count != sampleOutputRow.Count)
        {
            throw new ArgumentException("Header and row field counts must match.");
        }

        long objectBytes = Utf8("{");
        for (var i = 0; i < outputHeaders.Count; i++)
        {
            if (i > 0)
            {
                objectBytes += Utf8(",");
            }

            objectBytes += JsonStringBytes(outputHeaders[i]);
            objectBytes += Utf8(":");
            objectBytes += JsonStringBytes(sampleOutputRow[i]);
        }

        objectBytes += Utf8("}");
        return rowCount * (objectBytes + 1); // each record + newline byte
    }

    /// <summary>
    /// UTF-8 byte length of the LDAP OR filter the future batch path would render
    /// for one chunk of unique identifiers: <c>(|(attr=id1)(attr=id2)...)</c> with
    /// each value LDAP-escaped exactly as <c>ActiveDirectoryService.EscapeLdapValue</c>.
    /// A one-element chunk is still wrapped so the estimate is conservative.
    /// </summary>
    public static long RenderedOrFilterBytes(string matchAttribute, IReadOnlyList<string> identifiers)
    {
        long total = Utf8("(|") + Utf8(")");
        var attributeBytes = Utf8(matchAttribute);
        foreach (var identifier in identifiers)
        {
            // "(" + attr + "=" + escaped value + ")"
            total += 3 + attributeBytes + Utf8(EscapeLdapValue(identifier));
        }

        return total;
    }

    /// <summary>
    /// Conservative estimate of the complete LDAP search request in BER encoding
    /// for one chunk: the rendered filter, the requested attribute names, and a
    /// fixed protocol allowance, each element charged a 4-byte BER tag/length and
    /// the whole request inflated by a headroom factor. Deliberately an
    /// over-estimate so it can be compared against the DC receive ceiling; it is a
    /// model, not a measured wire capture.
    /// </summary>
    public static long ConservativeBerRequestBytes(
        long renderedFilterBytes,
        IReadOnlyList<string> requestedAttributes)
    {
        const long ProtocolBaseOverhead = 128; // messageID, op tag, baseObject, scope, deref, size/time limits.
        const long PerElementBerOverhead = 4;  // tag + length octets charged per filter/attribute element.
        const double HeadroomFactor = 1.25;    // conservative inflation over the modeled minimum.

        long attributeBytes = 0;
        foreach (var attribute in requestedAttributes)
        {
            attributeBytes += Utf8(attribute) + PerElementBerOverhead;
        }

        var modeled = ProtocolBaseOverhead + renderedFilterBytes + PerElementBerOverhead + attributeBytes;
        return (long)Math.Ceiling(modeled * HeadroomFactor);
    }

    private static long CsvDocumentBytes(
        IReadOnlyList<string> headers,
        IReadOnlyList<string> sampleRow,
        long rowCount)
    {
        var headerLine = CsvLineBytes(headers);
        var rowLine = CsvLineBytes(sampleRow);
        return headerLine + NewlineBytes + rowCount * (rowLine + NewlineBytes);
    }

    private static long CsvLineBytes(IReadOnlyList<string> fields)
    {
        if (fields.Count == 0)
        {
            return 0;
        }

        long total = fields.Count - 1; // comma separators
        foreach (var field in fields)
        {
            total += CsvFieldBytes(field);
        }

        return total;
    }

    private static long JsonStringArrayBytes(IReadOnlyList<string> values)
    {
        long total = Utf8("[") + Utf8("]");
        if (values.Count > 0)
        {
            total += values.Count - 1; // comma separators
            foreach (var value in values)
            {
                total += JsonStringBytes(value);
            }
        }

        return total;
    }

    private static long JsonRowArrayBytes(IReadOnlyList<string> sampleRow, long rowCount)
    {
        long total = Utf8("[") + Utf8("]");
        if (rowCount > 0)
        {
            total += rowCount - 1; // comma separators between rows
            total += rowCount * JsonInnerRowBytes(sampleRow);
        }

        return total;
    }

    private static long JsonInnerRowBytes(IReadOnlyList<string> cells)
    {
        long total = Utf8("[") + Utf8("]");
        if (cells.Count > 0)
        {
            total += cells.Count - 1; // comma separators between cells
            foreach (var cell in cells)
            {
                total += JsonStringBytes(cell);
            }
        }

        return total;
    }

    private static long Utf8(string literal) => Encoding.UTF8.GetByteCount(literal);

    // Mirrors QueryController.EscapeCsv; guarded by the whole-document cross-check.
    private static string EscapeCsv(string input)
    {
        if (input.Contains('"') || input.Contains(',') || input.Contains('\n') || input.Contains('\r'))
        {
            return $"\"{input.Replace("\"", "\"\"")}\"";
        }

        return input;
    }

    // Mirrors ActiveDirectoryService.EscapeLdapValue; guarded by the filter cross-check.
    private static string EscapeLdapValue(string value)
        => value
            .Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);
}
