using System.Collections.Frozen;
using AdQuery.Orchestrator.Models;
using Microsoft.AspNetCore.Hosting;

namespace AdQuery.Orchestrator.Security;

/// <summary>
/// Loads and exposes the directory security allow-lists without permitting mutation.
/// </summary>
public sealed class DirectorySecurityPolicy : IDirectorySecurityPolicy
{
    private static readonly FrozenSet<string> AllowedFilterOperators = new[]
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
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    private readonly FrozenDictionary<DirectoryObjectType, FrozenSet<string>> _allowedAttributes;

    public DirectorySecurityPolicy(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger<PlanValidator> logger)
    {
        _allowedAttributes = LoadAllowedAttributes(configuration, environment, logger);
    }

    public bool HasAllowedAttributes(DirectoryObjectType objectType)
    {
        return _allowedAttributes.TryGetValue(objectType, out var attributes) && attributes.Count > 0;
    }

    public bool IsAttributeAllowed(DirectoryObjectType objectType, string? attribute)
    {
        return attribute is not null &&
               _allowedAttributes.TryGetValue(objectType, out var attributes) &&
               attributes.Contains(attribute);
    }

    public bool IsFilterOperatorAllowed(string? operatorValue)
    {
        return operatorValue is not null && AllowedFilterOperators.Contains(operatorValue);
    }

    private static FrozenDictionary<DirectoryObjectType, FrozenSet<string>> LoadAllowedAttributes(
        IConfiguration configuration,
        IWebHostEnvironment environment,
        ILogger logger)
    {
        var defaults = GetDefaultAllowLists();
        var result = new Dictionary<DirectoryObjectType, FrozenSet<string>>();

        foreach (var (objectType, fallback) in defaults)
        {
            var configKey = $"Security:AttributeFiles:{objectType}";
            var configuredPath = configuration[configKey];
            FrozenSet<string> allowedSet;

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
                            allowedSet = fallback.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
                        }
                        else
                        {
                            allowedSet = attributes.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
                            logger.LogInformation("Loaded {Count} allowed attributes for {ObjectType} from {File}.", allowedSet.Count, objectType, resolvedPath);
                        }
                    }
                    else
                    {
                        logger.LogWarning("Allow-list file {File} for {ObjectType} not found. Falling back to defaults.", resolvedPath, objectType);
                        allowedSet = fallback.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to load allow-list file {File} for {ObjectType}. Falling back to defaults.", resolvedPath, objectType);
                    allowedSet = fallback.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
                }
            }
            else
            {
                allowedSet = fallback.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
            }

            result[objectType] = allowedSet;
        }

        return result.ToFrozenDictionary();
    }

    private static Dictionary<DirectoryObjectType, string[]> GetDefaultAllowLists()
    {
        return new Dictionary<DirectoryObjectType, string[]>
        {
            [DirectoryObjectType.User] =
            [
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
            ],
            [DirectoryObjectType.Group] =
            [
                "distinguishedName",
                "name",
                "mail",
                "description",
                "sAMAccountName",
                "groupType",
                "member",
                "whenCreated",
                "whenChanged"
            ],
            [DirectoryObjectType.Computer] =
            [
                "distinguishedName",
                "name",
                "dnsHostName",
                "operatingSystem",
                "operatingSystemVersion",
                "lastLogonTimestamp",
                "whenCreated",
                "whenChanged"
            ],
            [DirectoryObjectType.OrganizationalUnit] =
            [
                "distinguishedName",
                "name",
                "description",
                "whenCreated",
                "whenChanged"
            ]
        };
    }
}
