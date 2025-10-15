using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using Microsoft.AspNetCore.Hosting;

namespace AdQuery.Orchestrator.Security;

/// <summary>
/// Validates directory query plans against the project's security guardrails.
/// </summary>
public class PlanValidator : IPlanValidator
{
    private static readonly HashSet<string> AllowedOperations = new(StringComparer.OrdinalIgnoreCase)
    {
        "search",
        "expand_members",
        "lookup"
    };

    private static readonly HashSet<string> AllowedFilterOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "equals",
        "contains",
        "starts_with",
        "ends_with"
    };

    private readonly ILogger<PlanValidator> _logger;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<DirectoryObjectType, HashSet<string>> _allowedAttributes;

    private const int MaxStepsPerPlan = 10;
    private const int MaxFiltersPerStep = 5;
    private const int MaxAttributesPerStep = 25;
    private const int MaxProjectionColumns = 25;

    public PlanValidator(
        ILogger<PlanValidator> logger,
        IConfiguration configuration,
        IWebHostEnvironment environment)
    {
        _logger = logger;
        _configuration = configuration;
        _allowedAttributes = LoadAllowedAttributes(configuration, environment, logger);
    }

    public Task<PlanSecurityResult> ValidateSecurityAsync(DirectoryQueryPlan plan)
    {
        var result = new PlanSecurityResult();
        var seenSteps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var step in plan.Steps)
        {
            if (!AllowedOperations.Contains(step.Operation))
            {
                result.OperationsValid = false;
                result.BlockedOperations.Add($"Step {step.Step} uses unsupported operation '{step.Operation}'.");
            }

            if (!seenSteps.Add(step.Name))
            {
                result.SecurityErrors.Add($"Duplicate step name detected: {step.Name}");
            }

            if (step.Attributes.Count > MaxAttributesPerStep)
            {
                result.SecurityErrors.Add($"Step {step.Step} requests too many attributes ({step.Attributes.Count}).");
            }

            if (!_allowedAttributes.TryGetValue(step.TargetType, out var allowedAttributes) || allowedAttributes.Count == 0)
            {
                result.SecurityErrors.Add($"No allow-listed attributes configured for {step.TargetType}.");
                continue;
            }

            foreach (var attribute in step.Attributes)
            {
                if (!allowedAttributes.Contains(attribute))
                {
                    result.SecurityErrors.Add($"Step {step.Step} requests attribute '{attribute}' which is not allow-listed for {step.TargetType}.");
                }
            }

            foreach (var filter in step.Filters)
            {
                if (!allowedAttributes.Contains(filter.Attribute))
                {
                    result.SecurityErrors.Add($"Step {step.Step} filter references attribute '{filter.Attribute}' which is not allow-listed.");
                }

                if (!AllowedFilterOperators.Contains(filter.Operator))
                {
                    result.SecurityErrors.Add($"Step {step.Step} uses unsupported filter operator '{filter.Operator}'.");
                }
            }

            if (!string.IsNullOrWhiteSpace(step.Source) && !seenSteps.Contains(step.Source))
            {
                result.SecurityErrors.Add($"Step {step.Step} references unknown source '{step.Source}'. Steps must reference prior results.");
            }

            if (step.Operation.Equals("lookup", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(step.Source))
                {
                    result.SecurityErrors.Add($"Step {step.Step} lookup operation requires a source step.");
                }

                if (string.IsNullOrWhiteSpace(step.SourceAttribute))
                {
                    result.SecurityErrors.Add($"Step {step.Step} lookup operation requires 'source_attribute'.");
                }
            }
        }

        if (plan.Projection.Columns.Count > MaxProjectionColumns)
        {
            result.SecurityErrors.Add($"Projection defines too many columns ({plan.Projection.Columns.Count}).");
        }

        if (!result.SecurityErrors.Any() && result.OperationsValid)
        {
            _logger.LogDebug("Plan passed security validation.");
        }
        else
        {
            result.OperationsValid = false;
        }

        return Task.FromResult(result);
    }

    public bool ValidateHmac(DirectoryQueryPlan plan, string signature)
    {
        try
        {
            var secretKey = _configuration["Security:HmacSecretKey"];
            if (string.IsNullOrEmpty(secretKey))
            {
                _logger.LogWarning("HMAC secret key not configured; validation skipped.");
                return false;
            }

            var planJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            });

            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
            var computedHash = hmac.ComputeHash(Encoding.UTF8.GetBytes(planJson));
            var computedSignature = Convert.ToBase64String(computedHash);

            var isValid = string.Equals(signature, computedSignature, StringComparison.Ordinal);
            if (!isValid)
            {
                _logger.LogWarning("HMAC validation failed for supplied plan.");
            }

            return isValid;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating HMAC signature.");
            return false;
        }
    }

    public bool ValidateComplexity(DirectoryQueryPlan plan)
    {
        if (plan.Steps.Count > MaxStepsPerPlan)
        {
            _logger.LogWarning("Plan exceeds maximum step count: {Count}", plan.Steps.Count);
            return false;
        }

        foreach (var step in plan.Steps)
        {
            if (step.Filters.Count > MaxFiltersPerStep)
            {
                _logger.LogWarning("Step {Step} exceeds filter limit: {Count}", step.Step, step.Filters.Count);
                return false;
            }
        }

        return true;
    }

    private static Dictionary<DirectoryObjectType, HashSet<string>> LoadAllowedAttributes(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger logger)
    {
        var defaults = GetDefaultAllowLists();
        var result = new Dictionary<DirectoryObjectType, HashSet<string>>();

        foreach (var (objectType, fallback) in defaults)
        {
            var configKey = $"Security:AttributeFiles:{objectType}";
            var configuredPath = configuration[configKey];
            HashSet<string> allowedSet;

            if (!string.IsNullOrWhiteSpace(configuredPath))
            {
                var resolvedPath = Path.IsPathRooted(configuredPath)
                    ? configuredPath
                    : Path.Combine(environment.ContentRootPath, configuredPath);

                try
                {
                    if (File.Exists(resolvedPath))
                    {
                        var attributes = File.ReadAllLines(resolvedPath)
                            .Select(line => line?.Trim())
                            .Where(line => !string.IsNullOrWhiteSpace(line) && !line!.StartsWith("#"))
                            .Select(line => line!)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .ToList();

                        if (attributes.Count == 0)
                        {
                            logger.LogWarning("Allow-list file {File} for {ObjectType} is empty. Falling back to defaults.", resolvedPath, objectType);
                            allowedSet = new HashSet<string>(fallback, StringComparer.OrdinalIgnoreCase);
                        }
                        else
                        {
                            allowedSet = new HashSet<string>(attributes, StringComparer.OrdinalIgnoreCase);
                            logger.LogInformation("Loaded {Count} allowed attributes for {ObjectType} from {File}.", allowedSet.Count, objectType, resolvedPath);
                        }
                    }
                    else
                    {
                        logger.LogWarning("Allow-list file {File} for {ObjectType} not found. Falling back to defaults.", resolvedPath, objectType);
                        allowedSet = new HashSet<string>(fallback, StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load allow-list file {File} for {ObjectType}. Falling back to defaults.", resolvedPath, objectType);
                    allowedSet = new HashSet<string>(fallback, StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                allowedSet = new HashSet<string>(fallback, StringComparer.OrdinalIgnoreCase);
            }

            result[objectType] = allowedSet;
        }

        return result;
    }

    private static Dictionary<DirectoryObjectType, string[]> GetDefaultAllowLists()
    {
        return new Dictionary<DirectoryObjectType, string[]>
        {
            [DirectoryObjectType.User] = new[]
            {
                "distinguishedName",
                "displayName",
                "name",
                "givenName",
                "sn",
                "userAccountControl",
                "mail",
                "userPrincipalName",
                "sAMAccountName",
                "manager",
                "department",
                "title",
                "telephoneNumber",
                "mobile",
                "whenCreated",
                "whenChanged",
                "accountExpires",
                "enabled",
                "memberOf",
                "lastLogonTimestamp"
            },
            [DirectoryObjectType.Group] = new[]
            {
                "distinguishedName",
                "name",
                "mail",
                "description",
                "sAMAccountName",
                "groupType",
                "member",
                "whenCreated",
                "whenChanged"
            },
            [DirectoryObjectType.Computer] = new[]
            {
                "distinguishedName",
                "name",
                "dnsHostName",
                "operatingSystem",
                "operatingSystemVersion",
                "lastLogonTimestamp",
                "whenCreated",
                "whenChanged"
            },
            [DirectoryObjectType.OrganizationalUnit] = new[]
            {
                "distinguishedName",
                "name",
                "description",
                "whenCreated",
                "whenChanged"
            }
        };
    }
}
