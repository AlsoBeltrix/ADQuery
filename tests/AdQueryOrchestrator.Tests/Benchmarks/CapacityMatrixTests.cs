using Xunit;

namespace AdQuery.Orchestrator.Tests.Benchmarks;

/// <summary>
/// Env-gated entry point for the P05 Slice 0 evidence matrix. It is discoverable by the
/// unfiltered verification run but calls <see cref="Assert.Skip"/> when
/// <c>ADQUERY_CAPACITY_MATRIX</c> is unset, so it never executes in CI — a skipped test
/// does not count toward the run's executed-test gate. When the gate is set, it runs the
/// full matrix and writes results to the ignored <c>artifacts/</c> tree.
/// </summary>
public sealed class CapacityMatrixTests
{
    [Fact]
    public void CapacityMatrix_ProducesEvidenceArtifact()
    {
        if (!CapacityMatrixRunner.IsEnabled)
        {
            Assert.Skip(
                $"Capacity matrix is opt-in; set {CapacityMatrixRunner.GateVariable} to run it. " +
                "It is intentionally inert during normal verification.");
        }

        var artifactsRoot = ResolveArtifactsRoot();
        var artifactPath = CapacityMatrixRunner.Run(artifactsRoot);

        Assert.True(File.Exists(artifactPath), $"Expected matrix artifact at {artifactPath}.");
        Assert.True(new FileInfo(artifactPath).Length > 0, "Matrix artifact is empty.");
    }

    private static string ResolveArtifactsRoot()
    {
        // Walk up from the test binary to the repository root (which holds the ignored
        // artifacts/ directory alongside ADQuery.sln).
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ADQuery.sln")))
        {
            dir = dir.Parent;
        }

        var root = dir?.FullName ?? Directory.GetCurrentDirectory();
        return Path.Combine(root, "artifacts");
    }
}
