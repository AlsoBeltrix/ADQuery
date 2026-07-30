using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AdQuery.Orchestrator.Controllers;
using AdQuery.Orchestrator.Services;
using Xunit;

using static AdQuery.Orchestrator.Tests.Unit.AssemblyCallGraph;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F04 Slice 4 invariant lock: <see cref="QueryController.DownloadAsync"/> serializes the
/// settled result artifact and nothing else. It must never reach a model or re-execute a
/// plan — the owner's binding constraint is that exporting can never risk producing a
/// different result than the answer the user already read.
///
/// The guard walks the whole call graph reachable from <c>DownloadAsync</c> through the
/// application assembly and asserts that no method in it calls <see cref="IClaudeService"/>
/// or <see cref="IDirectoryPlanExecutor"/>, and that no field holding either service is even
/// loaded. Virtual and interface calls descend into every application-assembly implementation,
/// so routing export through an injected service does not hide what that service calls
/// (slice4-or-2). A "fail the test if the model is invoked" stub cannot be used here:
/// <c>DownloadAsync</c> writes its audit copy under <see cref="QueryLogHelper.OutputRoot"/>
/// (a hard-coded <c>E:\</c> path), which does not exist on a build agent, so the method cannot
/// be driven end to end portably. Reading the call graph proves the stronger claim anyway —
/// not merely that this input made no model call, but that no input can.
///
/// The companion claim, that the bytes come from the settled artifact, is guarded separately by
/// <c>ExportSerializesTheSettledArtifactTests</c>, which drives the real serializer.
/// </summary>
public sealed class ExportIsModelFreeTests
{
    private static readonly HashSet<Type> ForbiddenTypes =
    [
        typeof(IClaudeService),
        typeof(IDirectoryPlanExecutor),
    ];

    /// <summary>
    /// Every field in the application assembly that holds a forbidden service, discovered by
    /// type rather than named literally (slice4-or-2): a renamed field, or a new one on a
    /// service extracted out of the controller, is caught without editing this test.
    /// </summary>
    private static readonly HashSet<FieldInfo> ForbiddenFields = AppAssembly
        .GetTypes()
        .SelectMany(t => t.GetFields(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
        .Where(f => ForbiddenTypes.Any(forbidden => forbidden.IsAssignableFrom(f.FieldType)))
        .ToHashSet();

    [Fact]
    public void DownloadAsync_CallGraph_NeverReachesTheModelOrThePlanExecutor()
    {
        var offenders = new List<string>();

        foreach (var method in ReachableMethods(DownloadAsyncMethod()))
        {
            foreach (var callee in CalledMembers(method))
            {
                var declaring = callee.DeclaringType;
                if (declaring == null)
                {
                    continue;
                }

                var forbidden = ForbiddenTypes.Any(t =>
                    t == declaring || (t.IsAssignableFrom(declaring) && declaring != typeof(object)));
                if (forbidden)
                {
                    offenders.Add($"{method.DeclaringType?.Name}.{method.Name} → {declaring.Name}.{callee.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Export must serialize the settled artifact, never re-derive it. Model/executor calls "
            + "reachable from DownloadAsync: " + string.Join("; ", offenders));
    }

    [Fact]
    public void DownloadAsync_CallGraph_NeverLoadsTheModelOrExecutorFields()
    {
        // Belt and braces for the call-graph assertion above: a model call routed through a
        // local, a delegate, or a helper that receives the service as an argument would still
        // have to read one of these fields somewhere in the graph.
        var offenders = new List<string>();

        foreach (var method in ReachableMethods(DownloadAsyncMethod()))
        {
            foreach (var field in LoadedFields(method))
            {
                if (ForbiddenFields.Contains(field))
                {
                    offenders.Add(
                        $"{method.DeclaringType?.Name}.{method.Name} → "
                        + $"{field.DeclaringType?.Name}.{field.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "DownloadAsync must not read the model or plan-executor services. Loads: "
            + string.Join("; ", offenders));
    }

    [Fact]
    public void TheGuardWalksARealCallGraph()
    {
        // Over-removal sentinel: if the walker silently resolved nothing, the two assertions
        // above would pass vacuously. DownloadAsync demonstrably reaches the serializer.
        var reachable = ReachableMethods(DownloadAsyncMethod())
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("QueryController.GenerateFileContent", reachable);
        Assert.Contains("QueryController.BuildGroupedDistributionExport", reachable);
        Assert.Contains("QueryLogHelper.GetUserDirectory", reachable);
        Assert.True(reachable.Count > 10, $"walked only {reachable.Count} methods");
    }

    private static MethodInfo DownloadAsyncMethod() =>
        typeof(QueryController).GetMethod(nameof(QueryController.DownloadAsync))
        ?? throw new InvalidOperationException("QueryController.DownloadAsync was not found.");
}
