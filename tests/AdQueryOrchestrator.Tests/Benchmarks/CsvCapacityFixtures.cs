using System.Text;

namespace AdQuery.Orchestrator.Tests.Benchmarks;

/// <summary>
/// The character-composition families the P05 Slice 0 capacity harness sweeps.
/// Each family drives a different worst-case expansion path (JSON escaping,
/// CSV quote doubling, or three-byte UTF-8) so the recorded envelope reflects
/// the largest feasible body/output for a given grid, not just ASCII.
/// </summary>
public enum CsvContentKind
{
    /// <summary>Plain ASCII letters: one UTF-16 unit, one UTF-8 byte, no escaping.</summary>
    Ascii,

    /// <summary>Every character is a double quote: forces CSV quote doubling.</summary>
    Quote,

    /// <summary>Every character is a BMP three-byte UTF-8 code point.</summary>
    ThreeByteUtf8,

    /// <summary>Every character is a JSON control escape ( → six units on the wire).</summary>
    ControlEscaped,
}

/// <summary>
/// The four spine dimensions of a synthetic enrichment fixture. Only <see cref="Rows"/>
/// carries an owner requirement (100,000). The other three are explicitly unapproved
/// test coordinates per the P05 plan and must not be read as product limits.
/// </summary>
internal readonly record struct CsvFixtureShape(
    int Rows,
    int Columns,
    int CellCodeUnits,
    CsvContentKind Content)
{
    public long GridCells => (long)Rows * Columns;
}

/// <summary>
/// Deterministically materializes synthetic CSV enrichment inputs. No real directory
/// data, values, or telemetry are used; every cell is generated from its shape so runs
/// are reproducible and contain no CUI.
/// </summary>
internal static class CsvCapacityFixtures
{
    // A single BMP code point that encodes to three UTF-8 bytes (CJK "中").
    private const char ThreeByteChar = '中';

    // A control character whose JSON encoding is the six-unit  escape.
    private const char ControlChar = '';

    /// <summary>
    /// Builds the header row. Headers are ASCII regardless of cell content so the
    /// match column name stays stable and the provider-request estimate is realistic.
    /// </summary>
    public static List<string> BuildHeaders(int columns)
    {
        var headers = new List<string>(columns);
        headers.Add("Employee");
        for (var i = 1; i < columns; i++)
        {
            headers.Add($"Col{i}");
        }

        return headers;
    }

    /// <summary>
    /// Builds the full rectangular grid for a shape. Column 0 (the match column)
    /// always receives a unique ASCII identifier so directory correlation is
    /// deterministic; the remaining columns receive the shape's content family.
    /// </summary>
    public static List<List<string>> BuildRows(CsvFixtureShape shape)
    {
        var rows = new List<List<string>>(shape.Rows);
        var payload = BuildCell(shape.Content, shape.CellCodeUnits);
        for (var r = 0; r < shape.Rows; r++)
        {
            var row = new List<string>(shape.Columns) { BuildIdentifier(r) };
            for (var c = 1; c < shape.Columns; c++)
            {
                row.Add(payload);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// A unique, ASCII, LDAP-safe identifier for the match column at a given row index.
    /// </summary>
    public static string BuildIdentifier(int rowIndex)
    {
        return $"user{rowIndex:D7}";
    }

    private static string BuildCell(CsvContentKind kind, int codeUnits)
    {
        if (codeUnits <= 0)
        {
            return string.Empty;
        }

        var fill = kind switch
        {
            CsvContentKind.Ascii => 'a',
            CsvContentKind.Quote => '"',
            CsvContentKind.ThreeByteUtf8 => ThreeByteChar,
            CsvContentKind.ControlEscaped => ControlChar,
            _ => 'a',
        };

        return new string(fill, codeUnits);
    }

    /// <summary>
    /// Exact UTF-8 byte length of a string, used to record raw input bytes without
    /// serializing the whole request twice.
    /// </summary>
    public static long Utf8ByteCount(string value) => Encoding.UTF8.GetByteCount(value);
}
