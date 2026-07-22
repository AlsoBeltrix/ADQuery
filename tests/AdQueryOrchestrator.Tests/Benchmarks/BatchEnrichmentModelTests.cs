using Xunit;

namespace AdQuery.Orchestrator.Tests.Benchmarks;

/// <summary>
/// Guards the benchmark-only <see cref="BatchEnrichmentModel"/> arithmetic the P05
/// acceptance criteria depend on: LDAP calls equal ceil(unique / batch size), the dedup
/// ratio reduces unique work proportionally, and every input row is reconstructed. These
/// run in the normal suite and touch no live directory.
/// </summary>
public sealed class BatchEnrichmentModelTests
{
    private static readonly string[] RetrieveAttributes = ["displayName", "department"];

    [Theory]
    [InlineData(1000, 50)]
    [InlineData(1000, 250)]
    [InlineData(1000, 500)]
    [InlineData(1000, 1000)]
    [InlineData(1000, 300)] // non-divisor: exercises the ceil
    public void LdapCalls_EqualCeilUniqueOverBatch(int rows, int batchSize)
    {
        var model = Build(rows, batchSize, duplicateEvery: 0);

        var expectedCalls = (model.UniqueCount + batchSize - 1) / batchSize;
        Assert.Equal(expectedCalls, model.LdapCalls);
        Assert.Equal(rows, model.UniqueCount); // all unique when duplicateEvery == 0
    }

    [Fact]
    public void DuplicateRatio_ReducesUniqueWorkProportionally()
    {
        // duplicateEvery = 10 keeps 1 anchor per 10 rows plus... actually every row whose
        // index % 10 == 0 is a fresh anchor; the other 9 collapse onto it → ~10% unique.
        var model = Build(1000, batchSize: 500, duplicateEvery: 10);

        Assert.Equal(100, model.UniqueCount);
        Assert.Equal(1000, model.TotalRows);
        // Dedup index row-lists cover every input row exactly once.
        var mappedRows = model.DedupIndex.Values.Sum(list => list.Count);
        Assert.Equal(1000, mappedRows);
    }

    [Fact]
    public void EveryInputRowIsReconstructed_WithStatusColumn()
    {
        var model = Build(500, batchSize: 250, duplicateEvery: 0);

        Assert.Equal(500, model.OutputRows.Count);
        // original columns (3) + retrieve attributes (2) + AD_Status (1)
        Assert.Equal(6, model.OutputColumns);
        Assert.All(model.OutputRows, row => Assert.Equal(6, row.Count));
        Assert.Equal(500L * 6, model.OutputCells);
    }

    [Fact]
    public void FoundIdentifiers_RetainRecords_OthersDoNot()
    {
        var model = Build(300, batchSize: 100, duplicateEvery: 0);

        var found = model.Outcomes.Count(o => o.Value == LookupOutcomeKind.Found);
        Assert.Equal(found, model.Records.Count);
        Assert.All(
            model.Outcomes.Where(o => o.Value != LookupOutcomeKind.Found),
            o => Assert.DoesNotContain(o.Key, model.Records.Keys));
    }

    private static BatchEnrichmentModel Build(int rows, int batchSize, int duplicateEvery)
    {
        var shape = new CsvFixtureShape(rows, Columns: 3, CellCodeUnits: 8, Content: CsvContentKind.Ascii);
        return BatchEnrichmentModel.Build(
            shape,
            RetrieveAttributes,
            batchSize,
            duplicateEvery,
            notFoundEvery: 7,
            ambiguousEvery: 23,
            retrievedValueCodeUnits: 16);
    }
}
