using System.Collections.Generic;
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
/// Guards finding slice4-or-1: the F04 Slice 4 export rule is server policy, not a UI
/// decoration. Withholding the download pills tells the user an answer has no exportable
/// artifact; this asserts the server means it — a direct request for a single-record answer is
/// refused, and the status DTO does not advertise a URL the server would reject.
///
/// The rule the two sides share is F05-D1's: export turns on how many <em>records the result
/// holds</em>, never on how many lines the answer occupies. A pure count over many records is
/// therefore exportable here, exactly as it is in <c>ExportAffordanceTests</c>.
///
/// Drives the real <see cref="QueryController"/> action. A non-exportable job is refused
/// before any filesystem work, so no <c>E:\WWWOutput</c> path is touched and the test is
/// portable to a build agent.
/// </summary>
public sealed class ExportPolicyIsServerEnforcedTests
{
    private const string Owner = "ANALOG\\owner";
    private const string OwnerSam = "owner";

    private static DirectoryQueryPlan SearchPlan() => new()
    {
        Steps = { new DirectoryPlanStep { Name = "s1", Operation = "search" } },
        Projection = new ProjectionDefinition { RowStep = "s1" },
    };

    private static DirectoryQueryPlan PureCountPlan() => new()
    {
        Steps = { new DirectoryPlanStep { Name = "s1", Operation = "search" } },
        Projection = new ProjectionDefinition
        {
            RowStep = "s1",
            Aggregation = new AggregationDefinition { Count = true },
        },
    };

    [Fact]
    public void DownloadAsync_PureCountOverManyRecords_PassesThePolicyGate()
    {
        // F05-D1: "How many managers in Thailand?" answers with one number over a many-record
        // result, and those records are what the user reaches for next. The gate must let it
        // through. Execution stops at the artifact lookup (this job's results are absent),
        // which is the next check after the gate and well before any file is written.
        var (controller, _) = CreateController(PureCountPlan(), totalRows: 27000);

        var result = controller.DownloadAsync("job-1");

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("expired", notFound.Value?.ToString(), System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DownloadAsync_PureCountOverASingleRecord_IsRefused()
    {
        // The overshoot guard: one record is one record whether or not the plan counted it.
        var (controller, _) = CreateController(PureCountPlan(), totalRows: 1);

        var result = controller.DownloadAsync("job-1");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void DownloadAsync_SingleRecordAnswer_IsRefused()
    {
        var (controller, _) = CreateController(SearchPlan(), totalRows: 1);

        var result = controller.DownloadAsync("job-1");

        Assert.IsType<BadRequestObjectResult>(result);
    }

    [Fact]
    public void DownloadAsync_ExportableAnswer_PassesThePolicyGate()
    {
        // A multi-row list IS the artifact. The policy gate must let it through — proving the
        // refusals above are the rule firing, not the endpoint being broken for everything.
        // Execution stops at the artifact lookup (this job's results are absent), which is the
        // next check after the gate and well before any file is written.
        var (controller, _) = CreateController(SearchPlan(), totalRows: 42);

        var result = controller.DownloadAsync("job-1");

        var notFound = Assert.IsType<NotFoundObjectResult>(result);
        Assert.Contains("expired", notFound.Value?.ToString(), System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetJobStatus_NonExportableAnswer_AdvertisesNoDownloadUrl()
    {
        // The DTO must not offer a URL the server would refuse; the two read one policy.
        // A single-record result is the non-exportable case (F05-D1): the answer on screen is
        // the whole result. Before F05 this used a 27,000-row pure count, which the same
        // policy now correctly treats as exportable.
        var (controller, _) = CreateController(PureCountPlan(), totalRows: 1);

        var payload = CompletedResultOf(controller.GetJobStatus("job-1"));

        Assert.False(GetProperty<bool>(payload, "exportable"));
        Assert.Null(GetProperty<string>(payload, "downloadUrl"));
    }

    [Fact]
    public void GetJobStatus_ExportableAnswer_AdvertisesTheDownloadUrl()
    {
        var (controller, _) = CreateController(SearchPlan(), totalRows: 42);

        var payload = CompletedResultOf(controller.GetJobStatus("job-1"));

        Assert.True(GetProperty<bool>(payload, "exportable"));
        Assert.Equal("/api/query/download-async/job-1", GetProperty<string>(payload, "downloadUrl"));
    }

    private static object CompletedResultOf(IActionResult actionResult)
    {
        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var result = GetProperty<object>(ok.Value!, "result");
        return Assert.IsType<object>(result, exactMatch: false);
    }

    private static T? GetProperty<T>(object source, string name)
    {
        var property = source.GetType().GetProperty(name);
        Assert.NotNull(property);
        return (T?)property!.GetValue(source);
    }

    private static (QueryController Controller, StubJobManager Manager) CreateController(
        DirectoryQueryPlan plan, int totalRows)
    {
        var manager = new StubJobManager
        {
            JobsById =
            {
                ["job-1"] = new QueryJob
                {
                    JobId = "job-1",
                    UserName = OwnerSam,
                    Query = "q",
                    Status = JobStatus.Completed,
                    Plan = plan,
                    TotalRows = totalRows,
                    // No ResultArtifactPath: an exportable job therefore stops at the artifact
                    // lookup, which is the first thing after the policy gate.
                },
            },
        };

        var controller = new QueryController(
            NullLogger<QueryController>.Instance,
            null!,
            null!,
            new MemoryCache(new MemoryCacheOptions()),
            new ConfigurationBuilder().Build(),
            manager,
            null!,
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

        return (controller, manager);
    }

    private sealed class StubJobManager : IQueryJobManager
    {
        public Dictionary<string, QueryJob> JobsById { get; } = new();

        public QueryJob? GetJob(string jobId) => JobsById.TryGetValue(jobId, out var job) ? job : null;

        public Task<string> CreateJobAsync(
            string userName,
            string query,
            string? context = null,
            int? requestedResultLimit = null,
            string? previousJobId = null,
            CancellationToken cancellationToken = default) => Task.FromResult("new-job");

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
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
