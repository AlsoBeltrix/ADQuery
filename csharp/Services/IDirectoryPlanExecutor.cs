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

    public List<string> Errors { get; set; } = new();

    public List<string> Warnings { get; set; } = new();

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
