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
        "lookup",
        "expand_reports"
    };

    private static readonly HashSet<string> AllowedFilterOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "equals",
        "not_equals",
        "contains",
        "not_contains",
        "starts_with",
        "not_starts_with",
        "ends_with",
        "not_ends_with",
        "and",
        "or"
    };

    private readonly ILogger<PlanValidator> _logger;
    private readonly IConfiguration _configuration;
    private readonly Dictionary<DirectoryObjectType, HashSet<string>> _allowedAttributes;
    private readonly int _maxStepsPerPlan;

    private const int DefaultMaxStepsPerPlan = 10;
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
        _maxStepsPerPlan = Math.Max(1, _configuration.GetValue<int>("Security:MaxPlanComplexity", DefaultMaxStepsPerPlan));
    }

    public Task<PlanSecurityResult> ValidateSecurityAsync(DirectoryQueryPlan plan)
    {
        var result = new PlanSecurityResult();
        var seenSteps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stepLookup = new Dictionary<string, DirectoryPlanStep>(StringComparer.OrdinalIgnoreCase);

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
            else
            {
                stepLookup[step.Name] = step;
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

            ValidateFilters(step.Filters, step.TargetType, step.Step, allowedAttributes, result);

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

            // Validate expand_reports operation
            if (step.Operation.Equals("expand_reports", StringComparison.OrdinalIgnoreCase))
            {
                var expandReportsResult = ValidateExpandReports(step);
                result.SecurityErrors.AddRange(expandReportsResult.SecurityErrors);
                if (!expandReportsResult.OperationsValid)
                {
                    result.OperationsValid = false;
                }
            }
        }

        if (plan.Projection.Columns.Count > MaxProjectionColumns)
        {
            result.SecurityErrors.Add($"Projection defines too many columns ({plan.Projection.Columns.Count}).");
        }

        ValidateProjectionFilter(plan, result, stepLookup);

        // Validate aggregation if present
        if (plan.Projection?.Aggregation != null)
        {
            var aggregationResult = ValidateAggregation(plan.Projection);
            result.SecurityErrors.AddRange(aggregationResult.SecurityErrors);
            if (!aggregationResult.OperationsValid)
            {
                result.OperationsValid = false;
            }
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
        if (plan.Steps.Count > _maxStepsPerPlan)
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
    private void ValidateProjectionFilter(DirectoryQueryPlan plan, PlanSecurityResult result, Dictionary<string, DirectoryPlanStep> stepLookup)
    {
        if (plan?.Projection?.Filter is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(plan.Projection.RowStep))
        {
            result.SecurityErrors.Add("Projection filter specified but projection.row_step is empty.");
            return;
        }

        if (!stepLookup.TryGetValue(plan.Projection.RowStep, out var rowStep))
        {
            result.SecurityErrors.Add($"Projection filter references unknown row_step '{plan.Projection.RowStep}'.");
            return;
        }

        var filter = plan.Projection.Filter;
        if (string.IsNullOrWhiteSpace(filter.Attribute))
        {
            result.SecurityErrors.Add("Projection filter attribute is required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(filter.Value))
        {
            result.SecurityErrors.Add("Projection filter value is required.");
            return;
        }

        var operatorValue = string.IsNullOrWhiteSpace(filter.Operator) ? "equals" : filter.Operator;
        filter.Operator = operatorValue;

        if (!AllowedFilterOperators.Contains(operatorValue))
        {
            result.SecurityErrors.Add($"Projection filter uses unsupported operator '{operatorValue}'.");
        }

        if (!_allowedAttributes.TryGetValue(rowStep.TargetType, out var allowedAttributes) || allowedAttributes.Count == 0)
        {
            result.SecurityErrors.Add($"No allow-listed attributes configured for projection row step type {rowStep.TargetType}.");
            return;
        }

        if (!allowedAttributes.Contains(filter.Attribute))
        {
            result.SecurityErrors.Add($"Projection filter references attribute '{filter.Attribute}' which is not allow-listed for {rowStep.TargetType}.");
        }
    }

    private void ValidateFilters(IEnumerable<DirectoryFilter> filters, DirectoryObjectType targetType, int stepNumber, HashSet<string> allowedAttributes, PlanSecurityResult result)
    {
        if (filters is null)
        {
            return;
        }

        foreach (var filter in filters)
        {
            if (filter is null)
            {
                continue;
            }

            var operatorValue = string.IsNullOrWhiteSpace(filter.Operator)
                ? (filter.Conditions is { Count: > 0 } ? "and" : "equals")
                : filter.Operator.Trim();

            filter.Operator = operatorValue;

            if (filter.Conditions is { Count: > 0 })
            {
                if (!AllowedFilterOperators.Contains(operatorValue))
                {
                    result.SecurityErrors.Add($"Step {stepNumber} uses unsupported filter operator '{operatorValue}'.");
                }

                ValidateFilters(filter.Conditions, targetType, stepNumber, allowedAttributes, result);
                continue;
            }

            var attribute = filter.Attribute?.Trim();
            filter.Attribute = attribute ?? string.Empty;

            if (string.IsNullOrWhiteSpace(attribute))
            {
                result.SecurityErrors.Add($"Step {stepNumber} filter attribute is required.");
                continue;
            }

            if (!allowedAttributes.Contains(attribute))
            {
                result.SecurityErrors.Add($"Step {stepNumber} filter references attribute '{attribute}' which is not allow-listed.");
            }

            if (!AllowedFilterOperators.Contains(operatorValue))
            {
                result.SecurityErrors.Add($"Step {stepNumber} uses unsupported filter operator '{operatorValue}'.");
            }
        }
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

    private PlanSecurityResult ValidateExpandReports(DirectoryPlanStep step)
    {
        var errors = new List<string>();

        // Feature disabled check
        if (!_configuration.GetValue<bool>("Security:EnableRecursiveQueries"))
        {
            errors.Add($"Step '{step.Name}': recursive queries are disabled");
            return new PlanSecurityResult { OperationsValid = false, SecurityErrors = errors };
        }

        // max_depth validation
        if (step.MaxDepth.HasValue)
        {
            if (step.MaxDepth.Value < 1)
            {
                errors.Add($"Step '{step.Name}': max_depth must be >= 1 (got {step.MaxDepth.Value})");
            }

            var maxDepthLimit = _configuration.GetValue<int>("Security:MaxRecursionDepth");
            if (step.MaxDepth.Value > maxDepthLimit)
            {
                errors.Add($"Step '{step.Name}': max_depth {step.MaxDepth.Value} exceeds limit of {maxDepthLimit}");
            }
        }

        // max_nodes validation
        if (step.MaxNodes.HasValue)
        {
            var maxNodesLimit = _configuration.GetValue<int>("Security:MaxNodesPerRecursion");
            if (step.MaxNodes.Value < 1)
            {
                errors.Add($"Step '{step.Name}': max_nodes must be >= 1 (got {step.MaxNodes.Value})");
            }
            if (step.MaxNodes.Value > maxNodesLimit)
            {
                errors.Add($"Step '{step.Name}': max_nodes {step.MaxNodes.Value} exceeds limit of {maxNodesLimit}");
            }
        }

        // Missing source check
        if (string.IsNullOrEmpty(step.Source))
        {
            errors.Add($"Step '{step.Name}': expand_reports requires 'source' field");
        }

        // Wrong target_type check
        if (step.TargetType != DirectoryObjectType.User)
        {
            errors.Add($"Step '{step.Name}': expand_reports only supports target_type 'User' (got '{step.TargetType}')");
        }

        // Empty attributes check
        if (step.Attributes == null || !step.Attributes.Any())
        {
            errors.Add($"Step '{step.Name}': expand_reports requires at least one attribute");
        }

        return new PlanSecurityResult
        {
            OperationsValid = !errors.Any(),
            SecurityErrors = errors
        };
    }

    private PlanSecurityResult ValidateAggregation(ProjectionDefinition projection)
    {
        if (projection?.Aggregation == null)
        {
            return new PlanSecurityResult { OperationsValid = true };
        }

        var errors = new List<string>();
        var agg = projection.Aggregation;

        // No grouping and no count
        if (!agg.GroupBy.Any() && !agg.Count)
        {
            errors.Add("Aggregation requires 'group_by' fields or 'count: true'");
        }

        // Empty group_by fields
        if (agg.GroupBy.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("Aggregation group_by contains empty field names");
        }

        // Validate fields in allow-list (assume User type for aggregation fields)
        if (_allowedAttributes.TryGetValue(DirectoryObjectType.User, out var allowedAttributes))
        {
            foreach (var field in agg.GroupBy.Where(f => !string.IsNullOrWhiteSpace(f)))
            {
                if (!allowedAttributes.Contains(field))
                {
                    errors.Add($"Aggregation field '{field}' is not in attribute allow-list");
                }
            }
        }

        // Too many fields
        if (agg.GroupBy.Count > 5)
        {
            errors.Add($"Aggregation group_by has {agg.GroupBy.Count} fields; maximum is 5");
        }

        // Duplicates
        var duplicates = agg.GroupBy
            .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        if (duplicates.Any())
        {
            errors.Add($"Aggregation contains duplicate fields: {string.Join(", ", duplicates)}");
        }

        return new PlanSecurityResult
        {
            OperationsValid = !errors.Any(),
            SecurityErrors = errors
        };
    }
}




