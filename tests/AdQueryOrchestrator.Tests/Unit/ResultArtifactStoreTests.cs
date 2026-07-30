using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F04 Slice 7 (F04-D5) storage guards. The artifact of record replaced a 2h
/// <c>IMemoryCache</c> entry — a store with automatic expiry — with files on disk, which have
/// none. These cover what the store itself must therefore supply: an atomic write no reader can
/// observe half-finished, bounded reads that do not materialize the whole set, and the startup
/// sweep that is now the only thing removing crash orphans.
/// </summary>
public sealed class ResultArtifactStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "adquery-artifacts-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
            // A leaked temp directory is not a test failure.
        }
    }

    [Fact]
    public async Task WrittenArtifact_ReadsBackWholeWithItsPlanAndGroupValues()
    {
        var store = CreateStore();
        var job = Job("job-1", Plan("everyone under Sanjay"));

        var path = await store.WriteAsync(job, Result(3), TestContext.Current.CancellationToken);
        var artifact = store.Read(path);

        Assert.NotNull(artifact);
        Assert.Equal(3, artifact!.TotalRows);
        Assert.Equal(3, artifact.Rows.Count);
        Assert.Equal("row-0", artifact.Rows[0]["Name"]?.ToString());
        Assert.Equal(3, artifact.GroupValues.Count);
        Assert.Equal("g0", artifact.GroupValues[0][0]);
        Assert.Contains("everyone under Sanjay", artifact.PlanJson);
        Assert.Contains("a warning", artifact.Warnings);
    }

    [Fact]
    public async Task Incompleteness_SurvivesTheRoundTrip()
    {
        // ci-or-1. Whole-plan reuse rebuilds a later turn's result from this artifact, so a
        // partial set that read back as whole would give the second turn the confident answer
        // the first turn correctly caveated.
        var store = CreateStore();
        var partial = Result(3);
        partial.ResultIsIncomplete = true;

        var path = await store.WriteAsync(
            Job("job-1", Plan("capped")), partial, TestContext.Current.CancellationToken);

        Assert.True(store.Read(path)!.ResultIsIncomplete);
    }

    [Fact]
    public async Task ACompleteResult_ReadsBackComplete()
    {
        var store = CreateStore();
        var path = await store.WriteAsync(
            Job("job-1", Plan("whole")), Result(3), TestContext.Current.CancellationToken);

        Assert.False(store.Read(path)!.ResultIsIncomplete);
    }

    [Fact]
    public async Task BoundedRead_StopsAtTheRequestedRowCount_ButStillReportsTheRealTotal()
    {
        // Preview takes ten rows and the single-record headline takes one. A bounded read that
        // reported its own slice as the total would silently shrink every large answer, and one
        // that read the whole file to answer a question about its head would defeat the point of
        // unpinning results from RAM.
        var store = CreateStore();
        var path = await store.WriteAsync(
            Job("job-1", Plan("big")), Result(500), TestContext.Current.CancellationToken);

        var preview = store.Read(path, maxRows: 10);

        Assert.NotNull(preview);
        Assert.Equal(10, preview!.Rows.Count);
        Assert.Equal(500, preview.TotalRows);
    }

    [Fact]
    public async Task Read_OfAMissingOrDeletedArtifact_IsAbsentRatherThanAThrow()
    {
        var store = CreateStore();
        var path = await store.WriteAsync(
            Job("job-1", Plan("p")), Result(1), TestContext.Current.CancellationToken);

        store.Delete(path);

        Assert.Null(store.Read(path));
        Assert.Null(store.Read(null));

        // Deleting again is a no-op: retention and the sweep can both reach the same path.
        store.Delete(path);
    }

    [Fact]
    public async Task Read_OfACorruptArtifact_IsAbsentRatherThanAThrow()
    {
        // A reader must treat an unparseable artifact exactly as it treated an expired cache
        // entry, or a truncated file turns a completed job into a 500.
        var store = CreateStore();
        var path = await store.WriteAsync(
            Job("job-1", Plan("p")), Result(2), TestContext.Current.CancellationToken);

        File.WriteAllText(path, "{ not json at all");

        Assert.Null(store.Read(path));
    }

    [Fact]
    public async Task WriteLeavesNoTempFileBehind()
    {
        // The write is temp-then-move so no reader sees a partial artifact; a temp file
        // surviving a successful write would be swept as an orphan on the next start.
        var store = CreateStore();

        await store.WriteAsync(Job("job-1", Plan("p")), Result(4), TestContext.Current.CancellationToken);

        Assert.Empty(Directory.EnumerateFiles(
            _root, "*" + JsonLinesResultArtifactStore.TempExtension, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task AnInterruptedWrite_LeavesNoTempFileBehindEither()
    {
        // The startup sweep only runs when no process is alive to have leaked one, so a write
        // that fails or is cancelled while the service is up must reclaim its own partial —
        // otherwise cancelled large queries accumulate full-size files against the very volume
        // the admission check refuses new queries to protect.
        var store = CreateStore();
        using var cancellation = new CancellationTokenSource();

        // Cancel after the header, while the row loop (where a large write spends its time)
        // is still running.
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => store.WriteAsync(Job("job-1", Plan("p")), Result(500), cancellation.Token));

        Assert.Empty(Directory.EnumerateFiles(
            _root, "*" + JsonLinesResultArtifactStore.TempExtension, SearchOption.AllDirectories));
    }

    [Fact]
    public async Task SweepOrphans_RemovesTempFilesAndUnreferencedArtifacts_KeepingLiveOnes()
    {
        var store = CreateStore();

        var live = await store.WriteAsync(
            Job("live", Plan("p")), Result(1), TestContext.Current.CancellationToken);
        var orphan = await store.WriteAsync(
            Job("orphan", Plan("p")), Result(1), TestContext.Current.CancellationToken);

        // A temp file at startup is by definition an interrupted atomic write.
        var interrupted = Path.Combine(
            Path.GetDirectoryName(live)!, "adquery_X_partial" + JsonLinesResultArtifactStore.TempExtension);
        File.WriteAllText(interrupted, "{\"TotalRows\":1}");

        var removed = store.SweepOrphans(new HashSet<string>(StringComparer.OrdinalIgnoreCase) { live });

        Assert.Equal(2, removed);
        Assert.True(File.Exists(live));
        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(interrupted));
    }

    [Fact]
    public async Task SweepOrphans_LeavesTheCsvAndLogAuditTrailAlone()
    {
        // Artifacts share the per-user directory with the export and log copies the download
        // path writes. A sweep that matched on directory rather than extension would delete a
        // user's audit trail.
        var store = CreateStore();
        var artifact = await store.WriteAsync(
            Job("job-1", Plan("p")), Result(1), TestContext.Current.CancellationToken);

        var directory = Path.GetDirectoryName(artifact)!;
        var csv = Path.Combine(directory, "adquery_OWNER_20260101_000000000.csv");
        var log = Path.Combine(directory, "adquery_OWNER_20260101_000000000.log");
        File.WriteAllText(csv, "Name\nAda\n");
        File.WriteAllText(log, "Records: 1\n");

        store.SweepOrphans(new HashSet<string>(StringComparer.OrdinalIgnoreCase));

        Assert.False(File.Exists(artifact));
        Assert.True(File.Exists(csv));
        Assert.True(File.Exists(log));
    }

    [Fact]
    public void DiskAdmission_RefusesWhenFreeSpaceIsBelowTheConfiguredFloor()
    {
        // Exhaustion must surface as a refusal before a query is accepted, not as an atomic
        // write that fails partway through a job that already did its directory work.
        var impossible = CreateStore(minimumFreeBytes: long.MaxValue);
        var ordinary = CreateStore(minimumFreeBytes: 1);

        Assert.False(impossible.HasRoomForAnotherResult());
        Assert.True(ordinary.HasRoomForAnotherResult());
    }

    private JsonLinesResultArtifactStore CreateStore(long? minimumFreeBytes = null)
    {
        var settings = new Dictionary<string, string?> { ["Results:ArtifactRoot"] = _root };
        if (minimumFreeBytes.HasValue)
        {
            settings["Results:MinimumFreeDiskBytes"] = minimumFreeBytes.Value.ToString();
        }

        return new JsonLinesResultArtifactStore(
            NullLogger<JsonLinesResultArtifactStore>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
    }

    private static QueryJob Job(string jobId, DirectoryQueryPlan plan) => new()
    {
        JobId = jobId,
        UserName = "artifact-user",
        Query = "q",
        Plan = plan,
        CreatedAt = DateTime.UtcNow,
    };

    private static DirectoryQueryPlan Plan(string description) => new()
    {
        Description = description,
        Steps = { new DirectoryPlanStep { Step = 1, Name = "s1", Operation = "search" } },
        Projection = new ProjectionDefinition { RowStep = "s1" },
    };

    private static PlanExecutionResult Result(int rows) => new()
    {
        Success = true,
        Data = Enumerable.Range(0, rows)
            .Select(i => new Dictionary<string, object?> { ["Name"] = $"row-{i}" })
            .ToList(),
        GroupValues = Enumerable.Range(0, rows)
            .Select(IReadOnlyList<string?> (i) => new[] { $"g{i}" })
            .ToList(),
        Warnings = ["a warning"],
    };
}
