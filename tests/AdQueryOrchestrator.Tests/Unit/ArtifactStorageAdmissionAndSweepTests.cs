using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F04 Slice 7 (f04-or-7): the two lifecycle elements that live outside a job's own execution —
/// disk admission at the front door, and the startup sweep that is now the only thing removing
/// artifacts the in-memory job store can no longer name after a restart.
/// </summary>
public sealed class ArtifactStorageAdmissionAndSweepTests : IDisposable
{
    private const string Owner = "ANALOG\\admission-user";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "adquery-admission-" + Guid.NewGuid().ToString("N"));

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
    public async Task AQuery_IsRefusedWithInsufficientStorage_WhenTheArtifactVolumeIsFull()
    {
        // Exhaustion has to surface before the job is accepted. Refusing after the traversal,
        // when the atomic write fails, throws away work the user already waited for.
        var manager = new RecordingJobManager();
        var controller = CreateController(manager, new FullDiskArtifactStore());

        var result = await controller.ExecuteQueryAsync(new QueryRequest { Query = "who is jane" });

        var status = Assert.IsType<ObjectResult>(result);
        Assert.Equal(StatusCodes.Status507InsufficientStorage, status.StatusCode);
        Assert.False(manager.CreateJobCalled);
    }

    [Fact]
    public async Task AQuery_IsAccepted_WhenThereIsRoom()
    {
        // Over-removal sentinel: the refusal above must be the admission check firing, not the
        // endpoint being broken for everything.
        var manager = new RecordingJobManager();
        var controller = CreateController(manager, new NoResultArtifactStore());

        var result = await controller.ExecuteQueryAsync(new QueryRequest { Query = "who is jane" });

        Assert.IsType<AcceptedResult>(result);
        Assert.True(manager.CreateJobCalled);
    }

    [Fact]
    public async Task StartupSweep_RemovesPlantedOrphans_AndKeepsArtifactsOfSurvivingJobs()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Results:ArtifactRoot"] = _root })
            .Build();

        var artifacts = new JsonLinesResultArtifactStore(
            NullLogger<JsonLinesResultArtifactStore>.Instance, configuration);

        var jobs = new InMemoryQueryJobStore();

        var live = await WriteArtifactAsync(artifacts, "live");
        var orphan = await WriteArtifactAsync(artifacts, "orphan");
        var interrupted = Path.Combine(
            Path.GetDirectoryName(live)!, "adquery_X_interrupted" + JsonLinesResultArtifactStore.TempExtension);
        File.WriteAllText(interrupted, "{\"TotalRows\":7}");

        jobs.StoreJob(new QueryJob
        {
            JobId = "live",
            UserName = "sweep-user",
            Status = JobStatus.Completed,
            ResultArtifactPath = live,
        });

        var sweeper = new ResultArtifactSweeper(
            artifacts, jobs, NullLogger<ResultArtifactSweeper>.Instance);

        await sweeper.StartAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(live));
        Assert.False(File.Exists(orphan));
        Assert.False(File.Exists(interrupted));
    }

    [Fact]
    public async Task StartupSweep_ThatThrows_DoesNotStopTheApplicationStarting()
    {
        // A leaked artifact is a disk problem; a service that cannot start is an outage.
        var sweeper = new ResultArtifactSweeper(
            new ThrowingArtifactStore(),
            new InMemoryQueryJobStore(),
            NullLogger<ResultArtifactSweeper>.Instance);

        await sweeper.StartAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> WriteArtifactAsync(IResultArtifactStore artifacts, string jobId) =>
        await artifacts.WriteAsync(
            new QueryJob
            {
                JobId = jobId,
                UserName = "sweep-user",
                Query = "q",
                CreatedAt = DateTime.UtcNow,
            },
            new PlanExecutionResult
            {
                Success = true,
                Data = [new Dictionary<string, object?> { ["Name"] = jobId }],
            },
            TestContext.Current.CancellationToken);

    private static QueryController CreateController(
        IQueryJobManager manager, IResultArtifactStore artifacts) =>
        new(
            NullLogger<QueryController>.Instance,
            null!,
            null!,
            new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().Build(),
            manager,
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

    private sealed class FullDiskArtifactStore : IResultArtifactStore
    {
        public Task<string> WriteAsync(
            QueryJob job, PlanExecutionResult result, CancellationToken cancellationToken = default) =>
            throw new IOException("the admission check should have refused this query");

        public ResultArtifact? Read(string? artifactPath, int? maxRows = null) => null;
        public void Delete(string? artifactPath) { }
        public int SweepOrphans(IReadOnlySet<string> livePaths) => 0;
        public bool HasRoomForAnotherResult() => false;
    }

    private sealed class ThrowingArtifactStore : IResultArtifactStore
    {
        public Task<string> WriteAsync(
            QueryJob job, PlanExecutionResult result, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public ResultArtifact? Read(string? artifactPath, int? maxRows = null) => null;
        public void Delete(string? artifactPath) { }
        public int SweepOrphans(IReadOnlySet<string> livePaths) => throw new IOException("volume offline");
        public bool HasRoomForAnotherResult() => true;
    }

    private sealed class RecordingJobManager : IQueryJobManager
    {
        public bool CreateJobCalled { get; private set; }

        public Task<string> CreateJobAsync(
            string userName,
            string query,
            string? context = null,
            int? requestedResultLimit = null,
            string? previousJobId = null,
            CancellationToken cancellationToken = default)
        {
            CreateJobCalled = true;
            return Task.FromResult("new-job");
        }

        public QueryJob? GetJob(string jobId) => null;
        public Task EnqueueJobAsync(QueryJob job, string? forceModel = null) => Task.CompletedTask;
        public void CancelJob(string jobId) { }
        public List<QueryJob> GetUserJobs(string userName) => [];
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
