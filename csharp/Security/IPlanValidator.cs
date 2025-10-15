using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;

namespace AdQuery.Orchestrator.Security;

/// <summary>
/// Service contract for validating directory query plan security.
/// </summary>
public interface IPlanValidator
{
    Task<PlanSecurityResult> ValidateSecurityAsync(DirectoryQueryPlan plan);

    bool ValidateHmac(DirectoryQueryPlan plan, string signature);

    bool ValidateComplexity(DirectoryQueryPlan plan);
}
