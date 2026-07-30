using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Executes structured directory query plans.
/// </summary>
public interface IDirectoryPlanExecutor
{
    Task<PlanExecutionResult> ExecutePlanAsync(DirectoryQueryPlan plan, CancellationToken cancellationToken = default);

    Task<PlanExecutionResult> ExecutePlanAsync(DirectoryQueryPlan plan, IProgress<PlanProgressUpdate> progress, CancellationToken cancellationToken);

    Task<PlanValidationResult> ValidatePlanAsync(DirectoryQueryPlan plan, CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of executing a directory plan.
/// </summary>
public class PlanExecutionResult
{
    public bool Success { get; set; }

    public List<Dictionary<string, object?>> Data { get; set; } = new();

    /// <summary>
    /// Per-row <c>group_by</c> values, read from the row-step directory record rather
    /// than the display projection (slice1r2-or-1), one entry per <see cref="Data"/> row
    /// in <c>group_by</c> order. Empty when the plan requests no grouping. Grouping must
    /// not depend on which columns the plan happens to display.
    /// </summary>
    public List<IReadOnlyList<string?>> GroupValues { get; set; } = new();

    public List<string> Errors { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// True when the traversal stopped short of every matching record because it hit a
    /// safety limit (ci-or-1), so <see cref="Data"/> is a subset of what the plan asked for
    /// and any count derived from it is a floor rather than a total.
    /// <para>
    /// Set by the executor at the points where it actually truncates, not inferred by
    /// parsing <see cref="Warnings"/> text back out: the warnings are free-text operator
    /// diagnostics, while this is a fact about the answer that Narrate and the chat surface
    /// both have to act on. False by default, so a result is complete unless something
    /// deliberately says otherwise.
    /// </para>
    /// <para>
    /// A limit the <em>user</em> asked for is not incompleteness: "the first ten
    /// contractors" is completely answered by ten rows. Only system-imposed stops set this —
    /// see <see cref="DirectoryQueryPlan.ResultLimitIsSystemImposed"/>.
    /// </para>
    /// </summary>
    public bool ResultIsIncomplete { get; set; }

    public long ExecutionTimeMs { get; set; }

    public int StepsExecuted { get; set; }

    public int StepsSkipped { get; set; }
}

/// <summary>
/// Validation output for a directory plan.
/// </summary>
public class PlanValidationResult
{
    public bool IsValid { get; set; }

    public List<string> Errors { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

    public PlanSecurityResult Security { get; set; } = new();
}

/// <summary>
/// Security evaluation details for a plan.
/// </summary>
public class PlanSecurityResult
{
    public bool OperationsValid { get; set; } = true;

    public bool HmacValid { get; set; } = true;

    public bool ComplexityValid { get; set; } = true;

    public List<string> SecurityErrors { get; set; } = new();

    public List<string> BlockedOperations { get; set; } = new();
}
