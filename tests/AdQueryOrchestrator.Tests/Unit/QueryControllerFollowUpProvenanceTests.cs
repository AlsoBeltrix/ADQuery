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

    private static QueryController CreateController(StubJobManager manager)
    {
        var configuration = new ConfigurationBuilder().Build();
        var enforcer = new FollowUpContextEnforcer(
            Options.Create(new FollowUpOptions { MaxContextBytes = 2000 }),
            NullLogger<FollowUpContextEnforcer>.Instance);
        var builder = new FollowUpContextBuilder(enforcer, configuration);

        return new QueryController(
            NullLogger<QueryController>.Instance,
            null!,
            null!,
            new MemoryCache(new MemoryCacheOptions()),
            configuration,
            manager,
            builder,
            null!,
            null!)
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
        public Dictionary<string, QueryJob> JobsById { get; } = new();
        public string? LastCreateContext { get; private set; }
        public bool CreateJobCalled { get; private set; }

        public Task<string> CreateJobAsync(
            string userName,
            string query,
            string? context = null,
            int? requestedResultLimit = null,
            CancellationToken cancellationToken = default)
        {
            CreateJobCalled = true;
            LastCreateContext = context;
            return Task.FromResult("new-job");
        }

        public QueryJob? GetJob(string jobId) => JobsById.TryGetValue(jobId, out var job) ? job : null;

        public Task EnqueueJobAsync(QueryJob job, string? forceModel = null) => Task.CompletedTask;
        public void CancelJob(string jobId) { }
        public List<QueryJob> GetUserJobs(string userName) => new();
        public List<QueryJob> GetQueuedJobs() => new();
        public void CleanupCompletedJobs(System.TimeSpan olderThan) { }
        public Task ExecuteJobWithServicesAsync(
            string jobId,
            IClaudeService claude,
            IPlanValidator validator,
            IDirectoryPlanExecutor executor,
            IMemoryCache cache,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
