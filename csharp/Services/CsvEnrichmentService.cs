using AdQuery.Orchestrator.Models;
using System.Collections;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Executes CSV enrichment plans by iterating through rows and doing AD lookups.
/// </summary>
public interface ICsvEnrichmentService
{
    Task<CsvEnrichmentResult> ExecuteAsync(
        CsvEnrichmentPlan plan,
        List<string> csvHeaders,
        List<List<string>> csvData,
        CancellationToken cancellationToken);
}

public class CsvEnrichmentResult
{
    public bool Success { get; set; }
    public List<Dictionary<string, object?>> Data { get; set; } = new();
    public int TotalRows { get; set; }
    public int MatchedRows { get; set; }
    public int FilteredRows { get; set; }
    public List<string> Warnings { get; set; } = new();
    public List<string> Errors { get; set; } = new();
}

public class CsvEnrichmentService : ICsvEnrichmentService
{
    private readonly ILogger<CsvEnrichmentService> _logger;
    private readonly IConfiguration _configuration;
    private readonly IActiveDirectoryService _adService;

    public CsvEnrichmentService(
        ILogger<CsvEnrichmentService> logger,
        IConfiguration configuration,
        IActiveDirectoryService adService)
    {
        _logger = logger;
        _configuration = configuration;
        _adService = adService;
    }

    public async Task<CsvEnrichmentResult> ExecuteAsync(
        CsvEnrichmentPlan plan,
        List<string> csvHeaders,
        List<List<string>> csvData,
        CancellationToken cancellationToken)
    {
        var result = new CsvEnrichmentResult
        {
            TotalRows = csvData.Count
        };

        // Find the match column index
        var matchColumnIndex = csvHeaders
            .FindIndex(h => h.Equals(plan.MatchColumn, StringComparison.OrdinalIgnoreCase));

        if (matchColumnIndex < 0)
        {
            result.Errors.Add($"Column '{plan.MatchColumn}' not found in CSV headers: {string.Join(", ", csvHeaders)}");
            return result;
        }

        // Validate match attribute
        var validMatchAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "sAMAccountName", "userPrincipalName", "mail", "displayName", "employeeID"
        };

        if (!validMatchAttributes.Contains(plan.MatchAttribute))
        {
            result.Warnings.Add($"Unusual match attribute '{plan.MatchAttribute}', using sAMAccountName");
            plan.MatchAttribute = "sAMAccountName";
        }

        // Build list of attributes to retrieve (always include match attribute for correlation)
        var attributesToFetch = new List<string>(plan.RetrieveAttributes)
        {
            plan.MatchAttribute,
            "distinguishedName"
        };
        attributesToFetch = attributesToFetch.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        // Add filter attribute if specified
        if (plan.Filter != null && !string.IsNullOrWhiteSpace(plan.Filter.Attribute))
        {
            if (!attributesToFetch.Contains(plan.Filter.Attribute, StringComparer.OrdinalIgnoreCase))
            {
                attributesToFetch.Add(plan.Filter.Attribute);
            }
        }

        var notFound = new List<string>();
        var outputRows = new List<Dictionary<string, object?>>();

        // Process each CSV row
        for (int rowIndex = 0; rowIndex < csvData.Count; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var csvRow = csvData[rowIndex];
            var matchValue = matchColumnIndex < csvRow.Count ? csvRow[matchColumnIndex] : "";

            if (string.IsNullOrWhiteSpace(matchValue))
            {
                // Empty identifier - include row with empty AD data if output_mode is "all"
                if (plan.OutputMode.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    outputRows.Add(BuildOutputRow(csvHeaders, csvRow, null, plan.RetrieveAttributes, "Empty identifier"));
                }
                continue;
            }

            // Look up user in AD
            var adUser = await LookupUserAsync(plan.MatchAttribute, matchValue, attributesToFetch, cancellationToken);

            if (adUser == null)
            {
                notFound.Add(matchValue);
                if (plan.OutputMode.Equals("all", StringComparison.OrdinalIgnoreCase))
                {
                    outputRows.Add(BuildOutputRow(csvHeaders, csvRow, null, plan.RetrieveAttributes, "Not found"));
                }
                continue;
            }

            result.MatchedRows++;

            // Apply filter if specified
            if (plan.Filter != null && !string.IsNullOrWhiteSpace(plan.Filter.Attribute))
            {
                var passesFilter = EvaluateFilter(adUser, plan.Filter);
                if (!passesFilter)
                {
                    if (plan.OutputMode.Equals("all", StringComparison.OrdinalIgnoreCase))
                    {
                        outputRows.Add(BuildOutputRow(csvHeaders, csvRow, adUser, plan.RetrieveAttributes, "Filtered out"));
                    }
                    continue;
                }
            }

            result.FilteredRows++;
            outputRows.Add(BuildOutputRow(csvHeaders, csvRow, adUser, plan.RetrieveAttributes, "Matched"));
        }

        result.Data = outputRows;
        result.Success = true;

        if (notFound.Count > 0)
        {
            result.Warnings.Add($"{notFound.Count} of {csvData.Count} users not found in Active Directory");
        }

        _logger.LogInformation("CSV enrichment completed: {Total} rows, {Matched} matched, {Filtered} in output",
            result.TotalRows, result.MatchedRows, result.FilteredRows);

        return result;
    }

    private async Task<Dictionary<string, object?>?> LookupUserAsync(
        string matchAttribute,
        string matchValue,
        List<string> attributes,
        CancellationToken cancellationToken)
    {
        try
        {
            // Build search request
            var request = new DirectorySearchRequest
            {
                TargetType = DirectoryObjectType.User,
                Filters = new List<DirectoryFilter>
                {
                    new DirectoryFilter
                    {
                        Attribute = matchAttribute,
                        Operator = "equals",
                        Value = matchValue
                    }
                },
                Attributes = attributes,
                SizeLimit = 1
            };

            var results = await _adService.SearchAsync(request, cancellationToken);
            var first = results.FirstOrDefault();

            if (first == null) return null;

            // Convert DirectoryRecord to Dictionary
            return first.Attributes;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to lookup user with {Attribute}={Value}", matchAttribute, matchValue);
            return null;
        }
    }

    private static Dictionary<string, object?> BuildOutputRow(
        List<string> csvHeaders,
        List<string> csvRow,
        Dictionary<string, object?>? adUser,
        List<string> retrieveAttributes,
        string status)
    {
        var output = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

        // Add original CSV columns
        for (int i = 0; i < csvHeaders.Count && i < csvRow.Count; i++)
        {
            output[csvHeaders[i]] = csvRow[i];
        }

        // Add AD attributes
        foreach (var attr in retrieveAttributes)
        {
            var adKey = $"AD_{attr}";
            if (adUser != null && adUser.TryGetValue(attr, out var value))
            {
                output[adKey] = FormatAdValue(value);
            }
            else
            {
                output[adKey] = "";
            }
        }

        output["AD_Status"] = status;

        return output;
    }

    private static object? FormatAdValue(object? value)
    {
        if (value == null) return "";

        if (value is byte[] bytes)
        {
            // Convert byte arrays (like GUIDs) to string
            if (bytes.Length == 16)
            {
                return new Guid(bytes).ToString();
            }
            return Convert.ToBase64String(bytes);
        }

        if (value is DateTime dt)
        {
            return dt.ToString("yyyy-MM-dd HH:mm:ss");
        }

        if (value is IEnumerable enumerable and not string)
        {
            var parts = new List<string>();
            foreach (var item in enumerable)
            {
                parts.Add(item?.ToString() ?? "");
            }

            return string.Join("; ", parts);
        }

        return value;
    }

    private static bool EvaluateFilter(Dictionary<string, object?> adUser, CsvEnrichmentFilter filter)
    {
        if (!adUser.TryGetValue(filter.Attribute, out var value))
        {
            return false;
        }

        var candidates = ExtractFilterCandidates(value).ToList();
        if (candidates.Count == 0)
        {
            candidates.Add(string.Empty);
        }

        var filterValue = filter.Value ?? "";
        var filterOperator = (filter.Operator ?? "equals").ToLowerInvariant();

        return filterOperator switch
        {
            "equals" => candidates.Any(candidate => candidate.Equals(filterValue, StringComparison.OrdinalIgnoreCase)),
            "not_equals" => candidates.All(candidate => !candidate.Equals(filterValue, StringComparison.OrdinalIgnoreCase)),
            "contains" => candidates.Any(candidate => candidate.Contains(filterValue, StringComparison.OrdinalIgnoreCase)),
            "not_contains" => candidates.All(candidate => !candidate.Contains(filterValue, StringComparison.OrdinalIgnoreCase)),
            "starts_with" => candidates.Any(candidate => candidate.StartsWith(filterValue, StringComparison.OrdinalIgnoreCase)),
            "ends_with" => candidates.Any(candidate => candidate.EndsWith(filterValue, StringComparison.OrdinalIgnoreCase)),
            _ => candidates.Any(candidate => candidate.Equals(filterValue, StringComparison.OrdinalIgnoreCase))
        };
    }

    private static IEnumerable<string> ExtractFilterCandidates(object? value)
    {
        if (value == null)
        {
            yield break;
        }

        if (value is string s)
        {
            yield return s;
            yield break;
        }

        if (value is byte[] bytes)
        {
            yield return Convert.ToBase64String(bytes);
            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return item?.ToString() ?? string.Empty;
            }
            yield break;
        }

        yield return value.ToString() ?? string.Empty;
    }

    private static string EscapeLdapValue(string value)
    {
        // Escape special LDAP characters
        return value
            .Replace("\\", "\\5c")
            .Replace("*", "\\2a")
            .Replace("(", "\\28")
            .Replace(")", "\\29")
            .Replace("\0", "\\00");
    }
}
