using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Tests.Benchmarks;

/// <summary>
/// Outcome classification for a unique identifier's directory lookup, mirroring the
/// found / not-found / ambiguous distinction the planned batch path must track.
/// </summary>
internal enum LookupOutcomeKind
{
    Found,
    NotFound,
    Ambiguous,
}

/// <summary>
/// A benchmark-only model of every data structure the planned batched CSV-enrichment
/// path would retain in memory for one request: the deduplication index, per-identifier
/// row-index lists, the chunk (batch) correlation, per-identifier lookup outcomes, the
/// returned directory records, and the reconstructed output grid. It is deliberately
/// standalone — it is NOT wired into the endpoint and activates no unfinished behavior.
/// Its purpose is to let <see cref="CapacityMeasurement"/> read the retained heap of the
/// structures the P05 memory equation reserves for, which the earlier disposable probe
/// omitted.
/// </summary>
internal sealed class BatchEnrichmentModel
{
    /// <summary>Unique identifier → the input row indices that carry it (dedup index).</summary>
    public Dictionary<string, List<int>> DedupIndex { get; }

    /// <summary>Distinct identifiers in first-seen order (the batch input spine).</summary>
    public List<string> UniqueIdentifiers { get; }

    /// <summary>Chunks of unique identifiers, one per modeled LDAP call.</summary>
    public List<List<string>> Chunks { get; }

    /// <summary>Per unique identifier, the classified lookup outcome.</summary>
    public Dictionary<string, LookupOutcomeKind> Outcomes { get; }

    /// <summary>Per found identifier, the returned directory record kept alive.</summary>
    public Dictionary<string, DirectoryRecord> Records { get; }

    /// <summary>The reconstructed output grid: one row per original input row.</summary>
    public List<List<string>> OutputRows { get; }

    public long TotalRows { get; }
    public long UniqueCount => UniqueIdentifiers.Count;
    public long LdapCalls => Chunks.Count;
    public double AverageChunkSize => Chunks.Count == 0 ? 0 : (double)UniqueCount / Chunks.Count;
    public long OutputColumns { get; }
    public long OutputCells => (long)OutputRows.Count * OutputColumns;

    private BatchEnrichmentModel(
        Dictionary<string, List<int>> dedupIndex,
        List<string> uniqueIdentifiers,
        List<List<string>> chunks,
        Dictionary<string, LookupOutcomeKind> outcomes,
        Dictionary<string, DirectoryRecord> records,
        List<List<string>> outputRows,
        long totalRows,
        long outputColumns)
    {
        DedupIndex = dedupIndex;
        UniqueIdentifiers = uniqueIdentifiers;
        Chunks = chunks;
        Outcomes = outcomes;
        Records = records;
        OutputRows = outputRows;
        TotalRows = totalRows;
        OutputColumns = outputColumns;
    }

    /// <summary>
    /// Builds the full retained model for a fixture. The match-column values are the
    /// fixture's per-row identifiers; <paramref name="duplicateEvery"/> collapses every
    /// Nth row onto a shared identifier so the dedup ratio can be swept (0 = all unique).
    /// <paramref name="notFoundEvery"/> and <paramref name="ambiguousEvery"/> drive the
    /// outcome mix. Retrieved attribute values are synthetic and fixed-width.
    /// </summary>
    public static BatchEnrichmentModel Build(
        CsvFixtureShape shape,
        IReadOnlyList<string> retrieveAttributes,
        int batchSize,
        int duplicateEvery,
        int notFoundEvery,
        int ambiguousEvery,
        int retrievedValueCodeUnits)
    {
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var dedupIndex = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        var uniqueIdentifiers = new List<string>();

        for (var row = 0; row < shape.Rows; row++)
        {
            var identifier = IdentifierForRow(row, duplicateEvery);
            if (!dedupIndex.TryGetValue(identifier, out var rowList))
            {
                rowList = new List<int>();
                dedupIndex[identifier] = rowList;
                uniqueIdentifiers.Add(identifier);
            }

            rowList.Add(row);
        }

        var chunks = new List<List<string>>();
        for (var start = 0; start < uniqueIdentifiers.Count; start += batchSize)
        {
            var count = Math.Min(batchSize, uniqueIdentifiers.Count - start);
            chunks.Add(uniqueIdentifiers.GetRange(start, count));
        }

        var outcomes = new Dictionary<string, LookupOutcomeKind>(StringComparer.Ordinal);
        var records = new Dictionary<string, DirectoryRecord>(StringComparer.Ordinal);
        var retrievedValue = retrievedValueCodeUnits <= 0
            ? string.Empty
            : new string('x', retrievedValueCodeUnits);

        for (var i = 0; i < uniqueIdentifiers.Count; i++)
        {
            var identifier = uniqueIdentifiers[i];
            var outcome = ClassifyOutcome(i, notFoundEvery, ambiguousEvery);
            outcomes[identifier] = outcome;

            if (outcome == LookupOutcomeKind.Found)
            {
                records[identifier] = BuildRecord(identifier, retrieveAttributes, retrievedValue);
            }
        }

        var outputColumns = shape.Columns + retrieveAttributes.Count + 1; // + AD_Status
        var outputRows = new List<List<string>>(shape.Rows);
        var emptyCell = string.Empty;

        for (var row = 0; row < shape.Rows; row++)
        {
            var identifier = IdentifierForRow(row, duplicateEvery);
            var outcome = outcomes[identifier];
            var outputRow = new List<string>(outputColumns) { identifier };
            for (var c = 1; c < shape.Columns; c++)
            {
                outputRow.Add(emptyCell);
            }

            if (outcome == LookupOutcomeKind.Found && records.TryGetValue(identifier, out var record))
            {
                foreach (var attribute in retrieveAttributes)
                {
                    outputRow.Add(record.GetString(attribute) ?? string.Empty);
                }
            }
            else
            {
                for (var a = 0; a < retrieveAttributes.Count; a++)
                {
                    outputRow.Add(emptyCell);
                }
            }

            outputRow.Add(outcome.ToString());
            outputRows.Add(outputRow);
        }

        return new BatchEnrichmentModel(
            dedupIndex,
            uniqueIdentifiers,
            chunks,
            outcomes,
            records,
            outputRows,
            shape.Rows,
            outputColumns);
    }

    private static string IdentifierForRow(int row, int duplicateEvery)
    {
        if (duplicateEvery > 1 && row % duplicateEvery != 0)
        {
            // Collapse onto the most recent "anchor" row so a share of rows repeat.
            var anchor = row - (row % duplicateEvery);
            return CsvCapacityFixtures.BuildIdentifier(anchor);
        }

        return CsvCapacityFixtures.BuildIdentifier(row);
    }

    private static LookupOutcomeKind ClassifyOutcome(int index, int notFoundEvery, int ambiguousEvery)
    {
        if (ambiguousEvery > 0 && index % ambiguousEvery == 0)
        {
            return LookupOutcomeKind.Ambiguous;
        }

        if (notFoundEvery > 0 && index % notFoundEvery == 0)
        {
            return LookupOutcomeKind.NotFound;
        }

        return LookupOutcomeKind.Found;
    }

    private static DirectoryRecord BuildRecord(
        string identifier,
        IReadOnlyList<string> retrieveAttributes,
        string retrievedValue)
    {
        var record = new DirectoryRecord
        {
            ObjectType = DirectoryObjectType.User,
            DistinguishedName = $"CN={identifier},OU=Benchmark,DC=example,DC=invalid",
        };

        foreach (var attribute in retrieveAttributes)
        {
            record[attribute] = retrievedValue;
        }

        return record;
    }
}
