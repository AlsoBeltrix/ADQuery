using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F04 Slice 7 (F04-D5, f04-or-7) job-lifecycle guards, driving the real
/// <see cref="QueryJobManager"/> against a real artifact store rooted in a temp directory.
/// <para>
/// D5 moved results from a store with automatic expiry (a 2h <c>IMemoryCache</c> entry) to one
/// with none, so retention, reuse ownership, and the reuse optimization itself are all part of
/// this slice rather than a follow-up. These cover the four behaviors that only appear when a
/// whole job runs: whole-plan reuse traverses once, a differing projection does not reuse,
/// expiry deletes the file, and an artifact a surviving job still points at is kept.
/// </para>
/// </summary>
public sealed class ResultArtifactLifecycleTests : IDisposable
{
    private const string User = "lifecycle-user";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "adquery-lifecycle-" + Guid.NewGuid().ToString("N"));

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
    public async Task TwoTurnsWithAnIdenticalPlan_TraverseTheDirectoryOnce()
    {
        // The only optimization in the slice. The second turn's complete serialized plan is
        // byte-identical to the first's, so it reads the first turn's artifact instead of
        // asking the directory the same question again.
        var (manager, store, _) = CreateManager();
        var executor = new CountingExecutor();

        var first = await RunTurnAsync(manager, store, executor, "everyone under Sanjay", "displayName");
        var second = await RunTurnAsync(
            manager, store, executor, "everyone under Sanjay", "displayName", previousJobId: first.JobId);

        Assert.Equal(1, executor.Traversals);
        Assert.Equal(JobStatus.Completed, second.Status);
        Assert.Equal(first.TotalRows, second.TotalRows);

        // Reuse points at the existing file rather than writing a second copy of it.
        Assert.Equal(first.ResultArtifactPath, second.ResultArtifactPath);
    }

    [Fact]
    public async Task TwoTurnsWithIdenticalMembershipButADifferentProjection_EachGetTheirOwnShape()
    {
        // The artifact holds rows already filtered and reduced to one turn's columns, so
        // membership-step equality is not sufficient grounds for reuse: "everyone under Sanjay"
        // → "…with their titles" would otherwise return rows with no Title column.
        var (manager, store, artifacts) = CreateManager();
        var executor = new CountingExecutor();

        var first = await RunTurnAsync(manager, store, executor, "everyone under Sanjay", "displayName");
        var second = await RunTurnAsync(
            manager, store, executor, "everyone under Sanjay", "title", previousJobId: first.JobId);

        Assert.Equal(2, executor.Traversals);
        Assert.NotEqual(first.ResultArtifactPath, second.ResultArtifactPath);

        Assert.Contains("displayName", artifacts.Read(first.ResultArtifactPath)!.Rows[0].Keys);
        Assert.Contains("title", artifacts.Read(second.ResultArtifactPath)!.Rows[0].Keys);
    }

    [Fact]
    public async Task AnExpiredJob_LeavesNoArtifactOnDisk()
    {
        // Retention is now the only thing bounding artifact disk use, and it has to delete the
        // file *before* RemoveJob drops the metadata naming it.
        var (manager, store, _) = CreateManager();
        var executor = new CountingExecutor();

        var job = await RunTurnAsync(manager, store, executor, "everyone under Sanjay", "displayName");
        var path = job.ResultArtifactPath!;
        Assert.True(File.Exists(path));

        manager.CleanupCompletedJobs(TimeSpan.Zero);

        Assert.Null(store.GetJob(job.JobId));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AnArtifactAReusingJobStillPointsAt_SurvivesItsOriginatorsExpiry()
    {
        // Whole-plan reuse aliases one file from two jobs. Expiring the writer must not delete
        // the file the surviving reader reads.
        var (manager, store, _) = CreateManager();
        var executor = new CountingExecutor();

        var first = await RunTurnAsync(manager, store, executor, "everyone under Sanjay", "displayName");
        var second = await RunTurnAsync(
            manager, store, executor, "everyone under Sanjay", "displayName", previousJobId: first.JobId);

        var shared = first.ResultArtifactPath!;
        Assert.Equal(shared, second.ResultArtifactPath);

        // Only the originator has expired: it completed in the past, the reusing turn has not.
        first.CompletedAt = DateTime.UtcNow.AddHours(-48);
        manager.CleanupCompletedJobs(TimeSpan.FromHours(24));

        Assert.Null(store.GetJob(first.JobId));
        Assert.NotNull(store.GetJob(second.JobId));
        Assert.True(File.Exists(shared));

        // And once the last owner expires, the file goes.
        second.CompletedAt = DateTime.UtcNow.AddHours(-48);
        manager.CleanupCompletedJobs(TimeSpan.FromHours(24));

        Assert.False(File.Exists(shared));
    }

    [Fact]
    public async Task ALargeResult_LivesInItsArtifactRatherThanInTheCompletedJob()
    {
        // The 2h full-result cache entry is gone: a completed job carries a path and a count,
        // never the set. Nothing between completion and export holds 40k rows resident.
        var (manager, store, artifacts) = CreateManager();
        var executor = new CountingExecutor { RowCount = 40_000 };

        var job = await RunTurnAsync(manager, store, executor, "everyone", "displayName");

        Assert.Equal(40_000, job.TotalRows);
        Assert.NotNull(job.ResultArtifactPath);
        Assert.Equal(40_000, artifacts.Read(job.ResultArtifactPath)!.TotalRows);

        // The completed job exposes no row-bearing state of its own.
        var rowBearing = typeof(QueryJob).GetProperties()
            .Where(p => typeof(IEnumerable<Dictionary<string, object?>>).IsAssignableFrom(p.PropertyType))
            .Select(p => p.Name)
            .ToList();
        Assert.Empty(rowBearing);

        // Export-sized reads come off disk; a bounded reader still only takes its bound.
        Assert.Equal(10, artifacts.Read(job.ResultArtifactPath, maxRows: 10)!.Rows.Count);
    }

    [Fact]
    public async Task AReusingTurn_NeverCompletesPointingAtAnArtifactRetentionDeleted()
    {
        // Reuse reads an ancestor artifact and claims it a moment later; retention runs on the
        // executor loop every second and may delete exactly that file in between. The
        // interleaving is forced deterministically here — the sweep fires from inside the
        // reuse read — because a real preemption in a few instructions of synchronous code is
        // not reproducible. Either outcome is correct: reuse a live artifact, or traverse.
        // Completing with a path to a deleted file is not.
        var real = new JsonLinesResultArtifactStore(
            NullLogger<JsonLinesResultArtifactStore>.Instance, Configuration());

        QueryJobManager? manager = null;
        QueryJob? ancestor = null;

        var artifacts = new SweepOnReadArtifactStore(real, () =>
        {
            // Everything the retention sweep needs: the ancestor is past its retention.
            ancestor!.CompletedAt = DateTime.UtcNow.AddHours(-48);
            manager!.CleanupCompletedJobs(TimeSpan.FromHours(24));
        });

        var (createdManager, store, _) = CreateManager(artifacts);
        manager = createdManager;
        var executor = new CountingExecutor();

        ancestor = await RunTurnAsync(manager, store, executor, "everyone under Sanjay", "displayName");

        var second = await RunTurnAsync(
            manager, store, executor, "everyone under Sanjay", "displayName", previousJobId: ancestor.JobId);

        Assert.NotNull(second.ResultArtifactPath);
        Assert.True(
            File.Exists(second.ResultArtifactPath),
            "a completed job must never point at an artifact retention already deleted");
    }

    [Fact]
    public async Task AJobWhoseArtifactWriteFails_Fails_AndIsNeverNarrated()
    {
        // The artifact is the only place a completed result lives, so completing without one
        // would mean Completed with nothing readable: preview 404s, download is refused, and a
        // single-record headline degrades to a count — with nothing saying the result was lost.
        var artifacts = new FailingWriteArtifactStore();
        var (manager, store, _) = CreateManager(artifacts);
        var claude = new StubClaude("everyone under Sanjay", "displayName");

        var jobId = await manager.CreateJobAsync(
            User, "everyone under Sanjay", cancellationToken: TestContext.Current.CancellationToken);

        await manager.ExecuteJobWithServicesAsync(
            jobId,
            claude,
            new PermissiveValidator(),
            new CountingExecutor(),
            TestContext.Current.CancellationToken);

        var job = store.GetJob(jobId);
        Assert.NotNull(job);
        Assert.Equal(JobStatus.Failed, job!.Status);
        Assert.Null(job.ResultArtifactPath);

        // Narrate is the expensive half of a turn; describing a result already lost buys nothing.
        Assert.Equal(0, claude.AnswerCalls);
    }

    [Fact]
    public async Task AJobCancelledAfterItsArtifactIsWritten_LeavesNothingOnDisk()
    {
        // slice2-or-2. D5 writes the artifact before SetCompleted, so cancellation during
        // Narrate strands a file that neither reclamation path can reach: retention sweeps
        // Completed only, and the orphan sweeper treats a path any job still names as live.
        // Reclaiming on the terminal transition is what closes that gap.
        var (manager, store, _) = CreateManager();

        var jobId = await manager.CreateJobAsync(
            User, "everyone under Sanjay", cancellationToken: TestContext.Current.CancellationToken);

        await manager.ExecuteJobWithServicesAsync(
            jobId,
            new StubClaude("everyone under Sanjay", "displayName") { CancelOnNarrate = true },
            new PermissiveValidator(),
            new CountingExecutor(),
            TestContext.Current.CancellationToken);

        var job = store.GetJob(jobId)!;
        Assert.Equal(JobStatus.Cancelled, job.Status);
        Assert.Null(job.ResultArtifactPath);

        var artifactRoot = Path.Combine(_root, User);
        Assert.Empty(Directory.Exists(artifactRoot)
            ? Directory.EnumerateFiles(
                artifactRoot, "*" + JsonLinesResultArtifactStore.ArtifactExtension)
            : []);
    }

    [Fact]
    public async Task ACancelledTurnReusingAnAncestorsArtifact_LeavesThatArtifactAlone()
    {
        // The released path may not be the releasing job's to delete: whole-plan reuse aliases
        // one file across turns, so release asks the same ownership question retention does.
        var (manager, store, _) = CreateManager();
        var executor = new CountingExecutor();

        var ancestor = await RunTurnAsync(manager, store, executor, "everyone under Sanjay", "displayName");
        var shared = ancestor.ResultArtifactPath!;

        var jobId = await manager.CreateJobAsync(
            User,
            "everyone under Sanjay",
            previousJobId: ancestor.JobId,
            cancellationToken: TestContext.Current.CancellationToken);

        await manager.ExecuteJobWithServicesAsync(
            jobId,
            new StubClaude("everyone under Sanjay", "displayName") { CancelOnNarrate = true },
            new PermissiveValidator(),
            executor,
            TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Cancelled, store.GetJob(jobId)!.Status);
        Assert.True(File.Exists(shared), "the ancestor still reads the artifact it wrote");
        Assert.Equal(shared, store.GetJob(ancestor.JobId)!.ResultArtifactPath);
    }

    [Fact]
    public async Task AJobWritesItsAuditTrailUnderTheConfiguredRoot_NotTheDeployedServersVolume()
    {
        // QueryLogHelper.OutputRoot is an absolute path that exists only on the deployed
        // server, and the job's audit-trail directory is created *before* the method's try, so
        // a manager that ignores Results:ArtifactRoot throws DirectoryNotFoundException before
        // the job starts — on a build agent, every job-driving test at once. Slice 7 made the
        // artifact root configurable and left this write on the hard-coded one; CI caught it
        // on b4ed25f only because this machine happens to have the volume.
        var (manager, store, _) = CreateManager();

        var job = await RunTurnAsync(
            manager, store, new CountingExecutor(), "everyone under Sanjay", "displayName");

        var auditDirectory = Path.Combine(_root, User);
        Assert.True(
            Directory.Exists(auditDirectory),
            "the job's .log/.csv audit trail belongs beside its artifact under the configured root");
        Assert.NotEmpty(Directory.EnumerateFiles(auditDirectory, "*.log"));

        // And specifically not on the deployed server's volume.
        Assert.StartsWith(_root, job.ResultArtifactPath!, StringComparison.OrdinalIgnoreCase);
    }

    private async Task<QueryJob> RunTurnAsync(
        QueryJobManager manager,
        IQueryJobStore store,
        CountingExecutor executor,
        string description,
        string projectedAttribute,
        string? previousJobId = null)
    {
        var jobId = await manager.CreateJobAsync(
            User,
            description,
            previousJobId: previousJobId,
            cancellationToken: TestContext.Current.CancellationToken);

        await manager.ExecuteJobWithServicesAsync(
            jobId,
            new StubClaude(description, projectedAttribute),
            new PermissiveValidator(),
            executor,
            TestContext.Current.CancellationToken);

        var job = store.GetJob(jobId);
        Assert.NotNull(job);
        Assert.Equal(JobStatus.Completed, job!.Status);
        return job;
    }

    private IConfiguration Configuration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Results:ArtifactRoot"] = _root })
            .Build();

    private (QueryJobManager Manager, IQueryJobStore Store, IResultArtifactStore Artifacts) CreateManager(
        IResultArtifactStore? artifactStore = null)
    {
        var configuration = Configuration();

        var store = new InMemoryQueryJobStore();
        var artifacts = artifactStore ?? new JsonLinesResultArtifactStore(
            NullLogger<JsonLinesResultArtifactStore>.Instance, configuration);

        var manager = new QueryJobManager(
            store,
            new InMemoryQueryJobQueue(),
            NullLogger<QueryJobManager>.Instance,
            new PlanPreprocessor(configuration),
            new FollowUpContextEnforcer(
                Options.Create(new FollowUpOptions()),
                NullLogger<FollowUpContextEnforcer>.Instance),
            new AnswerReductionBuilder(Options.Create(new AnswerOptions())),
            artifacts,
            configuration);

        return (manager, store, artifacts);
    }

    /// <summary>
    /// Counts traversals so reuse is proven by the directory work not happening, not by a
    /// timing or a log line. Rows carry whichever attribute the turn projected, so a reused
    /// artifact of the wrong shape is visible in the rows themselves.
    /// </summary>
    private sealed class CountingExecutor : IDirectoryPlanExecutor
    {
        public int Traversals { get; private set; }
        public int RowCount { get; init; } = 3;

        public Task<PlanExecutionResult> ExecutePlanAsync(
            DirectoryQueryPlan plan, CancellationToken cancellationToken = default)
            => ExecutePlanAsync(plan, new Progress<PlanProgressUpdate>(), cancellationToken);

        public Task<PlanExecutionResult> ExecutePlanAsync(
            DirectoryQueryPlan plan, IProgress<PlanProgressUpdate> progress, CancellationToken cancellationToken)
        {
            Traversals++;

            var attribute = plan.Projection?.Columns.FirstOrDefault()?.Attribute ?? "displayName";

            return Task.FromResult(new PlanExecutionResult
            {
                Success = true,
                Data = Enumerable.Range(0, RowCount)
                    .Select(i => new Dictionary<string, object?> { [attribute] = $"{attribute}-{i}" })
                    .ToList(),
            });
        }

        public Task<PlanValidationResult> ValidatePlanAsync(
            DirectoryQueryPlan plan, CancellationToken cancellationToken = default)
            => Task.FromResult(new PlanValidationResult { IsValid = true });
    }

    /// <summary>
    /// A real store that runs a callback the first time a read finds an artifact, so the
    /// retention sweep can be made to land in the one window the reuse claim has to survive.
    /// </summary>
    private sealed class SweepOnReadArtifactStore(IResultArtifactStore inner, Action onFirstRead)
        : IResultArtifactStore
    {
        private bool _fired;

        public Task<string> WriteAsync(
            QueryJob job, PlanExecutionResult result, CancellationToken cancellationToken = default) =>
            inner.WriteAsync(job, result, cancellationToken);

        public ResultArtifact? Read(string? artifactPath, int? maxRows = null)
        {
            var artifact = inner.Read(artifactPath, maxRows);

            if (artifact != null && !_fired)
            {
                _fired = true;
                onFirstRead();
            }

            return artifact;
        }

        public void Delete(string? artifactPath) => inner.Delete(artifactPath);
        public int SweepOrphans(IReadOnlySet<string> livePaths) => inner.SweepOrphans(livePaths);
        public bool HasRoomForAnotherResult() => inner.HasRoomForAnotherResult();
    }

    /// <summary>
    /// Fails every artifact write the way a transient IO error, a permissions change on the
    /// artifact root, or a volume filling after the admission check would.
    /// </summary>
    private sealed class FailingWriteArtifactStore : IResultArtifactStore
    {
        public Task<string> WriteAsync(
            QueryJob job, PlanExecutionResult result, CancellationToken cancellationToken = default) =>
            throw new IOException("the artifact volume went away mid-write");

        public ResultArtifact? Read(string? artifactPath, int? maxRows = null) => null;
        public void Delete(string? artifactPath) { }
        public int SweepOrphans(IReadOnlySet<string> livePaths) => 0;
        public bool HasRoomForAnotherResult() => true;
    }

    /// <summary>
    /// Translates every turn to the same deterministic plan for a given description and
    /// projection, which is what makes whole-plan equality — and its absence — testable.
    /// </summary>
    private sealed class StubClaude(string description, string projectedAttribute) : IClaudeService
    {
        public int AnswerCalls { get; private set; }

        /// <summary>
        /// Cancels the turn from inside Narrate — the one window where the artifact exists and
        /// the job is not yet Completed (slice2-or-2).
        /// </summary>
        public bool CancelOnNarrate { get; init; }

        public Task<ClaudeResponse> GenerateExecutionPlanAsync(
            string userQuery,
            string? context = null,
            int? requestedResultLimit = null,
            CancellationToken cancellationToken = default,
            string? modelOverride = null)
            => Task.FromResult(new ClaudeResponse
            {
                Success = true,
                ModelUsed = "stub-model",
                Plan = new DirectoryQueryPlan
                {
                    Description = description,
                    Steps = { new DirectoryPlanStep { Step = 1, Name = "s1", Operation = "search" } },
                    Projection = new ProjectionDefinition
                    {
                        RowStep = "s1",
                        Columns =
                        {
                            new ProjectionColumn { Name = projectedAttribute, Attribute = projectedAttribute },
                        },
                    },
                },
            });

        public Task<ClaudeAnswerResponse> GenerateAnswerAsync(
            string reduction, CancellationToken cancellationToken = default, string? modelOverride = null)
        {
            AnswerCalls++;

            if (CancelOnNarrate)
            {
                throw new OperationCanceledException();
            }

            return Task.FromResult(new ClaudeAnswerResponse { Success = true, Answer = "ok" });
        }

        public Task<ClaudeHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ClaudeHealthResult { IsHealthy = true });
    }

    private sealed class PermissiveValidator : IPlanValidator
    {
        public Task<PlanSecurityResult> ValidateSecurityAsync(DirectoryQueryPlan plan)
            => Task.FromResult(new PlanSecurityResult());

        public bool ValidateHmac(DirectoryQueryPlan plan, string signature) => true;

        public bool ValidateComplexity(DirectoryQueryPlan plan) => true;
    }
}
