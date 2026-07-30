using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Controllers;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using AdQuery.Orchestrator.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F01 Slice C2 guard: <see cref="QueryController.ExecuteQueryAsync"/> resolves follow-up
/// provenance server-side. A follow-up asserts only a <c>previousJobId</c>; the controller
/// ownership-checks it (rejecting a foreign job) and assembles the bounded last-turn context
/// itself, ignoring any client-supplied context. Last-turn provenance is server-verified,
/// not client-asserted.
/// </summary>
public sealed class QueryControllerFollowUpProvenanceTests
{
    private const string Owner = "ANALOG\\owner";
    private const string OwnerSam = "owner";

    [Fact]
    public async Task ExecuteQueryAsync_ForeignPreviousJobId_IsRejected()
    {
        var manager = new StubJobManager
        {
            JobsById =
            {
                ["foreign"] = new QueryJob { JobId = "foreign", UserName = "someone-else", Status = JobStatus.Completed },
            },
        };
        var controller = CreateController(manager);

        var result = await controller.ExecuteQueryAsync(new QueryRequest
        {
            Query = "and in Dublin?",
            PreviousJobId = "foreign",
        });

        Assert.IsType<ForbidResult>(result);
        Assert.Null(manager.LastCreateContext);
        Assert.False(manager.CreateJobCalled);
    }

    [Fact]
    public async Task ExecuteQueryAsync_OwnCompletedPreviousJob_BuildsServerSideContext()
    {
        var manager = new StubJobManager
        {
            JobsById =
            {
                ["mine"] = new QueryJob
                {
                    JobId = "mine",
                    UserName = OwnerSam,
                    Query = "who is in group X",
                    Status = JobStatus.Completed,
                    // A client-asserted, over-cap "context" on the prior job must not leak.
                    Context = "PRIOR_JOB_CONTEXT_SENTINEL",
                },
            },
        };
        var controller = CreateController(manager);

        var result = await controller.ExecuteQueryAsync(new QueryRequest
        {
            Query = "and in Dublin?",
            PreviousJobId = "mine",
            // A client-supplied context must be ignored entirely.
            Context = "CLIENT_SUPPLIED_CONTEXT_SENTINEL",
        });

        Assert.IsType<AcceptedResult>(result);
        Assert.True(manager.CreateJobCalled);
        Assert.NotNull(manager.LastCreateContext);
        Assert.Contains("who is in group X", manager.LastCreateContext);
        Assert.DoesNotContain("CLIENT_SUPPLIED_CONTEXT_SENTINEL", manager.LastCreateContext);
        Assert.DoesNotContain("PRIOR_JOB_CONTEXT_SENTINEL", manager.LastCreateContext);

        // F04 Slice 6a: the ownership-checked link is recorded so the thread stays walkable
        // from the new job back through its predecessors.
        Assert.Equal("mine", manager.LastCreatePreviousJobId);
    }

    [Fact]
    public async Task ExecuteQueryAsync_ForeignPreviousJobId_RecordsNoThreadLink()
    {
        // The link must be recorded only after the ownership check, or a rejected request
        // could still splice a foreign turn into a thread.
        var manager = new StubJobManager
        {
            JobsById =
            {
                ["foreign"] = new QueryJob { JobId = "foreign", UserName = "someone-else", Status = JobStatus.Completed },
            },
        };
        var controller = CreateController(manager);

        await controller.ExecuteQueryAsync(new QueryRequest
        {
            Query = "and in Dublin?",
            PreviousJobId = "foreign",
        });

        Assert.Null(manager.LastCreatePreviousJobId);
    }

    [Fact]
    public async Task ExecuteQueryAsync_UnknownPreviousJobId_RecordsNoThreadLink()
    {
        var manager = new StubJobManager();
        var controller = CreateController(manager);

        await controller.ExecuteQueryAsync(new QueryRequest
        {
            Query = "and in Dublin?",
            PreviousJobId = "gone",
        });

        Assert.Null(manager.LastCreatePreviousJobId);
    }

    [Fact]
    public async Task ExecuteQueryAsync_NoPreviousJobId_SendsNoContext()
    {
        var manager = new StubJobManager();
        var controller = CreateController(manager);

        var result = await controller.ExecuteQueryAsync(new QueryRequest { Query = "who is jane" });

        Assert.IsType<AcceptedResult>(result);
        Assert.True(manager.CreateJobCalled);
        Assert.Null(manager.LastCreateContext);
    }

    [Fact]
    public async Task ExecuteQueryAsync_UnknownPreviousJobId_ProceedsWithNoContext()
    {
        // An expired/not-found prior job is benign: the follow-up runs without prior-turn
        // context rather than failing.
        var manager = new StubJobManager();
        var controller = CreateController(manager);

        var result = await controller.ExecuteQueryAsync(new QueryRequest
        {
            Query = "and in Dublin?",
            PreviousJobId = "gone",
        });

        Assert.IsType<AcceptedResult>(result);
        Assert.True(manager.CreateJobCalled);
        Assert.Null(manager.LastCreateContext);
    }

    [Fact]
    public async Task RetryWithAlternateModel_KeepsTheRetriedTurnsPlaceInTheThread()
    {
        // slice6-or-1: a retry replaces one logical turn, so the replacement must inherit the
        // original's ancestor. Otherwise the retried turn becomes a thread root and the next
        // follow-up is re-planned with every earlier question gone.
        var store = new InMemoryQueryJobStore();
        store.StoreJob(new QueryJob
        {
            JobId = "turn-1",
            UserName = OwnerSam,
            Query = "everyone under Sanjay",
            Status = JobStatus.Completed,
        });
        store.StoreJob(new QueryJob
        {
            JobId = "turn-2",
            UserName = OwnerSam,
            Query = "only with titles",
            PreviousJobId = "turn-1",
            Status = JobStatus.Completed,
        });

        var manager = new StubJobManager(store);
        var controller = CreateController(manager, store);

        var result = await controller.RetryWithAlternateModel(
            new RetryWithAlternateModelRequest { OriginalJobId = "turn-2" });

        Assert.IsType<OkObjectResult>(result.Result);
        var replacement = Assert.IsType<QueryJob>(manager.LastEnqueuedJob);

        // Not turn-2 itself: that would repeat the retried question in the walk.
        Assert.Equal("turn-1", replacement.PreviousJobId);

        // The thread the next follow-up would carry still reaches the opening question.
        replacement.Status = JobStatus.Completed;
        var context = BuildThreadContext(store, replacement);
        Assert.Contains("everyone under Sanjay", context);
        Assert.Contains("only with titles", context);
    }

    private static string BuildThreadContext(IQueryJobStore store, QueryJob previousJob)
    {
        var options = Options.Create(new FollowUpOptions
        {
            MaxContextBytes = FollowUpOptions.ContextTransportCodeUnitLimit,
            MaxPriorQuestions = FollowUpOptions.MaxThreadQuestions - 1,
        });
        var builder = new FollowUpContextBuilder(
            new FollowUpContextEnforcer(options, NullLogger<FollowUpContextEnforcer>.Instance),
            store,
            options,
            new ConfigurationBuilder().Build());

        return builder.BuildFromPreviousTurn(previousJob) ?? string.Empty;
    }

    private static QueryController CreateController(StubJobManager manager, IQueryJobStore? store = null)
    {
        var configuration = new ConfigurationBuilder().Build();
        var options = Options.Create(new FollowUpOptions { MaxContextBytes = 2000, MaxPriorQuestions = 1 });
        var enforcer = new FollowUpContextEnforcer(options, NullLogger<FollowUpContextEnforcer>.Instance);
        var builder = new FollowUpContextBuilder(
            enforcer, store ?? new InMemoryQueryJobStore(), options, configuration);

        return new QueryController(
            NullLogger<QueryController>.Instance,
            null!,
            null!,
            new MemoryCache(new MemoryCacheOptions()),
            configuration,
            manager,
            builder,
            null!,
            null!,
            new NoResultArtifactStore())
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
    }

    private sealed class StubJobManager : IQueryJobManager
    {
        private readonly IQueryJobStore? _store;

        public StubJobManager(IQueryJobStore? store = null) => _store = store;

        public Dictionary<string, QueryJob> JobsById { get; } = new();
        public string? LastCreateContext { get; private set; }
        public string? LastCreatePreviousJobId { get; private set; }
        public bool CreateJobCalled { get; private set; }
        public QueryJob? LastEnqueuedJob { get; private set; }

        public Task<string> CreateJobAsync(
            string userName,
            string query,
            string? context = null,
            int? requestedResultLimit = null,
            string? previousJobId = null,
            CancellationToken cancellationToken = default)
        {
            CreateJobCalled = true;
            LastCreateContext = context;
            LastCreatePreviousJobId = previousJobId;
            return Task.FromResult("new-job");
        }

        public QueryJob? GetJob(string jobId) =>
            JobsById.TryGetValue(jobId, out var job) ? job : _store?.GetJob(jobId);

        public Task EnqueueJobAsync(QueryJob job, string? forceModel = null)
        {
            LastEnqueuedJob = job;
            _store?.StoreJob(job);
            return Task.CompletedTask;
        }
        public void CancelJob(string jobId) { }
        public List<QueryJob> GetUserJobs(string userName) => new();
        public List<QueryJob> GetQueuedJobs() => new();
        public void CleanupCompletedJobs(System.TimeSpan olderThan) { }
        public Task ExecuteJobWithServicesAsync(
            string jobId,
            IClaudeService claude,
            IPlanValidator validator,
            IDirectoryPlanExecutor executor,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
