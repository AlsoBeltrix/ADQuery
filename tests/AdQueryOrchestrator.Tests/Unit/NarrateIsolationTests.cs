using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F04 Slice 2 guard (F04-D1). Narrate is additive and never a new failure mode: a job
/// whose second model call fails, throws, or returns nothing still completes with its
/// headline, rows, and aggregation intact, and simply carries no answer. A job whose
/// Narrate succeeds carries the model's text and nothing else — never the raw response.
///
/// Also asserts what reaches the model: the stubbed service records its reduction, which
/// must contain the distribution scalars and must not contain any result row value.
/// </summary>
public sealed class NarrateIsolationTests
{
    private const string RowOnlySentinel = "ROW_ONLY_SENTINEL";

    [Fact]
    public async Task NarrateSucceeds_JobCompletesWithTheModelAnswer()
    {
        var claude = new StubClaude { AnswerText = "There are 3 contractors in Dublin." };

        var job = await RunJobAsync(claude);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Equal("There are 3 contractors in Dublin.", job.Answer);
        Assert.Equal(3, job.TotalRows);
        Assert.NotNull(job.Aggregation);
    }

    [Fact]
    public async Task NarrateFails_JobStillCompletesWithHeadlineAndRows_AnswerAbsent()
    {
        var claude = new StubClaude { AnswerSucceeds = false };

        var job = await RunJobAsync(claude);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Null(job.Answer);
        Assert.Equal(3, job.TotalRows);
        Assert.NotNull(job.Aggregation);
        Assert.True(
            ((IDictionary<string, int>)job.Aggregation!["grouped_counts"]).Count > 0,
            "the grouped distribution survives a Narrate failure");
    }

    [Fact]
    public async Task NarrateThrows_JobStillCompletes_AnswerAbsent()
    {
        var claude = new StubClaude { AnswerThrows = true };

        var job = await RunJobAsync(claude);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Null(job.Answer);
        Assert.Equal(3, job.TotalRows);
    }

    [Fact]
    public async Task NarrateReturnsBlankText_IsTreatedAsNoAnswer()
    {
        var claude = new StubClaude { AnswerText = "   " };

        var job = await RunJobAsync(claude);

        Assert.Equal(JobStatus.Completed, job.Status);
        Assert.Null(job.Answer);
    }

    [Fact]
    public async Task TheReductionSentToTheModel_CarriesScalarsAndNoRowValues()
    {
        var claude = new StubClaude { AnswerText = "ok" };

        await RunJobAsync(claude);

        Assert.NotNull(claude.LastReduction);
        Assert.Contains("QUESTION: who are the contractors?", claude.LastReduction);
        Assert.Contains("DISTRIBUTION:", claude.LastReduction);
        Assert.Contains("3 rows", claude.LastReduction);
        Assert.Contains("distinct values", claude.LastReduction);

        // displayName is projected into every row but is not a group_by field, so no row
        // value may appear in the reduction.
        Assert.DoesNotContain(RowOnlySentinel, claude.LastReduction);
    }

    private static async Task<QueryJob> RunJobAsync(StubClaude claude)
    {
        var configuration = new ConfigurationBuilder().Build();
        var store = new InMemoryQueryJobStore();
        var manager = new QueryJobManager(
            store,
            new InMemoryQueryJobQueue(),
            NullLogger<QueryJobManager>.Instance,
            new PlanPreprocessor(configuration),
            new FollowUpContextEnforcer(
                Options.Create(new FollowUpOptions()),
                NullLogger<FollowUpContextEnforcer>.Instance),
            new AnswerReductionBuilder(Options.Create(new AnswerOptions())),
            new NoResultArtifactStore(),
            configuration);

        var jobId = await manager.CreateJobAsync(
            "narrate-user",
            "who are the contractors?",
            cancellationToken: TestContext.Current.CancellationToken);

        await manager.ExecuteJobWithServicesAsync(
            jobId,
            claude,
            new PermissiveValidator(),
            new StubExecutor(),
            TestContext.Current.CancellationToken);

        return store.GetJob(jobId)!;
    }

    private static DirectoryQueryPlan BuildPlan() => new()
    {
        Description = "Count contractors by employeeType",
        Steps = { new DirectoryPlanStep { Step = 1, Name = "s1", Operation = "search" } },
        Projection = new ProjectionDefinition
        {
            RowStep = "s1",
            Columns = { new ProjectionColumn { Name = "Name", Attribute = "displayName" } },
            Aggregation = new AggregationDefinition { Count = true, GroupBy = { "employeeType" } },
        },
    };

    private sealed class StubClaude : IClaudeService
    {
        public string? AnswerText { get; init; }
        public bool AnswerSucceeds { get; init; } = true;
        public bool AnswerThrows { get; init; }
        public string? LastReduction { get; private set; }

        public Task<ClaudeResponse> GenerateExecutionPlanAsync(
            string userQuery,
            string? context = null,
            int? requestedResultLimit = null,
            CancellationToken cancellationToken = default,
            string? modelOverride = null)
            => Task.FromResult(new ClaudeResponse
            {
                Success = true,
                Plan = BuildPlan(),
                ModelUsed = "stub-model",
            });

        public Task<ClaudeAnswerResponse> GenerateAnswerAsync(
            string reduction,
            CancellationToken cancellationToken = default,
            string? modelOverride = null)
        {
            LastReduction = reduction;

            if (AnswerThrows)
            {
                throw new InvalidOperationException("narrate exploded");
            }

            return Task.FromResult(new ClaudeAnswerResponse
            {
                Success = AnswerSucceeds,
                Answer = AnswerSucceeds ? AnswerText : null,
                ErrorMessage = AnswerSucceeds ? null : "provider unavailable",
            });
        }

        public Task<ClaudeHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new ClaudeHealthResult { IsHealthy = true });
    }

    private sealed class StubExecutor : IDirectoryPlanExecutor
    {
        public Task<PlanExecutionResult> ExecutePlanAsync(
            DirectoryQueryPlan plan, CancellationToken cancellationToken = default)
            => ExecutePlanAsync(plan, new Progress<PlanProgressUpdate>(), cancellationToken);

        public Task<PlanExecutionResult> ExecutePlanAsync(
            DirectoryQueryPlan plan, IProgress<PlanProgressUpdate> progress, CancellationToken cancellationToken)
        {
            string?[] types = ["CWK", "CWK", "FTE"];

            return Task.FromResult(new PlanExecutionResult
            {
                Success = true,
                Data = types
                    .Select((_, i) => new Dictionary<string, object?>
                    {
                        ["Name"] = $"{RowOnlySentinel}-{i}",
                    })
                    .ToList(),
                GroupValues = types
                    .Select(IReadOnlyList<string?> (type) => new[] { type })
                    .ToList(),
            });
        }

        public Task<PlanValidationResult> ValidatePlanAsync(
            DirectoryQueryPlan plan, CancellationToken cancellationToken = default)
            => Task.FromResult(new PlanValidationResult { IsValid = true });
    }

    private sealed class PermissiveValidator : IPlanValidator
    {
        public Task<PlanSecurityResult> ValidateSecurityAsync(DirectoryQueryPlan plan)
            => Task.FromResult(new PlanSecurityResult());

        public bool ValidateHmac(DirectoryQueryPlan plan, string signature) => true;

        public bool ValidateComplexity(DirectoryQueryPlan plan) => true;
    }
}
