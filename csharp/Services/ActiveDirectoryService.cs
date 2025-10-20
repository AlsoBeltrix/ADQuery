using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.DirectoryServices;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// LDAP-backed implementation that queries Active Directory using managed APIs.
/// </summary>
public class ActiveDirectoryService : IActiveDirectoryService
{
    private readonly ILogger<ActiveDirectoryService> _logger;
    private readonly IConfiguration _configuration;

    public ActiveDirectoryService(ILogger<ActiveDirectoryService> logger, IConfiguration configuration)
    {
        _logger = logger;
        _configuration = configuration;
    }

    public Task<IReadOnlyList<DirectoryRecord>> SearchAsync(DirectorySearchRequest request, CancellationToken cancellationToken = default)
    {
        var attributes = NormalizeAttributes(request.Attributes);
        var normalizedFilters = new List<DirectoryFilter>();

        if (request.Filters is not null && request.Filters.Count > 0)
        {
            foreach (var filter in request.Filters)
            {
                if (filter is null)
                {
                    continue;
                }

                if (!TryNormalizeFilter(filter, out var normalizedFilter))
                {
                    _logger.LogWarning("Skipping directory search for {TargetType} because a filter was missing required information.", request.TargetType);
                    return Task.FromResult<IReadOnlyList<DirectoryRecord>>(Array.Empty<DirectoryRecord>());
                }

                normalizedFilters.Add(normalizedFilter);
            }

            request.Filters = normalizedFilters;
        }

        var records = new List<DirectoryRecord>();

        using var entry = CreateDirectoryEntry(request.SearchBase);
        using var searcher = new DirectorySearcher(entry)
        {
            Filter = BuildFilter(request),
            SearchScope = MapScope(request.Scope),
            PageSize = 500
        };

        if (request.SizeLimit.HasValue && request.SizeLimit.Value > 0)
        {
            searcher.SizeLimit = request.SizeLimit.Value;
        }

        foreach (var attribute in attributes)
        {
            searcher.PropertiesToLoad.Add(attribute);
        }

        using var results = searcher.FindAll();
        foreach (SearchResult result in results)
        {
            cancellationToken.ThrowIfCancellationRequested();
            records.Add(MapToRecord(request.TargetType, result, attributes));
        }

        return Task.FromResult<IReadOnlyList<DirectoryRecord>>(records);
    }

    public async Task<IReadOnlyList<DirectoryRecord>> ExpandGroupMembersAsync(IEnumerable<string> groupDistinguishedNames, bool recursive, IEnumerable<string> attributes, CancellationToken cancellationToken = default)
    {
        var uniqueGroups = new HashSet<string>(groupDistinguishedNames.Where(g => !string.IsNullOrWhiteSpace(g)), StringComparer.OrdinalIgnoreCase);
        if (uniqueGroups.Count == 0)
        {
            return Array.Empty<DirectoryRecord>();
        }

        var memberDns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var groupDn in uniqueGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                using var groupEntry = new DirectoryEntry($"LDAP://{groupDn}");
                groupEntry.AuthenticationType = AuthenticationTypes.Secure | AuthenticationTypes.Sealing | AuthenticationTypes.Signing;
                groupEntry.RefreshCache(new[] { "member" });

                var values = groupEntry.Properties["member"];
                if (values is null)
                {
                    continue;
                }

                foreach (var value in values)
                {
                    if (value is string memberDn && memberDn.Length > 0)
                    {
                        memberDns.Add(memberDn);
                        if (recursive && memberDn.Contains("CN="))
                        {
                            // Simple heuristic to handle nested groups later
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to read members for group {GroupDn}", groupDn);
            }
        }

        if (memberDns.Count == 0)
        {
            return Array.Empty<DirectoryRecord>();
        }

        var records = await LookupAsync(memberDns, DirectoryObjectType.User, attributes, cancellationToken);

        if (!recursive)
        {
            return records;
        }

        // Recursive expansion: gather nested group members.
        var nestedGroups = records
            .Where(r => r.ObjectType == DirectoryObjectType.Group)
            .Select(r => r.DistinguishedName);

        if (nestedGroups.Any())
        {
            var nestedMembers = await ExpandGroupMembersAsync(nestedGroups, recursive: true, attributes, cancellationToken);
            return records.Concat(nestedMembers).ToList();
        }

        return records;
    }

    public Task<IReadOnlyList<DirectoryRecord>> LookupAsync(IEnumerable<string> distinguishedNames, DirectoryObjectType targetType, IEnumerable<string> attributes, CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentBag<DirectoryRecord>();
        var normalizedAttributes = NormalizeAttributes(attributes);
        var dns = distinguishedNames
            .Where(dn => !string.IsNullOrWhiteSpace(dn))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (dns.Count == 0)
        {
            return Task.FromResult<IReadOnlyList<DirectoryRecord>>(Array.Empty<DirectoryRecord>());
        }

        Parallel.ForEach(dns, new ParallelOptions { CancellationToken = cancellationToken, MaxDegreeOfParallelism = 4 }, dn =>
        {
            try
            {
                using var entry = new DirectoryEntry($"LDAP://{dn}");
                entry.AuthenticationType = AuthenticationTypes.Secure | AuthenticationTypes.Sealing | AuthenticationTypes.Signing;
                entry.RefreshCache(normalizedAttributes.ToArray());

                var record = new DirectoryRecord
                {
                    ObjectType = targetType,
                    DistinguishedName = dn
                };

                foreach (string attribute in normalizedAttributes)
                {
                    var property = entry.Properties[attribute];
                    if (property is null || property.Count == 0)
                    {
                        continue;
                    }

                    record.Attributes[attribute] = property.Count switch
                    {
                        1 => property[0],
                        _ => property.Cast<object>().Select(v => v?.ToString() ?? string.Empty).ToArray()
                    };
                }

                results.Add(record);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to lookup entry {DistinguishedName}", dn);
            }
        });

        return Task.FromResult<IReadOnlyList<DirectoryRecord>>(results.ToList());
    }

    public Task<IReadOnlyList<DirectoryRecord>> GetDirectReportsBatch(IEnumerable<string> managerDistinguishedNames, IEnumerable<string> attributes, CancellationToken cancellationToken = default)
    {
        var managerDNs = managerDistinguishedNames
            .Where(dn => !string.IsNullOrWhiteSpace(dn))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!managerDNs.Any())
        {
            return Task.FromResult<IReadOnlyList<DirectoryRecord>>(Array.Empty<DirectoryRecord>());
        }

        var normalizedAttributes = NormalizeAttributes(attributes);

        // Build OR filter for all managers: (|(manager=DN1)(manager=DN2)...)
        var managerFilters = managerDNs
            .Select(dn => new DirectoryFilter
            {
                Attribute = "manager",
                Operator = "equals",
                Value = dn
            })
            .ToList();

        var batchFilter = new DirectoryFilter
        {
            Operator = "or",
            Conditions = managerFilters
        };

        var request = new DirectorySearchRequest
        {
            TargetType = DirectoryObjectType.User,
            Filters = new List<DirectoryFilter> { batchFilter },
            Attributes = normalizedAttributes
        };

        _logger.LogDebug("Batch direct reports query for {Count} managers", managerDNs.Count);

        return SearchAsync(request, cancellationToken);
    }

    private DirectoryEntry CreateDirectoryEntry(string? searchBase)
    {
        var path = searchBase ?? ResolveDefaultNamingContext();
        var connectionString = path.StartsWith("LDAP", StringComparison.OrdinalIgnoreCase) ? path : $"LDAP://{path}";

        var entry = new DirectoryEntry(connectionString)
        {
            AuthenticationType = AuthenticationTypes.Secure | AuthenticationTypes.Sealing | AuthenticationTypes.Signing
        };

        return entry;
    }

    private string ResolveDefaultNamingContext()
    {
        var configured = _configuration["ActiveDirectory:RootPath"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured!;
        }

        using var root = new DirectoryEntry("LDAP://RootDSE");
        return root.Properties["defaultNamingContext"][0]?.ToString() ?? throw new InvalidOperationException("Unable to resolve default naming context.");
    }

    private bool TryNormalizeFilter(DirectoryFilter filter, out DirectoryFilter normalized)
    {
        var operatorValue = string.IsNullOrWhiteSpace(filter.Operator)
            ? (filter.Conditions is { Count: > 0 } ? "and" : "equals")
            : filter.Operator.Trim();

        if (filter.Conditions is { Count: > 0 })
        {
            var normalizedChildren = new List<DirectoryFilter>();
            foreach (var child in filter.Conditions)
            {
                if (child is null)
                {
                    continue;
                }

                if (!TryNormalizeFilter(child, out var normalizedChild))
                {
                    normalized = null!;
                    return false;
                }

                normalizedChildren.Add(normalizedChild);
            }

            normalized = new DirectoryFilter
            {
                Operator = operatorValue,
                Conditions = normalizedChildren
            };

            return true;
        }

        var attribute = filter.Attribute?.Trim();
        var value = filter.Value?.Trim();

        if (string.IsNullOrWhiteSpace(attribute) ||
            (string.IsNullOrWhiteSpace(value) && !AllowsEmptyFilterValue(attribute, operatorValue)))
        {
            normalized = null!;
            return false;
        }

        normalized = new DirectoryFilter
        {
            Attribute = attribute,
            Operator = operatorValue,
            Value = value ?? string.Empty
        };

        return true;
    }

    private static bool AllowsEmptyFilterValue(string attribute, string operatorValue)
    {
        if (attribute.Equals("AccountExpirationDate", StringComparison.OrdinalIgnoreCase) &&
            (operatorValue.Equals("not_equals", StringComparison.OrdinalIgnoreCase) ||
             operatorValue.Equals("equals", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private string BuildFilter(DirectorySearchRequest request)
    {
        var clauses = new List<string> { BuildObjectClassClause(request.TargetType) };
        foreach (var filter in request.Filters)
        {
            clauses.Add(BuildFilterClause(filter));
        }

        var builder = new StringBuilder();
        builder.Append("(&");
        foreach (var clause in clauses)
        {
            builder.Append(clause);
        }
        builder.Append(')');
        return builder.ToString();
    }

    private static string BuildObjectClassClause(DirectoryObjectType type)
    {
        return type switch
        {
            DirectoryObjectType.User => "(objectCategory=person)(objectClass=user)",
            DirectoryObjectType.Group => "(objectClass=group)",
            DirectoryObjectType.Computer => "(objectCategory=computer)",
            DirectoryObjectType.OrganizationalUnit => "(objectClass=organizationalUnit)",
            _ => "(objectClass=*)"
        };
    }

    private static string BuildFilterClause(DirectoryFilter filter)
    {
        if (filter.Conditions is { Count: > 0 })
        {
            return BuildCompoundFilterClause(filter);
        }

        if (filter.Attribute.Equals("Enabled", StringComparison.OrdinalIgnoreCase))
        {
            return BuildEnabledFilterClause(filter);
        }

        if (filter.Attribute.Equals("AccountExpirationDate", StringComparison.OrdinalIgnoreCase))
        {
            return BuildAccountExpirationDateFilterClause(filter);
        }

        var attribute = filter.Attribute;
        var value = EscapeLdapValue(filter.Value ?? string.Empty);

        return filter.Operator.ToLowerInvariant() switch
        {
            "equals" => $"({attribute}={value})",
            "not_equals" => $"(!({attribute}={value}))",
            "contains" => $"({attribute}=*{value}*)",
            "not_contains" => $"(!({attribute}=*{value}*))",
            "starts_with" => $"({attribute}={value}*)",
            "not_starts_with" => $"(!({attribute}={value}*))",
            "ends_with" => $"({attribute}=*{value})",
            "not_ends_with" => $"(!({attribute}=*{value}))",
            _ => $"({attribute}={value})"
        };
    }

    private static string BuildCompoundFilterClause(DirectoryFilter filter)
    {
        var operatorValue = string.IsNullOrWhiteSpace(filter.Operator)
            ? "and"
            : filter.Operator.Trim().ToLowerInvariant();

        var joiner = operatorValue.Equals("or", StringComparison.OrdinalIgnoreCase) ? '|' : '&';
        var builder = new StringBuilder();
        builder.Append('(').Append(joiner);

        foreach (var child in filter.Conditions ?? Enumerable.Empty<DirectoryFilter>())
        {
            builder.Append(BuildFilterClause(child));
        }

        builder.Append(')');
        return builder.ToString();
    }

    private static string BuildEnabledFilterClause(DirectoryFilter filter)
    {
        var operatorValue = string.IsNullOrWhiteSpace(filter.Operator)
            ? "equals"
            : filter.Operator.Trim().ToLowerInvariant();

        var normalizedValue = (filter.Value ?? string.Empty).Trim();
        var disabledClause = "(userAccountControl:1.2.840.113556.1.4.803:=2)";
        var enabledClause = $"(!{disabledClause})";

        return operatorValue switch
        {
            "equals" => IsDisabledComparison(normalizedValue) ? disabledClause : enabledClause,
            "not_equals" => IsDisabledComparison(normalizedValue) ? enabledClause : disabledClause,
            _ => disabledClause
        };
    }

    private static string BuildAccountExpirationDateFilterClause(DirectoryFilter filter)
    {
        const string NeverExpiresClause = "(|(accountExpires=0)(accountExpires=9223372036854775807))";
        var nowFileTime = DateTime.UtcNow.ToFileTimeUtc();
        var expiredClause = $"(&(!(accountExpires=0))(!(accountExpires=9223372036854775807))(accountExpires<={nowFileTime}))";

        var operatorValue = string.IsNullOrWhiteSpace(filter.Operator)
            ? "equals"
            : filter.Operator.Trim().ToLowerInvariant();

        var rawValue = (filter.Value ?? string.Empty).Trim();

        switch (operatorValue)
        {
            case "contains":
                if (rawValue.Contains("/", StringComparison.Ordinal) || rawValue.Contains("-", StringComparison.Ordinal))
                {
                    return expiredClause;
                }
                break;

            case "equals":
                if (string.IsNullOrWhiteSpace(rawValue) || rawValue.Equals("never", StringComparison.OrdinalIgnoreCase))
                {
                    return NeverExpiresClause;
                }

                if (TryParseAccountExpirationDate(rawValue, out var equalsFileTime))
                {
                    return $"(accountExpires={equalsFileTime})";
                }
                break;

            case "not_equals":
                if (string.IsNullOrWhiteSpace(rawValue) || rawValue.Equals("never", StringComparison.OrdinalIgnoreCase))
                {
                    return expiredClause;
                }

                if (rawValue.Contains("/", StringComparison.Ordinal) || rawValue.Contains("-", StringComparison.Ordinal))
                {
                    return expiredClause;
                }

                if (TryParseAccountExpirationDate(rawValue, out var notEqualsFileTime))
                {
                    return $"(!(accountExpires={notEqualsFileTime}))";
                }
                break;
        }

        return expiredClause;
    }

    private static bool TryParseAccountExpirationDate(string input, out long fileTime)
    {
        fileTime = 0;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        if (DateTime.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) ||
            DateTime.TryParse(input, CultureInfo.CurrentCulture, DateTimeStyles.AssumeLocal, out parsed) ||
            DateTime.TryParse(input, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed))
        {
            fileTime = parsed.ToUniversalTime().ToFileTimeUtc();
            return true;
        }

        return false;
    }

    private static bool IsDisabledComparison(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        return value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("disabled", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("0", StringComparison.OrdinalIgnoreCase);
    }

    private static SearchScope MapScope(DirectorySearchScope scope) => scope switch
    {
        DirectorySearchScope.Base => SearchScope.Base,
        DirectorySearchScope.OneLevel => SearchScope.OneLevel,
        _ => SearchScope.Subtree
    };

    private static DirectoryRecord MapToRecord(DirectoryObjectType targetType, SearchResult result, IEnumerable<string> attributes)
    {
        var attributeList = attributes?.ToList() ?? new List<string>();

        var record = new DirectoryRecord
        {
            ObjectType = targetType,
            DistinguishedName = result.Properties["distinguishedName"][0]?.ToString() ?? string.Empty
        };

        foreach (var attribute in attributeList)
        {
            if (!result.Properties.Contains(attribute))
            {
                continue;
            }

            var propertyValues = result.Properties[attribute];
            record.Attributes[attribute] = propertyValues.Count switch
            {
                0 => null,
                1 => propertyValues[0],
                _ => propertyValues.Cast<object>().Select(v => v?.ToString() ?? string.Empty).ToArray()
            };
        }

        if (attributeList.Any(a => a.Equals("Enabled", StringComparison.OrdinalIgnoreCase)) &&
            TryGetUserAccountControl(result, out var userAccountControl))
        {
            var isDisabled = (userAccountControl & 0x2) == 0x2;
            record.Attributes["Enabled"] = isDisabled ? "false" : "true";
        }

        if (attributeList.Any(a => a.Equals("AccountExpirationDate", StringComparison.OrdinalIgnoreCase)))
        {
            var expiration = TryGetAccountExpiration(result, out var fileTime)
                ? FormatAccountExpirationDate(fileTime)
                : "Never";

            record.Attributes["AccountExpirationDate"] = expiration;
        }

        return record;
    }

    private static bool TryGetUserAccountControl(SearchResult result, out int value)
    {
        value = 0;

        if (!result.Properties.Contains("userAccountControl") || result.Properties["userAccountControl"].Count == 0)
        {
            return false;
        }

        value = Convert.ToInt32(result.Properties["userAccountControl"][0], CultureInfo.InvariantCulture);
        return true;
    }

    private static bool TryGetAccountExpiration(SearchResult result, out long fileTime)
    {
        fileTime = 0;

        if (!result.Properties.Contains("accountExpires") || result.Properties["accountExpires"].Count == 0)
        {
            return false;
        }

        return TryExtractFileTime(result.Properties["accountExpires"][0], out fileTime);
    }

    private static bool TryExtractFileTime(object value, out long fileTime)
    {
        switch (value)
        {
            case long longValue:
                fileTime = longValue;
                return true;
            case int intValue:
                fileTime = intValue;
                return true;
        }

        var type = value.GetType();
        var highPart = type.GetProperty("HighPart");
        var lowPart = type.GetProperty("LowPart");
        if (highPart is not null && lowPart is not null)
        {
            var high = Convert.ToInt64(highPart.GetValue(value, null), CultureInfo.InvariantCulture);
            var low = Convert.ToInt64(lowPart.GetValue(value, null), CultureInfo.InvariantCulture);
            fileTime = (high << 32) + (long)((uint)low);
            return true;
        }

        fileTime = 0;
        return false;
    }

    private static string FormatAccountExpirationDate(long fileTime)
    {
        if (fileTime <= 0 || fileTime == long.MaxValue || fileTime == 9223372036854775807)
        {
            return "Never";
        }

        try
        {
            var utc = DateTime.FromFileTimeUtc(fileTime);
            if (utc <= DateTime.FromFileTimeUtc(0))
            {
                return "Never";
            }

            return utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "Never";
        }
    }

    private static string EscapeLdapValue(string value)
    {
        return value
            .Replace("\\", "\\5c", StringComparison.Ordinal)
            .Replace("*", "\\2a", StringComparison.Ordinal)
            .Replace("(", "\\28", StringComparison.Ordinal)
            .Replace(")", "\\29", StringComparison.Ordinal)
            .Replace("\0", "\\00", StringComparison.Ordinal);
    }

    private static List<string> NormalizeAttributes(IEnumerable<string> attributes)
    {
        var list = attributes?.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
                   ?? new List<string>();

        if (!list.Contains("distinguishedName", StringComparer.OrdinalIgnoreCase))
        {
            list.Insert(0, "distinguishedName");
        }

        if (list.Contains("Enabled", StringComparer.OrdinalIgnoreCase) &&
            !list.Contains("userAccountControl", StringComparer.OrdinalIgnoreCase))
        {
            list.Add("userAccountControl");
        }

        if (list.Contains("AccountExpirationDate", StringComparer.OrdinalIgnoreCase) &&
            !list.Contains("accountExpires", StringComparer.OrdinalIgnoreCase))
        {
            list.Add("accountExpires");
        }

        return list;
    }
}


