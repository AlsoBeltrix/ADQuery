using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Health check for the execution plan orchestrator system
/// </summary>
public class OrchestratorHealthCheck : IHealthCheck
{
    private readonly IDirectoryPlanExecutor _planExecutor;
    private readonly ILogger<OrchestratorHealthCheck> _logger;

    public OrchestratorHealthCheck(IDirectoryPlanExecutor planExecutor, ILogger<OrchestratorHealthCheck> logger)
    {
        _planExecutor = planExecutor;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var testPlan = new Models.DirectoryQueryPlan
            {
                Description = "Health check test plan",
                Steps = new List<Models.DirectoryPlanStep>
                {
                    new Models.DirectoryPlanStep
                    {
                        Step = 1,
                        Name = "probe_users",
                        Operation = "search",
                        TargetType = Models.DirectoryObjectType.User,
                        Attributes = new List<string> { "displayName" },
                        Filters = new List<Models.DirectoryFilter>
                        {
                            new Models.DirectoryFilter
                            {
                                Attribute = "displayName",
                                Operator = "contains",
                                Value = "test"
                            }
                        }
                    }
                },
                Projection = new Models.ProjectionDefinition
                {
                    RowStep = "probe_users",
                    Columns = new List<Models.ProjectionColumn>
                    {
                        new Models.ProjectionColumn
                        {
                            Name = "DisplayName",
                            Attribute = "displayName"
                        }
                    }
                }
            };

            var validationResult = await _planExecutor.ValidatePlanAsync(testPlan, cancellationToken);

            if (validationResult.IsValid)
            {
                return HealthCheckResult.Healthy("Directory plan validation succeeded.", new Dictionary<string, object>
                {
                    ["ValidationPassed"] = true,
                    ["SecurityValidation"] = validationResult.Security.OperationsValid
                });
            }
            else
            {
                return HealthCheckResult.Degraded("Directory plan validation reported issues.", null, new Dictionary<string, object>
                {
                    ["ValidationPassed"] = false,
                    ["ErrorCount"] = validationResult.Errors.Count,
                    ["Errors"] = string.Join("; ", validationResult.Errors.Take(3))
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during directory plan health check");
            return HealthCheckResult.Unhealthy("Exception during directory plan health check", ex, new Dictionary<string, object>
            {
                ["ExceptionType"] = ex.GetType().Name,
                ["ExceptionMessage"] = ex.Message
            });
        }
    }
}

