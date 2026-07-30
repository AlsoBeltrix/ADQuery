using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// An <see cref="IResultArtifactStore"/> that never has a result (F04 Slice 7). Lets a
/// controller test drive an endpoint past the artifact lookup without touching a real
/// output volume — the reader then takes the same "results expired or not available"
/// branch it takes for a job whose retention has passed.
/// </summary>
internal sealed class NoResultArtifactStore : IResultArtifactStore
{
    public Task<string> WriteAsync(
        QueryJob job,
        PlanExecutionResult result,
        CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);

    public ResultArtifact? Read(string? artifactPath, int? maxRows = null) => null;

    public void Delete(string? artifactPath) { }

    public int SweepOrphans(IReadOnlySet<string> livePaths) => 0;

    public bool HasRoomForAnotherResult() => true;
}
