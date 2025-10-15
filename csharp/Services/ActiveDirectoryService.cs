using System.Collections.Concurrent;
using System.Collections.Generic;
using System.DirectoryServices;
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
        var attribute = filter.Attribute;
        var value = EscapeLdapValue(filter.Value ?? string.Empty);

        return filter.Operator.ToLowerInvariant() switch
        {
            "equals" => $"({attribute}={value})",
            "contains" => $"({attribute}=*{value}*)",
            "starts_with" => $"({attribute}={value}*)",
            "ends_with" => $"({attribute}=*{value})",
            _ => $"({attribute}={value})"
        };
    }

    private static SearchScope MapScope(DirectorySearchScope scope) => scope switch
    {
        DirectorySearchScope.Base => SearchScope.Base,
        DirectorySearchScope.OneLevel => SearchScope.OneLevel,
        _ => SearchScope.Subtree
    };

    private static DirectoryRecord MapToRecord(DirectoryObjectType targetType, SearchResult result, IEnumerable<string> attributes)
    {
        var record = new DirectoryRecord
        {
            ObjectType = targetType,
            DistinguishedName = result.Properties["distinguishedName"][0]?.ToString() ?? string.Empty
        };

        foreach (var attribute in attributes)
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

        return record;
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

        return list;
    }
}

