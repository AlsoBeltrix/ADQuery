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

    /// <summary>
    /// LDAP attribute name → the PowerShell/ADSI display name the allow-lists are mostly
    /// written in (F06 Slice 2). Resolution is one hop and one direction: an incoming name not
    /// found verbatim is looked up here and retried under its canonical spelling.
    ///
    /// <para>
    /// <b>This never widens an allow-list.</b> A synonym maps a name to another name; the
    /// resulting name must still be present in the object type's own list, so an attribute the
    /// file does not contain stays refused however it is spelled.
    /// <c>AttributeSynonymsTests.ASynonymNeverWidensTheAllowList</c> is the guard.
    /// </para>
    ///
    /// <para>
    /// Earned by live job `5c1a4abb`, whose retry was refused for asking `l` while the list
    /// holds `City` — the same attribute under the other convention. The files already mix both
    /// (`physicalDeliveryOfficeName` beside `Office`, `sn` beside `Surname`), so this makes an
    /// existing inconsistency uniform rather than introducing a new policy. Keeping the mapping
    /// here rather than doubling every list keeps the files human-readable and puts the whole
    /// correspondence in one reviewable table.
    /// </para>
    /// </summary>
    private static readonly FrozenDictionary<string, string> LdapToDisplayName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["l"] = "City",
            ["st"] = "State",
            ["street"] = "StreetAddress",
            ["streetAddress"] = "StreetAddress",
            ["c"] = "Country",
            ["co"] = "Country",
            ["postOfficeBox"] = "POBox",
            ["postalCode"] = "PostalCode",
            ["facsimileTelephoneNumber"] = "Fax",
            ["telephoneNumber"] = "OfficePhone",
            ["givenName"] = "GivenName",
            // Three spellings: the LDAP `sn` the hardcoded fallback list carries, the lowercase
            // form, and the display name the prompt advertises (f06s2-cr-2).
            ["sn"] = "Surname",
            ["surname"] = "Surname",
            ["displayName"] = "DisplayName",
            ["userPrincipalName"] = "UserPrincipalName",
            ["sAMAccountName"] = "SamAccountName",
            ["distinguishedName"] = "DistinguishedName",
            ["department"] = "Department",
            ["title"] = "Title",
            ["company"] = "Company",
            ["description"] = "Description",
            ["initials"] = "Initials",
            ["manager"] = "Manager",
            ["memberOf"] = "MemberOf",
            ["employeeID"] = "EmployeeID",
            ["employeeNumber"] = "EmployeeNumber",
            ["physicalDeliveryOfficeName"] = "Office",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Every name to the full set of names meaning the same attribute (f06s2-cr-1's sibling
    /// finding, `f06s2-cr-2`).
    ///
    /// A single forward map plus a single reverse map is not enough once an attribute has more
    /// than two spellings. <c>Country</c> has three (<c>c</c>, <c>co</c>, <c>Country</c>) and
    /// <c>Surname</c> has three (<c>sn</c>, <c>surname</c>, <c>Surname</c>), so a reverse
    /// lookup collapsing to one arbitrary partner silently drops the rest: the prompt
    /// advertised <c>sn</c>/<c>Surname</c> while the reverse map paired <c>Surname</c> with
    /// <c>surname</c>, so a fallback allow-list holding only <c>sn</c> refused <c>Surname</c>.
    ///
    /// Equivalence classes remove the direction question entirely: a request is allowed when
    /// <em>any</em> member of its class is on the object type's list. This still cannot widen
    /// the set — every member names the same directory attribute, so admitting the request
    /// grants exactly the access the list already granted under a different spelling.
    /// </summary>
    private static readonly FrozenDictionary<string, FrozenSet<string>> SynonymClasses = BuildSynonymClasses();

    private static FrozenDictionary<string, FrozenSet<string>> BuildSynonymClasses()
    {
        // Group every (ldap, display) pair by its canonical display name, then map each member
        // of the resulting class back to the whole class.
        var classes = LdapToDisplayName
            .GroupBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .Select(group => group
                .Select(pair => pair.Key)
                .Append(group.Key)
                .ToFrozenSet(StringComparer.OrdinalIgnoreCase));

        var result = new Dictionary<string, FrozenSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var equivalents in classes)
        {
            foreach (var name in equivalents)
            {
                result[name] = equivalents;
            }
        }

        return result.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

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
        if (attribute is null || !_allowedAttributes.TryGetValue(objectType, out var attributes))
        {
            return false;
        }

        if (attributes.Contains(attribute))
        {
            return true;
        }

        // F06 Slice 2: the same attribute under any of its other spellings. Every candidate is
        // still checked against THIS object type's list, so nothing absent from the file is
        // admitted and per-type scoping is preserved.
        return SynonymClasses.TryGetValue(attribute, out var equivalents)
            && equivalents.Any(attributes.Contains);
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
