using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AdQuery.Orchestrator.Tests.Benchmarks;

/// <summary>
/// Executes the P05 Slice 0 evidence matrix and writes raw results plus derived variance
/// to the ignored <c>artifacts/</c> tree. Both entry points that call this are gated on
/// the <c>ADQUERY_CAPACITY_MATRIX</c> environment variable and skip when it is unset, so
/// the canonical verification run (which executes the suite unfiltered) never runs the
/// matrix and stays inert. No live provider, directory, or production output root is used.
/// </summary>
internal static class CapacityMatrixRunner
{
    public const string GateVariable = "ADQUERY_CAPACITY_MATRIX";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The five documented match candidates carry the retrieved-attribute shape.</summary>
    private static readonly string[] RetrieveAttributes = ["displayName", "department", "title"];

    /// <summary>Returns true when the gate is set; otherwise the caller should skip.</summary>
    public static bool IsEnabled =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GateVariable));

    /// <summary>
    /// Runs the analytic byte/structure matrix over every content family, duplicate ratio,
    /// batch candidate, and the row spine, plus process-budget HTTP measurements on the row
    /// spine with repeats for variance. Writes one JSON artifact and returns its path.
    /// </summary>
    public static string Run(string artifactsRoot)
    {
        var outputDir = Path.Combine(artifactsRoot, "capacity");
        Directory.CreateDirectory(outputDir);

        var analytic = RunAnalyticMatrix();
        var process = RunProcessBudgetMatrix(outputDir);
        var retainedStructures = RunRetainedStructureMatrix();

        var report = new MatrixReport
        {
            Host = Environment.MachineName,
            ProcessorCount = Environment.ProcessorCount,
            Is64BitProcess = Environment.Is64BitProcess,
            ServerGc = System.Runtime.GCSettings.IsServerGC,
            AnalyticCases = analytic,
            ProcessBudgetCases = process,
            RetainedStructureCases = retainedStructures,
        };

        var path = Path.Combine(outputDir, "capacity-matrix.json");
        File.WriteAllText(path, JsonSerializer.Serialize(report, Json));
        return path;
    }

    private static List<AnalyticCase> RunAnalyticMatrix()
    {
        int[] rowSpine = [10_000, 50_000, 100_000];
        int[] duplicatePercents = [0, 50, 90];
        int[] batchSizes = [50, 250, 500, 1_000];
        CsvContentKind[] families =
        [
            CsvContentKind.Ascii,
            CsvContentKind.Quote,
            CsvContentKind.ThreeByteUtf8,
            CsvContentKind.ControlEscaped,
        ];

        const int Columns = 10;
        const int CellCodeUnits = 16;
        const int RetrievedValueCodeUnits = 32;
        const string Query = "Enrich these users with directory attributes";

        var cases = new List<AnalyticCase>();

        foreach (var rows in rowSpine)
        {
            foreach (var family in families)
            {
                var shape = new CsvFixtureShape(rows, Columns, CellCodeUnits, family);
                var headers = CsvCapacityFixtures.BuildHeaders(Columns);
                var sampleRow = SampleRow(shape);
                var patterns = new Dictionary<string, string>
                {
                    ["Employee"] = "short alphanumeric (8 chars or less) - use sAMAccountName",
                };

                var provider = ProviderRequestMeasurement.Measure(Query, headers, rows, patterns);

                var outputHeaders = BuildOutputHeaders(headers);
                var sampleOutputRow = BuildSampleOutputRow(sampleRow, RetrievedValueCodeUnits);

                foreach (var duplicatePercent in duplicatePercents)
                {
                    var duplicateEvery = DuplicateEveryFor(duplicatePercent);
                    foreach (var batchSize in batchSizes)
                    {
                        var rendered = SampleFilter(shape, batchSize);
                        var model = BatchEnrichmentModel.Build(
                            shape,
                            RetrieveAttributes,
                            batchSize,
                            duplicateEvery,
                            notFoundEvery: 0,
                            ambiguousEvery: 0,
                            retrievedValueCodeUnits: RetrievedValueCodeUnits);

                        cases.Add(new AnalyticCase
                        {
                            Rows = rows,
                            Columns = Columns,
                            Content = family.ToString(),
                            DuplicatePercent = duplicatePercent,
                            BatchSize = batchSize,
                            UniqueIdentifiers = model.UniqueCount,
                            LdapCalls = model.LdapCalls,
                            AverageChunkSize = model.AverageChunkSize,
                            OutputCells = model.OutputCells,
                            RawCsvInputBytes = CsvCapacityByteModel.RawCsvInputBytes(headers, sampleRow, rows),
                            JsonRequestBodyBytes = CsvCapacityByteModel.JsonRequestBodyBytes(Query, headers, sampleRow, rows),
                            ProviderRequestBytes = provider.RequestBodyBytes,
                            ProviderOutputTokenReserve = provider.OutputTokenReserve,
                            EnrichedCsvOutputBytes = CsvCapacityByteModel.EnrichedCsvOutputBytes(outputHeaders, sampleOutputRow, rows),
                            CanonicalNdjsonBytes = CsvCapacityByteModel.CanonicalNdjsonBytes(outputHeaders, sampleOutputRow, rows),
                            RenderedFilterBytes = rendered.FilterBytes,
                            ConservativeBerRequestBytes = rendered.BerBytes,
                        });
                    }
                }
            }
        }

        return cases;
    }

    private static List<ProcessBudgetCase> RunProcessBudgetMatrix(string outputDir)
    {
        int[] rowSpine = [10_000, 50_000, 100_000];
        const int Columns = 10;
        const int Repeats = 3;

        var cases = new List<ProcessBudgetCase>();

        foreach (var rows in rowSpine)
        {
            var samples = new List<CapacitySample>(Repeats);
            for (var i = 0; i < Repeats; i++)
            {
                samples.Add(MeasureHttpProcessBudget(rows, Columns, outputDir));
            }

            var retained = samples.Select(s => s.RetainedHeapAboveBaseline).ToArray();
            var working = samples.Select(s => s.PeakWorkingSet).ToArray();

            cases.Add(new ProcessBudgetCase
            {
                Rows = rows,
                Columns = Columns,
                Repeats = Repeats,
                RetainedHeapMin = retained.Min(),
                RetainedHeapMax = retained.Max(),
                RetainedHeapMean = (long)retained.Average(),
                RetainedHeapSpreadPercent = SpreadPercent(retained),
                PeakWorkingSetMax = working.Max(),
                AllocatedBytesMean = (long)samples.Select(s => s.AllocatedBytesDelta).Average(),
                ElapsedMsMean = (long)samples.Select(s => s.ElapsedMs).Average(),
            });
        }

        return cases;
    }

    private static CapacitySample MeasureHttpProcessBudget(int rows, int columns, string outputDir)
    {
        var outputRoot = Path.Combine(outputDir, "http-output", Guid.NewGuid().ToString("N"));
        using var harness = new CapacityHttpHarness(outputRoot, RetrieveAttributes);
        using var client = harness.CreateClient();

        var shape = new CsvFixtureShape(rows, columns, CellCodeUnits: 16, Content: CsvContentKind.Ascii);
        var payload = new
        {
            query = "Add directory attributes",
            csvHeaders = CsvCapacityFixtures.BuildHeaders(columns),
            csvData = CsvCapacityFixtures.BuildRows(shape),
        };

        var (response, sample) = CapacityMeasurement.Measure(() =>
        {
            var result = client.PostAsJsonAsync("/api/query/csv-enrich", payload).GetAwaiter().GetResult();
            if (result.StatusCode != HttpStatusCode.OK)
            {
                throw new InvalidOperationException($"Enrichment returned {result.StatusCode}.");
            }

            return result;
        });

        response.Dispose();
        try
        {
            if (Directory.Exists(outputRoot))
            {
                Directory.Delete(outputRoot, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup of the isolated temp output root.
        }

        return sample;
    }

    /// <summary>
    /// Measures the retained managed heap of the benchmark-only planned batch structures
    /// (the dedup index, unique spine, chunks, outcomes, retained records, and reconstructed
    /// output grid) held live across a forced collection. This is the "retained-service
    /// reserve" term the P05 memory equation subtracts before dividing by the active-CSV
    /// count — measured here directly rather than only modeled arithmetically. The model is
    /// standalone and wires into no endpoint.
    /// </summary>
    private static List<RetainedStructureCase> RunRetainedStructureMatrix()
    {
        int[] rowSpine = [10_000, 50_000, 100_000];
        int[] duplicatePercents = [0, 90];
        const int Columns = 10;
        const int CellCodeUnits = 16;
        const int RetrievedValueCodeUnits = 32;
        const int BatchSize = 500;
        const int Repeats = 3;

        var cases = new List<RetainedStructureCase>();

        foreach (var rows in rowSpine)
        {
            foreach (var duplicatePercent in duplicatePercents)
            {
                var shape = new CsvFixtureShape(rows, Columns, CellCodeUnits, CsvContentKind.Ascii);
                var duplicateEvery = DuplicateEveryFor(duplicatePercent);

                var retained = new long[Repeats];
                var allocated = new long[Repeats];
                long uniqueCount = 0;
                for (var i = 0; i < Repeats; i++)
                {
                    var (model, sample) = CapacityMeasurement.Measure(() => BatchEnrichmentModel.Build(
                        shape,
                        RetrieveAttributes,
                        BatchSize,
                        duplicateEvery,
                        notFoundEvery: 0,
                        ambiguousEvery: 0,
                        retrievedValueCodeUnits: RetrievedValueCodeUnits));

                    retained[i] = sample.RetainedHeapAboveBaseline;
                    allocated[i] = sample.AllocatedBytesDelta;
                    uniqueCount = model.UniqueCount;
                }

                cases.Add(new RetainedStructureCase
                {
                    Rows = rows,
                    Columns = Columns,
                    DuplicatePercent = duplicatePercent,
                    BatchSize = BatchSize,
                    Repeats = Repeats,
                    UniqueIdentifiers = uniqueCount,
                    RetainedHeapMin = retained.Min(),
                    RetainedHeapMax = retained.Max(),
                    RetainedHeapMean = (long)retained.Average(),
                    RetainedHeapSpreadPercent = SpreadPercent(retained),
                    AllocatedBytesMean = (long)allocated.Average(),
                });
            }
        }

        return cases;
    }

    private static (long FilterBytes, long BerBytes) SampleFilter(CsvFixtureShape shape, int batchSize)
    {
        var chunk = new List<string>(batchSize);
        for (var i = 0; i < Math.Min(batchSize, shape.Rows); i++)
        {
            chunk.Add(CsvCapacityFixtures.BuildIdentifier(i));
        }

        var filterBytes = CsvCapacityByteModel.RenderedOrFilterBytes("sAMAccountName", chunk);
        var attributes = new List<string> { "distinguishedName" };
        attributes.AddRange(RetrieveAttributes);
        var berBytes = CsvCapacityByteModel.ConservativeBerRequestBytes(filterBytes, attributes);
        return (filterBytes, berBytes);
    }

    private static List<string> SampleRow(CsvFixtureShape shape)
    {
        var rows = CsvCapacityFixtures.BuildRows(shape with { Rows = 1 });
        return rows[0];
    }

    private static List<string> BuildOutputHeaders(List<string> inputHeaders)
    {
        var headers = new List<string>(inputHeaders);
        foreach (var attribute in RetrieveAttributes)
        {
            headers.Add($"AD_{attribute}");
        }

        headers.Add("AD_Status");
        return headers;
    }

    private static List<string> BuildSampleOutputRow(List<string> inputRow, int retrievedValueCodeUnits)
    {
        var row = new List<string>(inputRow);
        var value = retrievedValueCodeUnits <= 0 ? string.Empty : new string('x', retrievedValueCodeUnits);
        foreach (var _ in RetrieveAttributes)
        {
            row.Add(value);
        }

        row.Add("Found");
        return row;
    }

    private static int DuplicateEveryFor(int duplicatePercent) => duplicatePercent switch
    {
        0 => 0,       // all unique
        50 => 2,      // every 2nd row collapses → ~50% unique
        90 => 10,     // 1 anchor per 10 rows → ~10% unique
        _ => 0,
    };

    private static double SpreadPercent(long[] values)
    {
        var mean = values.Average();
        if (mean == 0)
        {
            return 0;
        }

        return (values.Max() - values.Min()) / mean * 100.0;
    }

    private sealed class MatrixReport
    {
        public string Host { get; set; } = string.Empty;
        public int ProcessorCount { get; set; }
        public bool Is64BitProcess { get; set; }
        public bool ServerGc { get; set; }
        public List<AnalyticCase> AnalyticCases { get; set; } = [];
        public List<ProcessBudgetCase> ProcessBudgetCases { get; set; } = [];
        public List<RetainedStructureCase> RetainedStructureCases { get; set; } = [];
    }

    private sealed class AnalyticCase
    {
        public int Rows { get; set; }
        public int Columns { get; set; }
        public string Content { get; set; } = string.Empty;
        public int DuplicatePercent { get; set; }
        public int BatchSize { get; set; }
        public long UniqueIdentifiers { get; set; }
        public long LdapCalls { get; set; }
        public double AverageChunkSize { get; set; }
        public long OutputCells { get; set; }
        public long RawCsvInputBytes { get; set; }
        public long JsonRequestBodyBytes { get; set; }
        public long ProviderRequestBytes { get; set; }
        public int ProviderOutputTokenReserve { get; set; }
        public long EnrichedCsvOutputBytes { get; set; }
        public long CanonicalNdjsonBytes { get; set; }
        public long RenderedFilterBytes { get; set; }
        public long ConservativeBerRequestBytes { get; set; }
    }

    private sealed class ProcessBudgetCase
    {
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int Repeats { get; set; }
        public long RetainedHeapMin { get; set; }
        public long RetainedHeapMax { get; set; }
        public long RetainedHeapMean { get; set; }
        public double RetainedHeapSpreadPercent { get; set; }
        public long PeakWorkingSetMax { get; set; }
        public long AllocatedBytesMean { get; set; }
        public long ElapsedMsMean { get; set; }
    }

    private sealed class RetainedStructureCase
    {
        public int Rows { get; set; }
        public int Columns { get; set; }
        public int DuplicatePercent { get; set; }
        public int BatchSize { get; set; }
        public int Repeats { get; set; }
        public long UniqueIdentifiers { get; set; }
        public long RetainedHeapMin { get; set; }
        public long RetainedHeapMax { get; set; }
        public long RetainedHeapMean { get; set; }
        public double RetainedHeapSpreadPercent { get; set; }
        public long AllocatedBytesMean { get; set; }
    }
}
