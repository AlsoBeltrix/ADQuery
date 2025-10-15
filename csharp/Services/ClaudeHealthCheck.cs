using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Health check for Claude AI service connectivity and functionality
/// </summary>
public class ClaudeHealthCheck : IHealthCheck
{
    private readonly IClaudeService _claudeService;
    private readonly ILogger<ClaudeHealthCheck> _logger;

    public ClaudeHealthCheck(IClaudeService claudeService, ILogger<ClaudeHealthCheck> logger)
    {
        _claudeService = claudeService;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var healthCheck = await _claudeService.CheckHealthAsync(cancellationToken);

            if (healthCheck.IsHealthy && healthCheck.JsonParsingWorking)
            {
                return HealthCheckResult.Healthy("Claude service is responding correctly", new Dictionary<string, object>
                {
                    ["ResponseTimeMs"] = healthCheck.ResponseTimeMs,
                    ["LastSuccessfulResponse"] = healthCheck.LastSuccessfulResponse?.ToString() ?? "Never",
                    ["JsonParsingWorking"] = healthCheck.JsonParsingWorking
                });
            }
            else if (healthCheck.IsHealthy)
            {
                return HealthCheckResult.Degraded("Claude service responding but JSON parsing issues", null, new Dictionary<string, object>
                {
                    ["ResponseTimeMs"] = healthCheck.ResponseTimeMs,
                    ["JsonParsingWorking"] = healthCheck.JsonParsingWorking,
                    ["ErrorMessage"] = healthCheck.ErrorMessage ?? "Unknown issue"
                });
            }
            else
            {
                return HealthCheckResult.Unhealthy("Claude service is not responding", null, new Dictionary<string, object>
                {
                    ["ResponseTimeMs"] = healthCheck.ResponseTimeMs,
                    ["ErrorMessage"] = healthCheck.ErrorMessage ?? "Unknown error"
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during Claude health check");
            return HealthCheckResult.Unhealthy("Exception during Claude health check", ex, new Dictionary<string, object>
            {
                ["ExceptionType"] = ex.GetType().Name,
                ["ExceptionMessage"] = ex.Message
            });
        }
    }
}