using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// ci-or-1 guard. A traversal stopped by a system limit must not narrate as a complete
/// answer. The executor records the stop as an explicit fact rather than only as free-text
/// warnings, that fact rides into the bounded reduction, and it survives whole-plan artifact
/// reuse — so the count the user reads is labelled a floor wherever it is read.
///
/// The user's own limit is deliberately not incompleteness: "the first ten contractors" is
/// completely answered by ten rows, and caveating that would make the caveat noise.
/// </summary>
public sealed class IncompleteResultsAreNarratedAsFloorsTests
{
    private const int Ceiling = 3;

    [Fact]
    public async Task ASystemCappedResult_IsMarkedIncomplete()
    {
        // The configured QueryDefaults:MaxResults ceiling, pushed onto a plan the user gave
        // no count for. The set is cut down to it, so the total is unknowable.
        var plan = SearchPlan();
        Preprocessor().EnsurePlanLimit(plan, Ceiling);

        var result = await ExecuteAsync(plan, Records(Ceiling + 4));

        Assert.True(plan.ResultLimitIsSystemImposed);
        Assert.Equal(Ceiling, result.Data.Count);
        Assert.True(result.ResultIsIncomplete);
    }

    [Fact]
    public async Task AUserRequestedLimit_IsNotIncompleteness()
    {
        // The model sets result_limit only when the user named a count. Ten rows completely
        // answer "the first ten": nothing was withheld, so nothing is caveated.
        var plan = SearchPlan();
        plan.ResultLimit = 2;
        Preprocessor().EnsurePlanLimit(plan, Ceiling);

        var result = await ExecuteAsync(plan, Records(Ceiling + 4));

        Assert.False(plan.ResultLimitIsSystemImposed);
        Assert.Equal(2, result.Data.Count);
        Assert.False(result.ResultIsIncomplete);
    }

    [Fact]
    public async Task AResultThatFitsUnderTheCeiling_IsComplete()
    {
        var plan = SearchPlan();
        Preprocessor().EnsurePlanLimit(plan, Ceiling);

        var result = await ExecuteAsync(plan, Records(Ceiling - 1));

        Assert.False(result.ResultIsIncomplete);
    }

    [Fact]
    public async Task AResultSittingExactlyOnTheCeiling_IsIncomplete()
    {
        // EnsurePlanLimit pushes the ceiling onto the row step's size_limit too, so the
        // directory can cut the set before the executor's own truncation ever runs. The row
        // count then lands on the limit with nothing truncated here — and the total is just
        // as unknowable as it is one row over.
        var plan = SearchPlan();
        Preprocessor().EnsurePlanLimit(plan, Ceiling);

        var result = await ExecuteAsync(plan, Records(Ceiling));

        Assert.Equal(Ceiling, result.Data.Count);
        Assert.True(result.ResultIsIncomplete);
    }

    [Fact]
    public void AnIncompleteResult_TellsNarrateTheFiguresAreFloors()
    {
        var plan = SearchPlan();
        var reduction = Builder().Build(
            "how many people report up through Sanjay?",
            plan,
            new HeadlineResult { Kind = HeadlineKind.Count, Count = 4000 },
            distribution: null,
            resultIsIncomplete: true);

        Assert.NotNull(reduction);
        Assert.Contains("COMPLETENESS: partial", reduction);
        Assert.Contains("floor", reduction);

        // Before the figure it qualifies, so the model reads it first.
        var lines = reduction!.Split('\n');
        Assert.True(
            lines.ToList().FindIndex(l => l.StartsWith("COMPLETENESS: ", System.StringComparison.Ordinal))
                < lines.ToList().FindIndex(l => l.StartsWith("RESULT: ", System.StringComparison.Ordinal)),
            "the completeness line must precede the headline it qualifies");
    }

    [Fact]
    public void ACompleteResult_CarriesNoCompletenessLine()
    {
        var reduction = Builder().Build(
            "how many contractors are in Bangalore?",
            SearchPlan(),
            new HeadlineResult { Kind = HeadlineKind.Count, Count = 412 },
            distribution: null,
            resultIsIncomplete: false);

        Assert.NotNull(reduction);
        Assert.DoesNotContain("COMPLETENESS", reduction);
    }

    [Fact]
    public void TheCompletenessLine_StaysWithinItsDeclaredBound()
    {
        // It is a fixed server-written string, which is what lets the reduction ceiling stay
        // derivable. The executor's warnings are free-text and unbounded in number and are
        // deliberately not what the reduction carries.
        Assert.True(
            AnswerReductionBuilder.IncompleteLine.Length <= AnswerOptions.MaxCompletenessChars,
            $"{AnswerReductionBuilder.IncompleteLine.Length} chars exceeded the declared bound "
            + $"of {AnswerOptions.MaxCompletenessChars}");
    }

    [Fact]
    public void TheCompletenessLine_IsNeverDroppedToFitTheCap()
    {
        // Dropping it while keeping the headline is exactly the defect: a floor stated as a
        // total. A cap that cannot hold both must yield no reduction rather than a confident
        // one.
        var plan = SearchPlan();
        var headline = new HeadlineResult { Kind = HeadlineKind.Count, Count = 4000 };
        const string Asked = "how many people report up through Sanjay?";

        var full = Builder().Build(Asked, plan, headline, distribution: null, resultIsIncomplete: true)!;
        Assert.Contains("COMPLETENESS", full);

        var tight = Builder(System.Text.Encoding.UTF8.GetByteCount(full) - 1)
            .Build(Asked, plan, headline, distribution: null, resultIsIncomplete: true);

        Assert.True(
            tight is null || tight.Contains("COMPLETENESS", System.StringComparison.Ordinal),
            "a reduction that kept the figures must have kept the line saying they are floors");
    }

    private static AnswerReductionBuilder Builder(int? maxBytes = null)
        => new(Options.Create(maxBytes is null
            ? new AnswerOptions()
            : new AnswerOptions { MaxReductionBytes = maxBytes.Value }));

    private static PlanPreprocessor Preprocessor()
        => new(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static DirectoryQueryPlan SearchPlan() => new()
    {
        Description = "Everyone reporting up through Sanjay",
        Steps = { new DirectoryPlanStep { Step = 1, Name = "s1", Operation = "search" } },
        Projection = new ProjectionDefinition
        {
            RowStep = "s1",
            Columns = { new ProjectionColumn { Name = "Name", Attribute = "displayName" } },
        },
    };

    private static DirectoryRecord[] Records(int count) => Enumerable
        .Range(0, count)
        .Select(i =>
        {
            var record = new DirectoryRecord { DistinguishedName = $"CN=p{i},DC=x" };
            record["displayName"] = $"Person {i}";
            return record;
        })
        .ToArray();

    private static async Task<PlanExecutionResult> ExecuteAsync(
        DirectoryQueryPlan plan,
        params DirectoryRecord[] records)
    {
        var executor = new DirectoryPlanExecutor(
            NullLogger<DirectoryPlanExecutor>.Instance,
            new PermissiveValidator(),
            new FixedDirectoryService(records));

        return await executor.ExecutePlanAsync(plan, CancellationToken.None);
    }

    private sealed class FixedDirectoryService : IActiveDirectoryService
    {
        private readonly IReadOnlyList<DirectoryRecord> _records;

        public FixedDirectoryService(IReadOnlyList<DirectoryRecord> records) => _records = records;

        /// <summary>
        /// Honours <c>size_limit</c> the way the directory does, so a set cut server-side
        /// before the executor's own truncation is the case this fixture reproduces.
        /// </summary>
        public Task<IReadOnlyList<DirectoryRecord>> SearchAsync(
            DirectorySearchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DirectoryRecord>>(
                request.SizeLimit is > 0 ? _records.Take(request.SizeLimit.Value).ToList() : _records);

        public Task<IReadOnlyList<DirectoryRecord>> ExpandGroupMembersAsync(
            IEnumerable<string> groupDistinguishedNames, bool recursive, IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DirectoryRecord>>([]);

        public Task<IReadOnlyList<DirectoryRecord>> LookupAsync(
            IEnumerable<string> distinguishedNames, DirectoryObjectType targetType, IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DirectoryRecord>>([]);

        public Task<IReadOnlyList<DirectoryRecord>> GetDirectReportsBatch(
            IEnumerable<string> managerDistinguishedNames, IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DirectoryRecord>>([]);
    }

    private sealed class PermissiveValidator : IPlanValidator
    {
        public Task<PlanSecurityResult> ValidateSecurityAsync(DirectoryQueryPlan plan)
            => Task.FromResult(new PlanSecurityResult());

        public bool ValidateHmac(DirectoryQueryPlan plan, string signature) => true;

        public bool ValidateComplexity(DirectoryQueryPlan plan) => true;
    }
}
