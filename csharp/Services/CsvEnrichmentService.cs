using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using System.Collections;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Executes CSV enrichment plans by iterating through rows and doing AD lookups.
/// </summary>
public interface ICsvEnrichmentService
{
    Task<CsvEnrichmentResult> ExecuteAsync(
        CsvEnrichmentPlan? plan,
        List<string> csvHeaders,
        List<List<string>> csvData,
        CancellationToken cancellationToken);
}

internal enum CsvEnrichmentFailureKind
{
    None,
    Validation,
    DirectoryOperation
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
    internal CsvEnrichmentFailureKind FailureKind { get; set; }
}

public class CsvEnrichmentService : ICsvEnrichmentService
{
    private readonly ILogger<CsvEnrichmentService> _logger;
    private readonly IActiveDirectoryService _adService;
    private readonly ICsvEnrichmentPlanValidator _planValidator;
    private readonly ICsvEnrichmentFilterEvaluator _filterEvaluator;

    public CsvEnrichmentService(
        ILogger<CsvEnrichmentService> logger,
        IActiveDirectoryService adService,
        ICsvEnrichmentPlanValidator planValidator,
        ICsvEnrichmentFilterEvaluator filterEvaluator)
    {
        _logger = logger;
        _adService = adService;
        _planValidator = planValidator;
        _filterEvaluator = filterEvaluator;
    }

    public async Task<CsvEnrichmentResult> ExecuteAsync(
        CsvEnrichmentPlan? plan,
        List<string> csvHeaders,
        List<List<string>> csvData,
        CancellationToken cancellationToken)
    {
        var validation = _planValidator.Validate(plan, csvHeaders);
        var result = new CsvEnrichmentResult
        {
            TotalRows = csvData.Count
        };

        if (!validation.IsValid)
        {
            result.FailureKind = CsvEnrichmentFailureKind.Validation;
            result.Errors.AddRange(validation.Errors);
            return result;
        }

        var executionPlan = validation.ExecutionPlan!;
        var attributesToFetch = executionPlan.AttributesToFetch.ToList();

        var notFoundCount = 0;
        var matchedRows = 0;
        var filteredRows = 0;
        var outputRows = new List<Dictionary<string, object?>>();

        // Process each CSV row
        for (int rowIndex = 0; rowIndex < csvData.Count; rowIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var csvRow = csvData[rowIndex];
            var matchValue = executionPlan.MatchColumnIndex < csvRow.Count
                ? csvRow[executionPlan.MatchColumnIndex]
                : "";

            if (string.IsNullOrWhiteSpace(matchValue))
            {
                // Empty identifier - include row with empty AD data if output_mode is "all"
                if (executionPlan.OutputMode == CsvEnrichmentOutputMode.All)
                {
                    outputRows.Add(BuildOutputRow(
                        csvHeaders,
                        csvRow,
                        null,
                        executionPlan.RetrieveAttributes,
                        "Empty identifier"));
                }
                continue;
            }

            // Look up user in AD
            var lookup = await LookupUserAsync(
                executionPlan.MatchAttribute,
                matchValue,
                attributesToFetch,
                rowIndex,
                cancellationToken);

            if (lookup.Status == DirectoryLookupStatus.OperationalFailure)
            {
                return DirectoryOperationFailure(csvData.Count);
            }

            if (lookup.Status == DirectoryLookupStatus.NotFound)
            {
                notFoundCount++;
                if (executionPlan.OutputMode == CsvEnrichmentOutputMode.All)
                {
                    outputRows.Add(BuildOutputRow(
                        csvHeaders,
                        csvRow,
                        null,
                        executionPlan.RetrieveAttributes,
                        "Not found"));
                }
                continue;
            }

            var adUser = lookup.Attributes!;
            matchedRows++;

            // Apply filter if specified
            if (executionPlan.Filter is not null)
            {
                var passesFilter = _filterEvaluator.Evaluate(
                    adUser,
                    executionPlan.Filter.Attribute,
                    executionPlan.Filter.Operator,
                    executionPlan.Filter.Value);
                if (!passesFilter)
                {
                    if (executionPlan.OutputMode == CsvEnrichmentOutputMode.All)
                    {
                        outputRows.Add(BuildOutputRow(
                            csvHeaders,
                            csvRow,
                            adUser,
                            executionPlan.RetrieveAttributes,
                            "Filtered out"));
                    }
                    continue;
                }
            }

            filteredRows++;
            outputRows.Add(BuildOutputRow(
                csvHeaders,
                csvRow,
                adUser,
                executionPlan.RetrieveAttributes,
                "Matched"));
        }

        result.Data = outputRows;
        result.MatchedRows = matchedRows;
        result.FilteredRows = filteredRows;
        result.Success = true;

        if (notFoundCount > 0)
        {
            result.Warnings.Add($"{notFoundCount} of {csvData.Count} users not found in Active Directory");
        }

        _logger.LogInformation("CSV enrichment completed: {Total} rows, {Matched} matched, {Filtered} in output",
            result.TotalRows, result.MatchedRows, result.FilteredRows);

        return result;
    }

    private async Task<DirectoryLookupResult> LookupUserAsync(
        string matchAttribute,
        string matchValue,
        List<string> attributes,
        int rowIndex,
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

            return first is null
                ? new DirectoryLookupResult(DirectoryLookupStatus.NotFound, null)
                : new DirectoryLookupResult(DirectoryLookupStatus.Found, first.Attributes);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogWarning(
                "CSV enrichment directory lookup failed at row index {RowIndex} using match attribute {MatchAttribute}; exception type {ExceptionType}.",
                rowIndex,
                matchAttribute,
                ex.GetType().FullName ?? ex.GetType().Name);

            return new DirectoryLookupResult(DirectoryLookupStatus.OperationalFailure, null);
        }
    }

    private static CsvEnrichmentResult DirectoryOperationFailure(int totalRows)
    {
        var result = new CsvEnrichmentResult
        {
            TotalRows = totalRows,
            FailureKind = CsvEnrichmentFailureKind.DirectoryOperation
        };
        result.Errors.Add("Active Directory lookup failed. Retry the CSV enrichment.");
        return result;
    }

    private static Dictionary<string, object?> BuildOutputRow(
        List<string> csvHeaders,
        List<string> csvRow,
        Dictionary<string, object?>? adUser,
        IReadOnlyList<string> retrieveAttributes,
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

    private enum DirectoryLookupStatus
    {
        Found,
        NotFound,
        OperationalFailure
    }

    private readonly record struct DirectoryLookupResult(
        DirectoryLookupStatus Status,
        Dictionary<string, object?>? Attributes);
}
