using System.Text;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F01 Slice C1 guard: <see cref="QueryJobManager.CreateJobAsync"/> is the single
/// enforcement point. The follow-up byte cap is applied to the stored job's context
/// before persistence (and therefore before any logging or model transmission that
/// reads it), independent of what the client supplied.
/// </summary>
public sealed class QueryJobManagerContextEnforcementTests
{
    private static QueryJobManager CreateManager(int maxBytes, out IQueryJobStore store)
    {
        var configuration = new ConfigurationBuilder().Build();
        store = new InMemoryQueryJobStore();
        var enforcer = new FollowUpContextEnforcer(
            Options.Create(new FollowUpOptions { MaxContextBytes = maxBytes }));

        return new QueryJobManager(
            store,
            new InMemoryQueryJobQueue(),
            NullLogger<QueryJobManager>.Instance,
            new PlanPreprocessor(configuration),
            enforcer,
            configuration);
    }

    [Fact]
    public async Task CreateJobAsync_OverCapContext_StoresBoundedContext()
    {
        var manager = CreateManager(maxBytes: 8, out var store);
        var overCap = new string('x', 100);

        var jobId = await manager.CreateJobAsync(
            "user", "who is jane", overCap, cancellationToken: TestContext.Current.CancellationToken);

        // Over-cap opaque context is dropped whole, not persisted as a fragment.
        var stored = store.GetJob(jobId);
        Assert.NotNull(stored);
        Assert.Null(stored!.Context);
    }

    [Fact]
    public async Task CreateJobAsync_InBoundsContext_PersistsUnchanged()
    {
        var manager = CreateManager(maxBytes: 2000, out var store);
        const string context = "prior: who is in group X";

        var jobId = await manager.CreateJobAsync(
            "user", "and in Dublin?", context, cancellationToken: TestContext.Current.CancellationToken);

        var stored = store.GetJob(jobId);
        Assert.NotNull(stored);
        Assert.Equal(context, stored!.Context);
        Assert.True(Encoding.UTF8.GetByteCount(stored.Context!) <= 2000);
    }

    [Fact]
    public async Task CreateJobAsync_NoContext_StoresNull()
    {
        var manager = CreateManager(maxBytes: 2000, out var store);

        var jobId = await manager.CreateJobAsync(
            "user", "who is jane", context: null, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Null(store.GetJob(jobId)!.Context);
    }
}
