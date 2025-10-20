using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AdQuery.Orchestrator.Controllers;

/// <summary>
/// Main controller for handling AD query requests via execution plans
/// </summary>
[Authorize(Roles = "ANALOG\\ADEXNLQ_Users")]
[ApiController]
[Route("api/[controller]")]
public class QueryController : ControllerBase
{
    private const string OutputRoot = @"E:\WWWOutput";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
    private static readonly Regex InvalidPathChars = new Regex(@"[^\w\.-]", RegexOptions.Compiled);
    private static readonly Regex[] ResultLimitPatterns = new[]
    {
        new Regex(@"\bfirst\s+(?<num>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\btop\s+(?<num>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\btake\s+(?<num>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\blimit(?:ed)?(?:\s+to)?\s+(?<num>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\bup\s+to\s+(?<num>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\bno\s+more\s+than\s+(?<num>\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\bonly\s+(?<num>\d+)\s+(?:users|results|records|entries)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\bshow\s+(?<num>\d+)\s+(?:\w+\s+)*(?:users|results|records|entries)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\breturn\s+(?<num>\d+)\s+(?:\w+\s+)*(?:users|results|records|entries)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new Regex(@"\b(?<num>\d+)\s+(?:users|results|records|entries)\s+(?:only|max|maximum)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    };
    private static readonly HashSet<string> SupportedFormats = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "csv",
        "excel",
        "html",
        "text"
    };

    private readonly ILogger<QueryController> _logger;
    private readonly IClaudeService _claudeService;
    private readonly IDirectoryPlanExecutor _planExecutor;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _configuration;
    private readonly HashSet<string> _licenseAttributeAliases;
    private readonly bool _allowUnlimitedResults;

    public QueryController(
        ILogger<QueryController> logger,
        IClaudeService claudeService,
        IDirectoryPlanExecutor planExecutor,
        IMemoryCache cache,
        IConfiguration configuration)
    {
        _logger = logger;
        _claudeService = claudeService;
        _planExecutor = planExecutor;
        _cache = cache;
        _configuration = configuration;
        _licenseAttributeAliases = BuildLicenseAliasSet(configuration);
        _allowUnlimitedResults = configuration.GetValue("QueryDefaults:AllowUnlimited", false);
    }

    /// <summary>
    /// Processes a natural language query and returns AD data
    /// </summary>
    [HttpPost("execute")]
    public async Task<ActionResult<QueryResponse>> ExecuteQuery([FromBody] QueryRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var requestId = Guid.NewGuid().ToString();
        var timestampUtc = DateTime.UtcNow;
        var samAccountName = GetSamAccountName(HttpContext.User);
        var requestedLimit = ExtractResultLimit(request.Query);
        var userDirectory = GetUserDirectory(OutputRoot, samAccountName);
        var baseFileName = BuildFileBaseName(samAccountName, timestampUtc);
        var logPath = Path.Combine(userDirectory, $"{baseFileName}.log");
        var outputPath = Path.Combine(userDirectory, $"{baseFileName}.csv");
        string? rawModelResponse = null;
        string? modelPlanJson = null;
        string? executedPlanJson = null;

        _logger.LogInformation("Processing query request {RequestId}: {Query}", requestId, request.Query);

        try
        {
            var claudeResponse = await _claudeService.GenerateExecutionPlanAsync(
                request.Query,
                request.Context,
                requestedLimit,
                HttpContext.RequestAborted);

            rawModelResponse = claudeResponse.RawResponse;

            if (!claudeResponse.Success || claudeResponse.Plan == null)
            {
                var errorMessage = $"Failed to generate directory plan: {claudeResponse.ErrorMessage}";
                _logger.LogWarning("Claude failed to generate directory plan for request {RequestId}: {Error}",
                    requestId, claudeResponse.ErrorMessage);

                WriteQueryLog(logPath, timestampUtc, requestId, samAccountName, request.Query, request.Context, success: false, recordCount: 0, warnings: null, errorMessage: errorMessage, resultLimit: requestedLimit, outputPath: null, rawModelResponse: rawModelResponse, modelPlanJson: modelPlanJson, executedPlanJson: executedPlanJson);

                return BadRequest(new QueryResponse
                {
                    Success = false,
                    Error = errorMessage,
                    RequestId = requestId,
                    TokenUsage = claudeResponse.TokenUsage
                });
            }

            var plan = claudeResponse.Plan;

            modelPlanJson = SerializePlan(plan);

            ApplyCustomMappings(plan);

            if (requestedLimit.HasValue && requestedLimit.Value > 0)
            {
                EnsurePlanLimit(plan, requestedLimit.Value);
            }

            executedPlanJson = SerializePlan(plan);

            var executionResult = await _planExecutor.ExecutePlanAsync(plan, HttpContext.RequestAborted);
            var fullRows = executionResult.Data ?? new List<Dictionary<string, object?>>();

            var effectiveLimit = plan.ResultLimit.HasValue && plan.ResultLimit.Value > 0
                ? plan.ResultLimit
                : requestedLimit;

            if (effectiveLimit.HasValue && effectiveLimit.Value > 0 && fullRows.Count > effectiveLimit.Value)
            {
                fullRows = fullRows.Take(effectiveLimit.Value).ToList();
            }

            var previewRows = fullRows.Take(10).Select(CloneDictionary).ToList();

            var response = new QueryResponse
            {
                Success = executionResult.Success,
                Data = previewRows,
                RecordCount = fullRows.Count,
                RequestId = requestId,
                ExecutionTimeMs = executionResult.ExecutionTimeMs,
                StepsExecuted = executionResult.StepsExecuted,
                StepsSkipped = executionResult.StepsSkipped,
                TokenUsage = claudeResponse.TokenUsage
            };

            if (!executionResult.Success)
            {
                response.Error = string.Join("; ", executionResult.Errors);
                response.Warnings = executionResult.Warnings;

                WriteQueryLog(logPath, timestampUtc, requestId, samAccountName, request.Query, request.Context, success: false, recordCount: fullRows.Count, warnings: executionResult.Warnings, errorMessage: response.Error, resultLimit: effectiveLimit, outputPath: null, rawModelResponse: rawModelResponse, modelPlanJson: modelPlanJson, executedPlanJson: executedPlanJson);
            }
            else
            {
                if (executionResult.Warnings.Any())
                {
                    response.Warnings = executionResult.Warnings;
                }

                var headers = DetermineHeaders(fullRows);
                var csvContent = GenerateFileContent(fullRows, headers, "csv");
                System.IO.File.WriteAllBytes(outputPath, csvContent);

                CacheQueryResult(
                    requestId,
                    fullRows,
                    samAccountName,
                    request.Query,
                    request.Context,
                    logPath,
                    outputPath,
                    timestampUtc,
                    effectiveLimit);

                WriteQueryLog(logPath, timestampUtc, requestId, samAccountName, request.Query, request.Context, success: true, recordCount: fullRows.Count, warnings: executionResult.Warnings, errorMessage: null, resultLimit: effectiveLimit, outputPath: outputPath, rawModelResponse: rawModelResponse, modelPlanJson: modelPlanJson, executedPlanJson: executedPlanJson);
            }

            _logger.LogInformation("Query request {RequestId} completed. Success: {Success}, Steps: {Steps}, Time: {Time}ms",
                requestId, response.Success, response.StepsExecuted, response.ExecutionTimeMs);

            return response;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Query request {RequestId} was cancelled", requestId);
            WriteQueryLog(logPath, timestampUtc, requestId, samAccountName, request.Query, request.Context, success: false, recordCount: 0, warnings: null, errorMessage: "Request was cancelled or timed out", resultLimit: requestedLimit, outputPath: null, rawModelResponse: rawModelResponse, modelPlanJson: modelPlanJson, executedPlanJson: executedPlanJson);
            return StatusCode(408, new QueryResponse
            {
                Success = false,
                Error = "Request was cancelled or timed out",
                RequestId = requestId
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error processing query request {RequestId}", requestId);
            WriteQueryLog(logPath, timestampUtc, requestId, samAccountName, request.Query, request.Context, success: false, recordCount: 0, warnings: null, errorMessage: ex.Message, resultLimit: requestedLimit, outputPath: null, rawModelResponse: rawModelResponse, modelPlanJson: modelPlanJson, executedPlanJson: executedPlanJson);
            return StatusCode(500, new QueryResponse
            {
                Success = false,
                Error = "An unexpected error occurred while processing the request",
                RequestId = requestId
            });
        }
    }

    /// <summary>
    /// Validates an execution plan without executing it
    /// </summary>
    [HttpPost("validate")]
    public async Task<ActionResult<ValidationResponse>> ValidatePlan([FromBody] DirectoryQueryPlan plan)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var requestId = Guid.NewGuid().ToString();
        _logger.LogDebug("Validating execution plan {RequestId}: {Description}", requestId, plan.Description);

            try
            {
                ApplyCustomMappings(plan);

                var validationResult = await _planExecutor.ValidatePlanAsync(plan);

            var response = new ValidationResponse
            {
                IsValid = validationResult.IsValid,
                Errors = validationResult.Errors,
                Warnings = validationResult.Warnings,
                Security = validationResult.Security,
                RequestId = requestId
            };

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error validating execution plan {RequestId}", requestId);
            return StatusCode(500, new ValidationResponse
            {
                IsValid = false,
                Errors = new List<string> { "An unexpected error occurred during validation" },
                RequestId = requestId
            });
        }
    }

    /// <summary>
    /// Generates a downloadable artifact for a previously executed query.
    /// </summary>
    [HttpGet("download/{requestId}")]
    public IActionResult Download(string requestId, [FromQuery] string? format = null)
    {
        if (string.IsNullOrWhiteSpace(requestId))
        {
            return BadRequest("Request ID is required.");
        }

        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "csv" : format.Trim().ToLowerInvariant();
        if (!SupportedFormats.Contains(normalizedFormat))
        {
            return BadRequest("Unsupported download format.");
        }

        if (!_cache.TryGetValue(requestId, out CachedQueryResult? cached) || cached is null)
        {
            return NotFound("The requested query results are no longer available.");
        }

        var headers = DetermineHeaders(cached.Rows);
        var metadata = GetFormatMetadata(normalizedFormat);
        var baseFileName = !string.IsNullOrWhiteSpace(cached.BaseFileName)
            ? cached.BaseFileName
            : BuildFileBaseName(cached.SamAccountName, cached.TimestampUtc);
        var fileName = $"{baseFileName}.{metadata.Extension}";

        byte[] fileContent;
        if (normalizedFormat.Equals("csv", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(cached.OutputPath) &&
            System.IO.File.Exists(cached.OutputPath))
        {
            fileContent = System.IO.File.ReadAllBytes(cached.OutputPath);
        }
        else
        {
            fileContent = GenerateFileContent(cached.Rows, headers, normalizedFormat);
        }

        if (!string.IsNullOrWhiteSpace(cached.LogPath))
        {
            AppendDownloadEvent(cached.LogPath, normalizedFormat);
        }

        return File(fileContent, metadata.ContentType, fileName);
    }

    /// <summary>
    /// Gets health status of the orchestrator system
    /// </summary>
    [HttpGet("health")]
    public async Task<ActionResult<HealthResponse>> GetHealth()
    {
        try
        {
            var claudeHealth = await _claudeService.CheckHealthAsync(HttpContext.RequestAborted);

            var response = new HealthResponse
            {
                Overall = claudeHealth.IsHealthy ? "Healthy" : "Unhealthy",
                Claude = new ClaudeHealthStatus
                {
                    IsHealthy = claudeHealth.IsHealthy,
                    ResponseTimeMs = claudeHealth.ResponseTimeMs,
                    JsonParsingWorking = claudeHealth.JsonParsingWorking,
                    LastSuccessfulResponse = claudeHealth.LastSuccessfulResponse,
                    ErrorMessage = claudeHealth.ErrorMessage
                },
                Timestamp = DateTime.UtcNow
            };

            return response;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error checking system health");
            return StatusCode(500, new HealthResponse
            {
                Overall = "Error",
                Timestamp = DateTime.UtcNow
            });
        }
    }

    private void CacheQueryResult(
        string requestId,
        List<Dictionary<string, object?>> rows,
        string? samAccountName,
        string query,
        string? context,
        string logPath,
        string outputPath,
        DateTime timestampUtc,
        int? resultLimit)
    {
        var clonedRows = rows.Select(CloneDictionary).ToList();
        var entry = new CachedQueryResult
        {
            Rows = clonedRows,
            SamAccountName = samAccountName,
            Query = query,
            Context = context,
            TimestampUtc = timestampUtc,
            LogPath = logPath,
            OutputPath = outputPath,
            BaseFileName = Path.GetFileNameWithoutExtension(outputPath) ?? string.Empty,
            ResultLimit = resultLimit
        };

        _cache.Set(requestId, entry, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = CacheDuration
        });
    }

    private static Dictionary<string, object?> CloneDictionary(Dictionary<string, object?> source)
    {
        var clone = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in source)
        {
            clone[kvp.Key] = kvp.Value;
        }
        return clone;
    }

    private static List<string> DetermineHeaders(IEnumerable<Dictionary<string, object?>> rows)
    {
        var headers = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in rows)
        {
            foreach (var key in row.Keys)
            {
                if (seen.Add(key))
                {
                    headers.Add(key);
                }
            }
        }

        return headers;
    }

    private static (string ContentType, string Extension) GetFormatMetadata(string format)
    {
        return format switch
        {
            "csv" => ("text/csv", "csv"),
            "excel" => ("application/vnd.ms-excel", "xls"),
            "html" => ("text/html", "html"),
            "text" => ("text/plain", "txt"),
            _ => ("application/octet-stream", "dat")
        };
    }

    private static byte[] GenerateFileContent(IReadOnlyList<Dictionary<string, object?>> rows, IReadOnlyList<string> headers, string format)
    {
        var effectiveHeaders = headers.Any() ? headers : DetermineHeaders(rows);

        return format switch
        {
            "csv" => Encoding.UTF8.GetBytes(BuildCsv(rows, effectiveHeaders)),
            "excel" => Encoding.UTF8.GetBytes(BuildHtmlTable(rows, effectiveHeaders, includeDocumentWrapper: true)),
            "html" => Encoding.UTF8.GetBytes(BuildHtmlTable(rows, effectiveHeaders, includeDocumentWrapper: true)),
            "text" => Encoding.UTF8.GetBytes(BuildPlainText(rows, effectiveHeaders)),
            _ => Encoding.UTF8.GetBytes(BuildPlainText(rows, effectiveHeaders))
        };
    }

    private static string BuildCsv(IReadOnlyList<Dictionary<string, object?>> rows, IReadOnlyList<string> headers)
    {
        var builder = new StringBuilder();
        if (headers.Any())
        {
            builder.AppendLine(string.Join(",", headers.Select(h => EscapeCsv(h))));
        }

        foreach (var row in rows)
        {
            var values = headers.Select(header =>
            {
                row.TryGetValue(header, out var value);
                return EscapeCsv(FormatCellValue(value));
            });
            builder.AppendLine(string.Join(",", values));
        }

        return builder.ToString();
    }

    private static string BuildHtmlTable(IReadOnlyList<Dictionary<string, object?>> rows, IReadOnlyList<string> headers, bool includeDocumentWrapper)
    {
        var builder = new StringBuilder();

        if (includeDocumentWrapper)
        {
            builder.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\"><title>Directory Query Results</title></head><body>");
        }

        builder.AppendLine("<table border=\"1\" cellspacing=\"0\" cellpadding=\"4\">");

        if (headers.Any())
        {
            builder.AppendLine("<thead><tr>");
            foreach (var header in headers)
            {
                builder.Append("<th>")
                       .Append(System.Net.WebUtility.HtmlEncode(header))
                       .AppendLine("</th>");
            }
            builder.AppendLine("</tr></thead>");
        }

        builder.AppendLine("<tbody>");
        foreach (var row in rows)
        {
            builder.AppendLine("<tr>");
            foreach (var header in headers)
            {
                row.TryGetValue(header, out var value);
                builder.Append("<td>")
                       .Append(System.Net.WebUtility.HtmlEncode(FormatCellValue(value)))
                       .AppendLine("</td>");
            }
            builder.AppendLine("</tr>");
        }

        builder.AppendLine("</tbody></table>");

        if (includeDocumentWrapper)
        {
            builder.AppendLine("</body></html>");
        }

        return builder.ToString();
    }

    private static string BuildPlainText(IReadOnlyList<Dictionary<string, object?>> rows, IReadOnlyList<string> headers)
    {
        var builder = new StringBuilder();
        if (headers.Any())
        {
            builder.AppendLine(string.Join('\t', headers));
        }

        foreach (var row in rows)
        {
            var values = headers.Select(header =>
            {
                row.TryGetValue(header, out var value);
                return FormatCellValue(value);
            });
            builder.AppendLine(string.Join('\t', values));
        }

        return builder.ToString();
    }

    private static string FormatCellValue(object? value)
    {
        switch (value)
        {
            case null:
                return string.Empty;
            case string s:
                return s;
            case DateTime dt:
                return dt.ToString("o");
            case DateTimeOffset dto:
                return dto.ToString("o");
            case bool b:
                return b ? "True" : "False";
            case JsonElement json:
                return FormatJsonElement(json);
            case IEnumerable enumerable when value is not string:
                {
                    var parts = new List<string>();
                    foreach (var item in enumerable)
                    {
                        parts.Add(FormatCellValue(item));
                    }
                    return string.Join(", ", parts.Where(part => !string.IsNullOrEmpty(part)));
                }
            default:
                return Convert.ToString(value) ?? string.Empty;
        }
    }

    private static string FormatJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "True",
            JsonValueKind.False => "False",
            JsonValueKind.Array => string.Join(", ", element.EnumerateArray().Select(FormatJsonElement)),
            _ => element.ToString()
        };
    }

    private static string EscapeCsv(string input)
    {
        if (input.Contains('"') || input.Contains(',') || input.Contains('\n') || input.Contains('\r'))
        {
            return $"\"{input.Replace("\"", "\"\"")}\"";
        }
        return input;
    }

    private static string EscapeForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
    }

    private static string GetSamAccountName(ClaimsPrincipal? user)
    {
        var raw = user?.Identity?.Name;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "unknown";
        }

        var separatorIndex = raw.IndexOf('\\');
        if (separatorIndex >= 0 && separatorIndex < raw.Length - 1)
        {
            return raw[(separatorIndex + 1)..];
        }

        return raw;
    }

    private static string GetUserDirectory(string root, string? samAccountName)
    {
        var accountSegment = SanitizePathSegment(string.IsNullOrWhiteSpace(samAccountName) ? "unknown" : samAccountName!);
        var directory = Path.Combine(root, accountSegment);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static string BuildFileBaseName(string? samAccountName, DateTime timestampUtc)
    {
        var accountSegment = SanitizePathSegment(string.IsNullOrWhiteSpace(samAccountName) ? "unknown" : samAccountName).ToUpperInvariant();
        return $"adquery_{accountSegment}_{timestampUtc:yyyyMMdd_HHmmssfff}";
    }

    private static int? ExtractResultLimit(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        foreach (var pattern in ResultLimitPatterns)
        {
            var match = pattern.Match(query);
            if (match.Success && int.TryParse(match.Groups["num"].Value, out var value) && value > 0)
            {
                return Math.Min(value, 500);
            }
        }

        return null;
    }

    private static void EnsurePlanLimit(DirectoryQueryPlan plan, int limit)
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

    private void ApplyCustomMappings(DirectoryQueryPlan plan)
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

    private static string? SerializePlan(DirectoryQueryPlan? plan)
    {
        if (plan is null)
        {
            return null;
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            };
            return JsonSerializer.Serialize(plan, options);
        }
        catch (Exception ex)
        {
            return $"<serialization_error: {ex.Message}>";
        }
    }

    private static void AppendMultilineSection(StringBuilder builder, string heading, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        builder.AppendLine($"{heading}:");

        var lines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
        foreach (var line in lines)
        {
            builder.Append("  ");
            builder.AppendLine(line);
        }
    }

    private static void WriteQueryLog(
        string logPath,
        DateTime timestampUtc,
        string requestId,
        string? samAccountName,
        string query,
        string? context,
        bool success,
        int recordCount,
        IEnumerable<string>? warnings,
        string? errorMessage,
        int? resultLimit,
        string? outputPath,
        string? rawModelResponse = null,
        string? modelPlanJson = null,
        string? executedPlanJson = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"TimestampUtc: {timestampUtc:o}");
        builder.AppendLine($"RequestId: {requestId}");
        builder.AppendLine($"User: {samAccountName ?? "unknown"}");
        builder.AppendLine($"Success: {success}");
        builder.AppendLine($"Records: {recordCount}");

        if (resultLimit.HasValue && resultLimit.Value > 0)
        {
            builder.AppendLine($"ResultLimit: {resultLimit.Value}");
        }

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            builder.AppendLine($"OutputFile: {outputPath}");
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            builder.AppendLine($"Query: {EscapeForLog(query)}");
        }

        if (!string.IsNullOrWhiteSpace(context))
        {
            builder.AppendLine($"Context: {EscapeForLog(context)}");
        }

        if (warnings != null && warnings.Any())
        {
            builder.AppendLine($"Warnings: {EscapeForLog(string.Join(" | ", warnings))}");
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            builder.AppendLine($"Error: {EscapeForLog(errorMessage)}");
        }

        AppendMultilineSection(builder, "ModelResponseRaw", rawModelResponse);
        AppendMultilineSection(builder, "ModelPlanJson", modelPlanJson);
        AppendMultilineSection(builder, "ExecutedPlanJson", executedPlanJson);

        builder.AppendLine("DownloadHistory:");
        System.IO.File.WriteAllText(logPath, builder.ToString());
    }

    private static void AppendDownloadEvent(string logPath, string format)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        var entry = $"  - {DateTime.UtcNow:o} format={format.ToUpperInvariant()}{Environment.NewLine}";
        System.IO.File.AppendAllText(logPath, entry);
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var sanitized = InvalidPathChars.Replace(value, "_");
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private sealed class CachedQueryResult
    {
        public List<Dictionary<string, object?>> Rows { get; set; } = new();
        public string? SamAccountName { get; set; }
        public string Query { get; set; } = string.Empty;
        public string? Context { get; set; }
        public DateTime TimestampUtc { get; set; }
        public string LogPath { get; set; } = string.Empty;
        public string OutputPath { get; set; } = string.Empty;
        public string BaseFileName { get; set; } = string.Empty;
        public int? ResultLimit { get; set; }
    }
}

/// <summary>
/// Request model for query execution
/// </summary>
public class QueryRequest
{
    /// <summary>
    /// Natural language query to process
    /// </summary>
    [Required]
    [StringLength(1000, MinimumLength = 1)]
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// Optional context about the AD environment
    /// </summary>
    [StringLength(2000)]
    public string? Context { get; set; }

    /// <summary>
    /// Optional HMAC signature for security validation
    /// </summary>
    public string? Signature { get; set; }
}

/// <summary>
/// Response model for query execution
/// </summary>
public class QueryResponse
{
    /// <summary>
    /// Whether the query was executed successfully
    /// </summary>
    public bool Success { get; set; }

    /// <summary>
    /// Query result data
    /// </summary>
    public List<Dictionary<string, object?>> Data { get; set; } = new();

    /// <summary>
    /// Total records returned
    /// </summary>
    public int RecordCount { get; set; }

    /// <summary>
    /// Error message if execution failed
    /// </summary>
    public string? Error { get; set; }

    /// <summary>
    /// Warning messages
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Unique request identifier
    /// </summary>
    public string RequestId { get; set; } = string.Empty;

    /// <summary>
    /// Total execution time in milliseconds
    /// </summary>
    public long ExecutionTimeMs { get; set; }

    /// <summary>
    /// Number of steps executed
    /// </summary>
    public int StepsExecuted { get; set; }

    /// <summary>
    /// Number of steps skipped due to conditions
    /// </summary>
    public int StepsSkipped { get; set; }

    /// <summary>
    /// Token usage statistics from Claude
    /// </summary>
    public TokenUsage TokenUsage { get; set; } = new();
}

/// <summary>
/// Response model for plan validation
/// </summary>
public class ValidationResponse
{
    /// <summary>
    /// Whether the plan is valid
    /// </summary>
    public bool IsValid { get; set; }

    /// <summary>
    /// Validation errors
    /// </summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>
    /// Validation warnings
    /// </summary>
    public List<string> Warnings { get; set; } = new();

    /// <summary>
    /// Security validation results
    /// </summary>
    public PlanSecurityResult Security { get; set; } = new();

    /// <summary>
    /// Unique request identifier
    /// </summary>
    public string RequestId { get; set; } = string.Empty;
}

/// <summary>
/// Response model for health checks
/// </summary>
public class HealthResponse
{
    /// <summary>
    /// Overall system health status
    /// </summary>
    public string Overall { get; set; } = string.Empty;

    /// <summary>
    /// Claude service health status
    /// </summary>
    public ClaudeHealthStatus Claude { get; set; } = new();

    /// <summary>
    /// Health check timestamp
    /// </summary>
    public DateTime Timestamp { get; set; }
}

/// <summary>
/// Claude service health status
/// </summary>
public class ClaudeHealthStatus
{
    /// <summary>
    /// Whether Claude is responding correctly
    /// </summary>
    public bool IsHealthy { get; set; }

    /// <summary>
    /// Response time for health check
    /// </summary>
    public long ResponseTimeMs { get; set; }

    /// <summary>
    /// Whether JSON parsing is working correctly
    /// </summary>
    public bool JsonParsingWorking { get; set; }

    /// <summary>
    /// Last successful response time
    /// </summary>
    public DateTime? LastSuccessfulResponse { get; set; }

    /// <summary>
    /// Error message if health check failed
    /// </summary>
    public string? ErrorMessage { get; set; }
}







