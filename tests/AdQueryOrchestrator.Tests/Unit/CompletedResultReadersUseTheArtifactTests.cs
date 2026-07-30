using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Controllers;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using AdQuery.Orchestrator.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

using static AdQuery.Orchestrator.Tests.Unit.AssemblyCallGraph;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F04 Slice 7 (F04-D5): every reader of a completed result reads the artifact of record.
/// <para>
/// The slice's order was load-bearing — create the artifact, migrate all four readers, only
/// then drop the 2h full-result <c>IMemoryCache</c> entry — because a reader left behind would
/// have started returning "results expired" the moment the cache went. There are four:
/// preview, the single-record headline, download, and cross-turn reuse. The first three are
/// covered here; reuse is covered by <c>ResultArtifactLifecycleTests</c>, which counts
/// traversals.
/// </para>
/// <para>
/// Two halves, because neither is sufficient alone. The behavioral half drives the real
/// endpoints against a real artifact on disk with no cache entry in existence. The static half
/// walks each reader's call graph and asserts it reaches the artifact store and never the
/// memory cache — <c>DownloadAsync</c> is only reachable that way, since it writes its audit
/// copy under the hard-coded <c>E:\WWWOutput</c> and so is not drivable on a build agent (the
/// same reason <c>ExportIsModelFreeTests</c> reads IL rather than driving it).
/// </para>
/// </summary>
public sealed class CompletedResultReadersUseTheArtifactTests : IDisposable
{
    private const string Owner = "ANALOG\\artifact-reader";
    private const string OwnerSam = "artifact-reader";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "adquery-readers-" + Guid.NewGuid().ToString("N"));

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
        }
    }

    [Fact]
    public async Task Preview_ServesRowsFromTheArtifactWithNoCacheEntryInExistence()
    {
        var (controller, job) = await CompletedJobAsync(rowCount: 250);

        var ok = Assert.IsType<OkObjectResult>(controller.GetJobPreview(job.JobId));

        var rows = Assert.IsAssignableFrom<IEnumerable<Dictionary<string, object?>>>(
            GetProperty<object>(ok.Value!, "rows")).ToList();

        // Bounded to the preview size, but the advertised total is the whole set: a preview
        // that reported its own slice would silently shrink every large answer.
        Assert.Equal(10, rows.Count);
        Assert.Equal("row-0", rows[0]["displayName"]?.ToString());
        Assert.Equal(250, GetProperty<int>(ok.Value!, "totalRows"));
        Assert.True(GetProperty<bool>(ok.Value!, "hasMore"));
    }

    [Fact]
    public async Task SingleRecordHeadline_KeepsTheRecordKind_NotDowngradedToCount()
    {
        // The record kind needs the one row, which now lives only in the artifact. A reader
        // still on the cache finds nothing, passes a null first row to the classifier, and the
        // answer silently degrades from "here is the person" to "1".
        var (controller, job) = await CompletedJobAsync(rowCount: 1);

        var ok = Assert.IsType<OkObjectResult>(controller.GetJobStatus(job.JobId));
        var result = GetProperty<object>(ok.Value!, "result");
        var headline = Assert.IsType<HeadlineResult>(GetProperty<object>(result!, "headline"));

        Assert.Equal(HeadlineKind.Record, headline.Kind);
        Assert.NotNull(headline.Record);
        Assert.Equal("row-0", headline.Record!["displayName"]?.ToString());
    }

    [Fact]
    public async Task AReaderWhoseArtifactIsGone_ReportsExpiredRatherThanFailing()
    {
        // Over-removal sentinel for the two assertions above: they must pass because the
        // artifact is being read, not because the endpoints answer regardless.
        var (controller, job) = await CompletedJobAsync(rowCount: 250);
        File.Delete(job.ResultArtifactPath!);

        var notFound = Assert.IsType<NotFoundObjectResult>(controller.GetJobPreview(job.JobId));
        Assert.Contains("expired", notFound.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(nameof(QueryController.GetJobPreview))]
    [InlineData(nameof(QueryController.GetJobStatus))]
    [InlineData(nameof(QueryController.DownloadAsync))]
    public void EveryCompletedResultReader_ReachesTheArtifactStoreAndNeverTheMemoryCache(string readerName)
    {
        // The controller legitimately keeps an IMemoryCache for the unrelated legacy
        // CachedQueryResult download path, so the claim has to be per-reader rather than
        // "the controller holds no cache".
        var reader = typeof(QueryController).GetMethod(readerName)
            ?? throw new InvalidOperationException($"QueryController.{readerName} was not found.");

        var reachable = ReachableMethods(reader);

        Assert.Contains(
            reachable,
            m => m.DeclaringType == typeof(JsonLinesResultArtifactStore) && m.Name == nameof(IResultArtifactStore.Read));

        var cacheCalls = reachable
            .SelectMany(m => CalledMembers(m).Select(callee => (Method: m, Callee: callee)))
            .Where(pair => IsMemoryCache(pair.Callee.DeclaringType))
            .Select(pair => $"{pair.Method.DeclaringType?.Name}.{pair.Method.Name} → {pair.Callee.Name}")
            .ToList();

        Assert.True(
            cacheCalls.Count == 0,
            $"{readerName} must read the artifact of record, not the results cache. Cache calls: "
            + string.Join("; ", cacheCalls));
    }

    [Fact]
    public void TheJobExecutionPath_NoLongerHoldsTheFullResultInACacheEntry()
    {
        // The fourth reader's other half: completion used to Set a 2h full-result entry, and
        // cross-turn reuse read it back. Both sides are gone — the manager has no cache at all.
        var executePath = ReachableMethods(
            typeof(QueryJobManager).GetMethod(nameof(QueryJobManager.ExecuteJobWithServicesAsync))!);

        var cacheCalls = executePath
            .SelectMany(m => CalledMembers(m).Select(callee => (Method: m, Callee: callee)))
            .Where(pair => IsMemoryCache(pair.Callee.DeclaringType))
            .Select(pair => $"{pair.Method.DeclaringType?.Name}.{pair.Method.Name} → {pair.Callee.Name}")
            .ToList();

        Assert.True(
            cacheCalls.Count == 0,
            "Job execution must persist results as the artifact of record, never as a cache "
            + "entry holding the set resident. Cache calls: " + string.Join("; ", cacheCalls));

        Assert.DoesNotContain(
            typeof(QueryJobManager).GetFields(BindingFlags.Instance | BindingFlags.NonPublic),
            f => IsMemoryCache(f.FieldType));
    }

    private static bool IsMemoryCache(Type? type) =>
        type != null && (typeof(IMemoryCache).IsAssignableFrom(type) || type == typeof(CacheExtensions));

    /// <summary>
    /// A completed job whose result exists only as an artifact on disk — the state every
    /// reader now faces, since nothing writes a results cache entry any more.
    /// </summary>
    private async Task<(QueryController Controller, QueryJob Job)> CompletedJobAsync(int rowCount)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Results:ArtifactRoot"] = _root })
            .Build();

        var artifacts = new JsonLinesResultArtifactStore(
            NullLogger<JsonLinesResultArtifactStore>.Instance, configuration);

        var job = new QueryJob
        {
            JobId = "job-1",
            UserName = OwnerSam,
            Query = "who reports to Sanjay",
            Status = JobStatus.Completed,
            CreatedAt = DateTime.UtcNow,
            TotalRows = rowCount,
            Plan = new DirectoryQueryPlan
            {
                Steps = { new DirectoryPlanStep { Step = 1, Name = "s1", Operation = "search" } },
                Projection = new ProjectionDefinition
                {
                    RowStep = "s1",
                    Columns = { new ProjectionColumn { Name = "displayName", Attribute = "displayName" } },
                },
            },
        };

        job.ResultArtifactPath = await artifacts.WriteAsync(
            job,
            new PlanExecutionResult
            {
                Success = true,
                Data = Enumerable.Range(0, rowCount)
                    .Select(i => new Dictionary<string, object?> { ["displayName"] = $"row-{i}" })
                    .ToList(),
            },
            TestContext.Current.CancellationToken);

        var controller = new QueryController(
            NullLogger<QueryController>.Instance,
            null!,
            null!,
            new MemoryCache(new MemoryCacheOptions()),
            configuration,
            new SingleJobManager(job),
            null!,
            null!,
            null!,
            artifacts)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, Owner)], "Test")),
                },
            },
        };

        return (controller, job);
    }

    private static T? GetProperty<T>(object source, string name)
    {
        var property = source.GetType().GetProperty(name);
        Assert.NotNull(property);
        return (T?)property!.GetValue(source);
    }

    private sealed class SingleJobManager(QueryJob job) : IQueryJobManager
    {
        public QueryJob? GetJob(string jobId) =>
            string.Equals(jobId, job.JobId, StringComparison.OrdinalIgnoreCase) ? job : null;

        public Task<string> CreateJobAsync(
            string userName,
            string query,
            string? context = null,
            int? requestedResultLimit = null,
            string? previousJobId = null,
            CancellationToken cancellationToken = default) => Task.FromResult("new-job");

        public Task EnqueueJobAsync(QueryJob queued, string? forceModel = null) => Task.CompletedTask;
        public void CancelJob(string jobId) { }
        public List<QueryJob> GetUserJobs(string userName) => [job];
        public List<QueryJob> GetQueuedJobs() => [];
        public void CleanupCompletedJobs(TimeSpan olderThan) { }
        public Task ExecuteJobWithServicesAsync(
            string jobId,
            IClaudeService claude,
            IPlanValidator validator,
            IDirectoryPlanExecutor executor,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
