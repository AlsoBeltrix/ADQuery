using System.Collections.Generic;
using System.Linq;
using AdQuery.Orchestrator.Models;
using Microsoft.Extensions.Configuration;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Normalizes directory plans prior to validation/execution (filter cleanup, alias mapping, limits).
/// </summary>
public interface IPlanPreprocessor
{
    void ApplyCustomMappings(DirectoryQueryPlan plan);
    void EnsurePlanLimit(DirectoryQueryPlan plan, int limit);
    void PrepareForExecution(DirectoryQueryPlan plan, int? requestedLimit);
}

public class PlanPreprocessor : IPlanPreprocessor
{
    private readonly HashSet<string> _licenseAttributeAliases;

    public PlanPreprocessor(IConfiguration configuration)
    {
        _licenseAttributeAliases = BuildLicenseAliasSet(configuration);
    }

    public void PrepareForExecution(DirectoryQueryPlan plan, int? requestedLimit)
    {
        ApplyCustomMappings(plan);

        if (requestedLimit.HasValue && requestedLimit.Value > 0)
        {
            EnsurePlanLimit(plan, requestedLimit.Value);
        }
    }

    public void ApplyCustomMappings(DirectoryQueryPlan plan)
    {
        if (plan?.Steps is null || plan.Steps.Count == 0)
        {
            return;
        }

        foreach (var step in plan.Steps)
        {
            if (step.Filters is null)
            {
                continue;
            }

            foreach (var filter in step.Filters)
            {
                NormalizeFilter(filter);
            }
        }

        if (plan.Projection?.Filter is not null)
        {
            NormalizeFilter(plan.Projection.Filter);
        }
    }

    public void EnsurePlanLimit(DirectoryQueryPlan plan, int limit)
    {
        if (plan is null || limit <= 0)
        {
            return;
        }

        var appliedLimit = plan.ResultLimit.HasValue && plan.ResultLimit.Value > 0
            ? Math.Min(plan.ResultLimit.Value, limit)
            : limit;

        plan.ResultLimit = appliedLimit;

        var targetStepName = plan.Projection?.RowStep;
        DirectoryPlanStep? targetStep = null;

        if (!string.IsNullOrWhiteSpace(targetStepName))
        {
            targetStep = plan.Steps.FirstOrDefault(step =>
                step.Name.Equals(targetStepName, StringComparison.OrdinalIgnoreCase));
        }

        if (targetStep is null)
        {
            targetStep = plan.Steps.FirstOrDefault(step =>
                string.Equals(step.Operation, "search", StringComparison.OrdinalIgnoreCase));
        }

        if (targetStep is not null)
        {
            if (!targetStep.SizeLimit.HasValue || targetStep.SizeLimit.Value <= 0 || targetStep.SizeLimit.Value > appliedLimit)
            {
                targetStep.SizeLimit = appliedLimit;
            }
        }
    }

    private void NormalizeFilter(DirectoryFilter filter)
    {
        if (filter is null)
        {
            return;
        }

        filter.Operator = NormalizeFilterOperator(filter.Operator);

        if (filter.Conditions is { Count: > 0 })
        {
            foreach (var child in filter.Conditions)
            {
                NormalizeFilter(child);
            }

            return;
        }

        var trimmedAttribute = filter.Attribute?.Trim();
        filter.Attribute = trimmedAttribute ?? filter.Attribute ?? string.Empty;
        filter.Value = (filter.Value ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(filter.Attribute))
        {
            return;
        }

        if (_licenseAttributeAliases.Contains(filter.Attribute))
        {
            filter.Attribute = "extensionAttribute11";
        }
    }

    private static string NormalizeFilterOperator(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "equals";
        }

        var normalized = value.Trim().ToLowerInvariant();
        normalized = normalized.Replace("-", "_").Replace(" ", "_");

        if (normalized.StartsWith("!"))
        {
            normalized = normalized[1..];
            return normalized switch
            {
                "equals" or "equal" => "not_equals",
                "contains" or "contain" => "not_contains",
                "starts_with" or "start_with" or "startswith" => "not_starts_with",
                "ends_with" or "end_with" or "endswith" => "not_ends_with",
                _ => "not_equals"
            };
        }

        return normalized switch
        {
            "=" or "==" or "equal" => "equals",
            "equals" => "equals",
            "not_equals" or "not_equal" or "not_equal_to" or "does_not_equal" or "not_equals_to" or "!=" => "not_equals",
            "contain" or "contains" => "contains",
            "notcontain" or "not_contains" or "does_not_contain" => "not_contains",
            "start_with" or "starts_with" or "startswith" => "starts_with",
            "not_start_with" or "not_starts_with" or "does_not_start_with" => "not_starts_with",
            "end_with" or "ends_with" or "endswith" => "ends_with",
            "not_end_with" or "not_ends_with" or "does_not_end_with" => "not_ends_with",
            _ when normalized.StartsWith("not_contains") => "not_contains",
            _ when normalized.StartsWith("not_starts_with") => "not_starts_with",
            _ when normalized.StartsWith("not_ends_with") => "not_ends_with",
            _ => normalized
        };
    }

    private static HashSet<string> BuildLicenseAliasSet(IConfiguration configuration)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "license",
            "licenses",
            "licence",
            "licences",
            "extensionattribute11"
        };

        var extensionAttributesSection = configuration.GetSection("CustomMappings:ExtensionAttributes");
        foreach (var child in extensionAttributesSection.GetChildren())
        {
            if (child.Key.Equals("ExtensionAttribute11", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(child.Value))
            {
                aliases.Add(child.Value);
            }
        }

        return aliases;
    }
}
