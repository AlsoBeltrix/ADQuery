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

    private (QueryJobManager Manager, IQueryJobStore Store, IResultArtifactStore Artifacts) CreateManager()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Results:ArtifactRoot"] = _root })
            .Build();

        var store = new InMemoryQueryJobStore();
        var artifacts = new JsonLinesResultArtifactStore(
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
    /// Translates every turn to the same deterministic plan for a given description and
    /// projection, which is what makes whole-plan equality — and its absence — testable.
    /// </summary>
    private sealed class StubClaude(string description, string projectedAttribute) : IClaudeService
    {
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
            => Task.FromResult(new ClaudeAnswerResponse { Success = true, Answer = "ok" });

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
