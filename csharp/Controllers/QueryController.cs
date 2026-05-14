using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using AdQuery.Orchestrator.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;

namespace AdQuery.Orchestrator.Controllers;

/// <summary>
/// Main controller for handling AD query requests via execution plans
/// </summary>
[Authorize(Roles = "ANALOG\\ADEXNLQ_Users")]
[ApiController]
[Route("api/[controller]")]
public class QueryController : ControllerBase
{
    private const string DefaultAlternateModel = "@vertexai-global/anthropic.claude-opus-4-1@20250805";
    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(30);
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
    private readonly IQueryJobManager _jobManager;
    private readonly IPlanPreprocessor _planPreprocessor;
    private readonly IPlanValidator _planValidator;
    private readonly ICsvEnrichmentService _csvEnrichmentService;
    private readonly string _defaultModelId;
    private readonly string _defaultModelDisplayName;
    private readonly string _alternateModelId;
    private readonly string _alternateModelDisplayName;

    public QueryController(
        ILogger<QueryController> logger,
        IClaudeService claudeService,
        IDirectoryPlanExecutor planExecutor,
        IMemoryCache cache,
        IConfiguration configuration,
        IQueryJobManager jobManager,
        IPlanPreprocessor planPreprocessor,
        IPlanValidator planValidator,
        ICsvEnrichmentService csvEnrichmentService)
    {
        _logger = logger;
        _claudeService = claudeService;
        _planExecutor = planExecutor;
        _cache = cache;
        _configuration = configuration;
        _jobManager = jobManager;
        _planPreprocessor = planPreprocessor;
        _planValidator = planValidator;
        _csvEnrichmentService = csvEnrichmentService;

        _defaultModelId = configuration.GetValue<string>("Claude:Model", "claude-3-sonnet-20240229")!;
        _defaultModelDisplayName = DeriveModelDisplayName(_defaultModelId);

        _alternateModelId = configuration.GetValue<string>("Claude:AlternateModel", DefaultAlternateModel)!;
        var derivedAltDisplayName = DeriveModelDisplayName(_alternateModelId);
        _alternateModelDisplayName = configuration.GetValue<string>("Claude:AlternateModelDisplayName", derivedAltDisplayName)!;
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
        var maxResults = _configuration.GetValue<int>("QueryDefaults:MaxResults", 0);
        var requestedLimit = maxResults > 0 ? (int?)maxResults : null;
        var userDirectory = QueryLogHelper.GetUserDirectory(samAccountName);
        var baseFileName = QueryLogHelper.BuildFileBaseName(samAccountName, timestampUtc);
        var logPath = Path.Combine(userDirectory, $"{baseFileName}.log");
        var outputPath = Path.Combine(userDirectory, $"{baseFileName}.csv");
        string? rawModelResponse = null;
        string? modelPlanJson = null;
        string? executedPlanJson = null;
        string? modelUsed = null;

        _logger.LogInformation("Processing query request {RequestId}: {Query}", requestId, request.Query);

        try
        {
            var claudeResponse = await _claudeService.GenerateExecutionPlanAsync(
                request.Query,
                request.Context,
                requestedLimit,
                HttpContext.RequestAborted);

            rawModelResponse = claudeResponse.RawResponse;
            modelUsed = claudeResponse.ModelUsed;

            if (!claudeResponse.Success || claudeResponse.Plan == null)
            {
                var errorMessage = $"Failed to generate directory plan: {claudeResponse.ErrorMessage}";
                _logger.LogWarning("Claude failed to generate directory plan for request {RequestId}: {Error}",
                    requestId, claudeResponse.ErrorMessage);

                QueryLogHelper.WriteQueryLog(logPath, timestampUtc, requestId, samAccountName, request.Query, request.Context, success: false, recordCount: 0, warnings: null, errorMessage: errorMessage, resultLimit: requestedLimit, outputPath: null, rawModelResponse: rawModelResponse, modelPlanJson: modelPlanJson, executedPlanJson: executedPlanJson, modelUsed: modelUsed);

                return BadRequest(new QueryResponse
                {
                    Success = false,
                    Error = errorMessage,
                    RequestId = requestId,
                    TokenUsage = claudeResponse.TokenUsage
                });
            }

            var plan = claudeResponse.Plan;

            modelPlanJson = QueryLogHelper.SerializePlan(plan);

            _planPreprocessor.PrepareForExecution(plan, requestedLimit);

            executedPlanJson = QueryLogHelper.SerializePlan(plan);

            var executionResult = await _planExecutor.ExecutePlanAsync(plan, HttpContext.RequestAborted);
            var fullRows = executionResult.Data ?? new List<Dictionary<string, object?>>();

            var effectiveLimit = plan.ResultLimit.HasValue && plan.ResultLimit.Value > 0
                ? plan.ResultLimit
                : requestedLimit;

            if (effectiveLimit.HasValue && effectiveLimit.Value > 0 && fullRows.Count > effectiveLimit.Value)
            {
                fullRows = fullRows.Take(effectiveLimit.Value).ToList();
            }

            var previewRowCount = _configuration.GetValue<int>("QueryDefaults:PreviewRowCount", 10);
            var previewRows = fullRows.Take(previewRowCount).Select(CloneDictionary).ToList();

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

                QueryLogHelper.WriteQueryLog(logPath, timestampUtc, requestId, samAccountName, request.Query, request.Context, success: false, recordCount: fullRows.Count, warnings: executionResult.Warnings, errorMessage: response.Error, resultLimit: effectiveLimit, outputPath: null, rawModelResponse: rawModelResponse, modelPlanJson: modelPlanJson, executedPlanJson: executedPlanJson, modelUsed: modelUsed);
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

                QueryLogHelper.WriteQueryLog(logPath, timestampUtc, requestId, samAccountName, request.Query, request.Context, success: true, recordCount: fullRows.Count, warnings: executionResult.Warnings, errorMessage: null, resultLimit: effectiveLimit, outputPath: outputPath, rawModelResponse: rawModelResponse, modelPlanJson: modelPlanJson, executedPlanJson: executedPlanJson, modelUsed: modelUsed);
            }

            _logger.LogInformation("Query request {RequestId} completed. Success: {Success}, Steps: {Steps}, Time: {Time}ms",
                requestId, response.Success, response.StepsExecuted, response.ExecutionTimeMs);

            return response;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Query request {RequestId} was cancelled", requestId);
            QueryLogHelper.WriteQueryLog(logPath, timestampUtc, requestId, samAccountName, request.Query, request.Context, success: false, recordCount: 0, warnings: null, errorMessage: "Request was cancelled or timed out", resultLimit: requestedLimit, outputPath: null, rawModelResponse: rawModelResponse, modelPlanJson: modelPlanJson, executedPlanJson: executedPlanJson, modelUsed: modelUsed);
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
            QueryLogHelper.WriteQueryLog(logPath, timestampUtc, requestId, samAccountName, request.Query, request.Context, success: false, recordCount: 0, warnings: null, errorMessage: ex.Message, resultLimit: requestedLimit, outputPath: null, rawModelResponse: rawModelResponse, modelPlanJson: modelPlanJson, executedPlanJson: executedPlanJson, modelUsed: modelUsed);
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
    public async Task<ActionResult<ValidationResponse>> ValidatePlan(
        [FromBody] DirectoryQueryPlan plan,
        [FromHeader(Name = "X-Plan-Signature")] string? signature = null)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var requestId = Guid.NewGuid().ToString();
        _logger.LogDebug("Validating execution plan {RequestId}: {Description}", requestId, plan.Description);

            try
            {
                if (_configuration.GetValue<bool>("Security:EnableHmacValidation"))
                {
                    if (string.IsNullOrWhiteSpace(signature) || !_planValidator.ValidateHmac(plan, signature))
                    {
                        return Unauthorized(new ValidationResponse
                        {
                            IsValid = false,
                            Errors = new List<string> { "Plan signature validation failed." },
                            Security = new PlanSecurityResult { HmacValid = false },
                            RequestId = requestId
                        });
                    }
                }

                _planPreprocessor.ApplyCustomMappings(plan);

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

        var currentUser = GetSamAccountName(HttpContext.User);
        if (!string.IsNullOrWhiteSpace(cached.SamAccountName) &&
            !string.Equals(cached.SamAccountName, currentUser, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning("User {User} attempted to download results owned by {Owner} for request {RequestId}", currentUser, cached.SamAccountName, requestId);
            return Forbid();
        }

        var headers = DetermineHeaders(cached.Rows);
        var metadata = GetFormatMetadata(normalizedFormat);
        var baseFileName = !string.IsNullOrWhiteSpace(cached.BaseFileName)
            ? cached.BaseFileName
            : QueryLogHelper.BuildFileBaseName(cached.SamAccountName, cached.TimestampUtc);
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
            QueryLogHelper.AppendDownloadEvent(cached.LogPath, normalizedFormat);
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

    /// <summary>
    /// Gets client configuration settings
    /// </summary>
    [HttpGet("config")]
    public IActionResult GetConfig()
    {
        return Ok(new
        {
            previewRowCount = _configuration.GetValue<int>("QueryDefaults:PreviewRowCount", 10),
            summaryRowCount = _configuration.GetValue<int>("QueryDefaults:SummaryRowCount", 20),
            defaultModelId = _defaultModelId,
            defaultModelDisplayName = _defaultModelDisplayName,
            alternateModelId = _alternateModelId,
            alternateModelDisplayName = _alternateModelDisplayName
        });
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
            "excel" => ("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "xlsx"),
            "html" => ("text/html", "html"),
            "text" => ("text/plain", "txt"),
            _ => ("application/octet-stream", "dat")
        };
    }

    private static byte[] GenerateFileContent(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<string> headers,
        string format,
        Dictionary<string, object>? aggregation = null,
        List<string>? warnings = null,
        QueryMetadata? metadata = null)
    {
        var effectiveHeaders = headers.Any() ? headers : DetermineHeaders(rows);

        return format switch
        {
            "csv" => Encoding.UTF8.GetBytes(BuildCsv(rows, effectiveHeaders, aggregation, warnings, metadata)),
            "excel" => BuildExcelBytes(rows, effectiveHeaders, aggregation, warnings, metadata),
            "html" => Encoding.UTF8.GetBytes(BuildHtmlTable(rows, effectiveHeaders, aggregation, warnings, metadata, includeDocumentWrapper: true)),
            "text" => Encoding.UTF8.GetBytes(BuildPlainText(rows, effectiveHeaders, aggregation, warnings, metadata)),
            _ => Encoding.UTF8.GetBytes(BuildPlainText(rows, effectiveHeaders, aggregation, warnings, metadata))
        };
    }

    private static string BuildCsv(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<string> headers,
        Dictionary<string, object>? aggregation = null,
        List<string>? warnings = null,
        QueryMetadata? metadata = null)
    {
        var builder = new StringBuilder();

        // Add query metadata as comments
        if (metadata != null)
        {
            builder.AppendLine($"# Query: {EscapeCsv(metadata.Query)}");
            builder.AppendLine($"# User: {metadata.User}");
            builder.AppendLine($"# Model: {metadata.Model ?? "unknown"}");
            builder.AppendLine($"# Timestamp: {metadata.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
            builder.AppendLine($"# Records: {metadata.RecordCount:N0}");
            builder.AppendLine("#");
        }

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

        // Add aggregation summary as comments
        if (aggregation != null && aggregation.Any())
        {
            builder.AppendLine();
            builder.AppendLine("# SUMMARY");

            if (aggregation.ContainsKey("grouped_counts"))
            {
                var counts = aggregation["grouped_counts"] as Dictionary<string, int>;
                if (counts != null)
                {
                    builder.AppendLine("# Category,Count");
                    foreach (var (key, count) in counts.OrderByDescending(kvp => kvp.Value))
                    {
                        builder.AppendLine($"# {EscapeCsv(key)},{count}");
                    }
                }
            }

            if (aggregation.ContainsKey("level_metadata"))
            {
                builder.AppendLine("#");
                builder.AppendLine("# HIERARCHY DEPTH");
                var levels = aggregation["level_metadata"] as Dictionary<int, int>;
                if (levels != null)
                {
                    builder.AppendLine("# Level,Count");
                    foreach (var (level, count) in levels.OrderBy(kvp => kvp.Key))
                    {
                        builder.AppendLine($"# Level {level},{count}");
                    }
                }
            }
        }

        // Add warnings as comments
        if (warnings != null && warnings.Any())
        {
            builder.AppendLine();
            builder.AppendLine("# WARNINGS");
            foreach (var warning in warnings)
            {
                builder.AppendLine($"# {EscapeCsv(warning)}");
            }
        }

        return builder.ToString();
    }

    private static string BuildHtmlTable(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<string> headers,
        Dictionary<string, object>? aggregation,
        List<string>? warnings,
        QueryMetadata? metadata,
        bool includeDocumentWrapper)
    {
        var builder = new StringBuilder();

        if (includeDocumentWrapper)
        {
            builder.AppendLine("<!DOCTYPE html><html><head><meta charset=\"utf-8\">");
            builder.AppendLine("<title>Active Directory Query Results</title>");
            builder.AppendLine("<style>");
            builder.AppendLine("body { font-family: 'Segoe UI', Arial, sans-serif; margin: 0; padding: 20px; background: #f5f5f5; color: #333; }");
            builder.AppendLine(".container { max-width: 1200px; margin: 0 auto; background: white; padding: 30px; border-radius: 8px; box-shadow: 0 2px 4px rgba(0,0,0,0.1); }");
            builder.AppendLine("h1 { color: #2c3e50; margin: 0 0 10px; font-size: 24px; }");
            builder.AppendLine("h2 { color: #34495e; margin: 30px 0 15px; font-size: 18px; border-bottom: 2px solid #3498db; padding-bottom: 8px; }");
            builder.AppendLine(".metadata { background: #ecf0f1; padding: 15px; border-radius: 4px; margin-bottom: 20px; font-size: 14px; }");
            builder.AppendLine(".metadata-row { margin: 5px 0; }");
            builder.AppendLine(".label { font-weight: bold; color: #555; min-width: 120px; display: inline-block; }");
            builder.AppendLine("table { border-collapse: collapse; margin: 15px 0; width: 100%; }");
            builder.AppendLine("th, td { border: 1px solid #ddd; padding: 10px; text-align: left; }");
            builder.AppendLine("th { background: linear-gradient(to bottom, #3498db, #2980b9); color: white; font-weight: 600; font-size: 13px; }");
            builder.AppendLine("tbody tr:nth-child(even) { background: #f9f9f9; }");
            builder.AppendLine("tbody tr:hover { background: #e3f2fd; }");
            builder.AppendLine(".summary-table { max-width: 500px; }");
            builder.AppendLine(".summary-table td:last-child { text-align: right; font-weight: bold; }");
            builder.AppendLine(".warning { background: #fff3cd; padding: 15px; margin: 15px 0; border-left: 4px solid #ffc107; border-radius: 4px; }");
            builder.AppendLine(".warning strong { color: #856404; }");
            builder.AppendLine(".footer { margin-top: 30px; padding-top: 20px; border-top: 1px solid #ddd; color: #777; font-size: 12px; text-align: center; }");
            builder.AppendLine("</style>");
            builder.AppendLine("</head><body><div class=\"container\">");

            // Add header with query metadata
            if (metadata != null)
            {
                builder.AppendLine("<h1>Active Directory Query Results</h1>");
                builder.AppendLine("<div class=\"metadata\">");
                builder.AppendLine($"<div class=\"metadata-row\"><span class=\"label\">Query:</span> {System.Net.WebUtility.HtmlEncode(metadata.Query)}</div>");
                builder.AppendLine($"<div class=\"metadata-row\"><span class=\"label\">User:</span> {System.Net.WebUtility.HtmlEncode(metadata.User)}</div>");
                builder.AppendLine($"<div class=\"metadata-row\"><span class=\"label\">Model:</span> {System.Net.WebUtility.HtmlEncode(metadata.Model ?? "unknown")}</div>");
                builder.AppendLine($"<div class=\"metadata-row\"><span class=\"label\">Generated:</span> {metadata.Timestamp:yyyy-MM-dd HH:mm:ss} UTC</div>");
                builder.AppendLine($"<div class=\"metadata-row\"><span class=\"label\">Total Records:</span> {metadata.RecordCount:N0}</div>");
                builder.AppendLine("</div>");
            }
        }

        // Add aggregation summary
        if (aggregation != null && aggregation.Any())
        {
            builder.AppendLine("<h2>Summary</h2>");

            if (aggregation.ContainsKey("grouped_counts"))
            {
                var counts = aggregation["grouped_counts"] as Dictionary<string, int>;
                if (counts != null && counts.Any())
                {
                    builder.AppendLine("<table class=\"summary-table\">");
                    builder.AppendLine("<thead><tr><th>Category</th><th>Count</th></tr></thead>");
                    builder.AppendLine("<tbody>");
                    foreach (var (key, count) in counts.OrderByDescending(kvp => kvp.Value))
                    {
                        builder.Append("<tr><td>")
                               .Append(System.Net.WebUtility.HtmlEncode(key))
                               .Append("</td><td>")
                               .Append(count.ToString("N0"))
                               .AppendLine("</td></tr>");
                    }
                    builder.AppendLine("</tbody></table>");
                }
            }
        }

        // Add warnings
        if (warnings != null && warnings.Any())
        {
            builder.AppendLine("<div class=\"warning\">");
            builder.AppendLine("<strong>⚠ Warnings:</strong><ul>");
            foreach (var warning in warnings)
            {
                builder.Append("<li>").Append(System.Net.WebUtility.HtmlEncode(warning)).AppendLine("</li>");
            }
            builder.AppendLine("</ul></div>");
        }

        // Add data table
        builder.AppendLine("<h2>Data</h2>");
        builder.AppendLine("<table>");

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
            builder.AppendLine("<div class=\"footer\">Generated by ADQuery Orchestrator</div>");
            builder.AppendLine("</div></body></html>");
        }

        return builder.ToString();
    }

    private static string BuildPlainText(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<string> headers,
        Dictionary<string, object>? aggregation = null,
        List<string>? warnings = null,
        QueryMetadata? metadata = null)
    {
        var builder = new StringBuilder();

        // Add query metadata header
        if (metadata != null)
        {
            builder.AppendLine("ACTIVE DIRECTORY QUERY RESULTS");
            builder.AppendLine("==============================");
            builder.AppendLine();
            builder.AppendLine($"Query:     {metadata.Query}");
            builder.AppendLine($"User:      {metadata.User}");
            builder.AppendLine($"Model:     {metadata.Model ?? "unknown"}");
            builder.AppendLine($"Generated: {metadata.Timestamp:yyyy-MM-dd HH:mm:ss} UTC");
            builder.AppendLine($"Records:   {metadata.RecordCount:N0}");
            builder.AppendLine();
        }

        // Add aggregation summary
        if (aggregation != null && aggregation.Any())
        {
            builder.AppendLine("SUMMARY");
            builder.AppendLine("=======");
            builder.AppendLine();

            if (aggregation.ContainsKey("grouped_counts"))
            {
                var counts = aggregation["grouped_counts"] as Dictionary<string, int>;
                if (counts != null && counts.Any())
                {
                    builder.AppendLine("Category\tCount");
                    builder.AppendLine("--------\t-----");
                    foreach (var (key, count) in counts.OrderByDescending(kvp => kvp.Value))
                    {
                        builder.AppendLine($"{key}\t{count:N0}");
                    }
                    builder.AppendLine();
                }
            }
        }

        // Add warnings
        if (warnings != null && warnings.Any())
        {
            builder.AppendLine("WARNINGS");
            builder.AppendLine("========");
            foreach (var warning in warnings)
            {
                builder.AppendLine($"- {warning}");
            }
            builder.AppendLine();
        }

        // Add data
        builder.AppendLine("DATA");
        builder.AppendLine("====");
        builder.AppendLine();

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

    private static byte[] BuildExcelBytes(
        IReadOnlyList<Dictionary<string, object?>> rows,
        IReadOnlyList<string> headers,
        Dictionary<string, object>? aggregation = null,
        List<string>? warnings = null,
        QueryMetadata? metadata = null)
    {
        using var workbook = new XLWorkbook();

        // Info sheet (if metadata exists)
        if (metadata != null)
        {
            var infoSheet = workbook.Worksheets.Add("Info");
            int row = 1;

            infoSheet.Cell(row, 1).Value = "Active Directory Query Results";
            infoSheet.Cell(row, 1).Style.Font.Bold = true;
            infoSheet.Cell(row, 1).Style.Font.FontSize = 14;
            row += 2;

            infoSheet.Cell(row, 1).Value = "Query:";
            infoSheet.Cell(row, 1).Style.Font.Bold = true;
            infoSheet.Cell(row, 2).Value = metadata.Query;
            row++;

            infoSheet.Cell(row, 1).Value = "User:";
            infoSheet.Cell(row, 1).Style.Font.Bold = true;
            infoSheet.Cell(row, 2).Value = metadata.User;
            row++;

            infoSheet.Cell(row, 1).Value = "Model:";
            infoSheet.Cell(row, 1).Style.Font.Bold = true;
            infoSheet.Cell(row, 2).Value = metadata.Model ?? "unknown";
            row++;

            infoSheet.Cell(row, 1).Value = "Generated:";
            infoSheet.Cell(row, 1).Style.Font.Bold = true;
            infoSheet.Cell(row, 2).Value = metadata.Timestamp.ToString("yyyy-MM-dd HH:mm:ss UTC");
            row++;

            infoSheet.Cell(row, 1).Value = "Total Records:";
            infoSheet.Cell(row, 1).Style.Font.Bold = true;
            infoSheet.Cell(row, 2).Value = metadata.RecordCount;

            infoSheet.Columns().AdjustToContents();
        }

        // Summary sheet (if aggregation exists)
        if (aggregation != null && aggregation.Any() && aggregation.ContainsKey("grouped_counts"))
        {
            var counts = aggregation["grouped_counts"] as Dictionary<string, int>;
            if (counts != null && counts.Any())
            {
                var summarySheet = workbook.Worksheets.Add("Summary");
                int row = 1;

                // Headers
                summarySheet.Cell(row, 1).Value = "Category";
                summarySheet.Cell(row, 2).Value = "Count";
                summarySheet.Range(row, 1, row, 2).Style.Font.Bold = true;
                summarySheet.Range(row, 1, row, 2).Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
                summarySheet.Range(row, 1, row, 2).Style.Font.FontColor = XLColor.White;
                row++;

                // Data
                foreach (var (key, count) in counts.OrderByDescending(kvp => kvp.Value))
                {
                    summarySheet.Cell(row, 1).Value = key;
                    summarySheet.Cell(row, 2).Value = count;
                    row++;
                }

                summarySheet.Columns().AdjustToContents();
            }
        }

        // Data sheet
        var dataSheet = workbook.Worksheets.Add("Data");
        int dataRow = 1;

        // Headers
        if (headers.Any())
        {
            for (int col = 0; col < headers.Count; col++)
            {
                dataSheet.Cell(dataRow, col + 1).Value = headers[col];
            }
            dataSheet.Range(dataRow, 1, dataRow, headers.Count).Style.Font.Bold = true;
            dataSheet.Range(dataRow, 1, dataRow, headers.Count).Style.Fill.BackgroundColor = XLColor.FromHtml("#4472C4");
            dataSheet.Range(dataRow, 1, dataRow, headers.Count).Style.Font.FontColor = XLColor.White;
            dataRow++;
        }

        // Data rows
        foreach (var row in rows)
        {
            for (int col = 0; col < headers.Count; col++)
            {
                row.TryGetValue(headers[col], out var value);
                var cellValue = FormatCellValue(value);

                if (double.TryParse(cellValue, out var numericValue))
                {
                    dataSheet.Cell(dataRow, col + 1).Value = numericValue;
                }
                else
                {
                    dataSheet.Cell(dataRow, col + 1).Value = cellValue;
                }
            }
            dataRow++;
        }

        dataSheet.Columns().AdjustToContents();

        // Save to memory stream and return as bytes
        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return stream.ToArray();
    }

    private static bool IsNumeric(string value)
    {
        return !string.IsNullOrWhiteSpace(value) && double.TryParse(value, out _);
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

    /// <summary>
    /// Creates an async query job (returns immediately with jobId for polling)
    /// </summary>
    [HttpPost("execute-async")]
    public async Task<IActionResult> ExecuteQueryAsync([FromBody] QueryRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userName = GetSamAccountName(HttpContext.User);
        var maxResults = _configuration.GetValue<int>("QueryDefaults:MaxResults", 0);
        var requestedLimit = maxResults > 0 ? (int?)maxResults : null;
        var context = request.Context;

        try
        {
            var jobId = await _jobManager.CreateJobAsync(
                userName,
                request.Query,
                context,
                requestedLimit,
                HttpContext.RequestAborted);

            _logger.LogInformation("Async query job {JobId} created for user {UserName}", jobId, userName);

            return Accepted(new
            {
                jobId,
                statusUrl = $"/api/query/jobs/{jobId}",
                message = "Query job created. Poll status endpoint for progress."
            });
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning("User {UserName} exceeded async job limits: {Message}", userName, ex.Message);
            return BadRequest(new { error = ex.Message });
        }
    }

    /// <summary>
    /// Gets status and results for an async query job
    /// </summary>
    [HttpGet("jobs/{jobId}")]
    public IActionResult GetJobStatus(string jobId)
    {
        var job = _jobManager.GetJob(jobId);
        if (job == null)
        {
            return NotFound(new { error = "Job not found" });
        }

        var userName = GetSamAccountName(HttpContext.User);
        if (!job.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var response = new
        {
            jobId = job.JobId,
            status = job.Status.ToString().ToLower(),
            query = job.Query,
            modelUsed = job.ModelUsed,
            createdAt = job.CreatedAt,
            startedAt = job.StartedAt,
            completedAt = job.CompletedAt,
            responseTimeMs = job.CompletedAt.HasValue && job.StartedAt.HasValue
                ? (long)(job.CompletedAt.Value - job.StartedAt.Value).TotalMilliseconds
                : 0,
            progress = job.Status == JobStatus.Running || job.Status == JobStatus.Queued ? new
            {
                nodesProcessed = job.NodesProcessed,
                currentDepth = job.CurrentDepth,
                estimatedTotal = job.EstimatedTotal,
                phase = job.Phase,
                percentComplete = job.EstimatedTotal > 0
                    ? (int)((job.NodesProcessed / (double)job.EstimatedTotal) * 100)
                    : 0
            } : null,
            result = job.Status == JobStatus.Completed ? new
            {
                totalRows = job.TotalRows,
                aggregation = BuildAggregationSummary(job),
                warnings = job.Warnings.Any() ? job.Warnings : null,
                downloadUrl = $"/api/query/download-async/{job.JobId}"
            } : null,
            error = job.Status == JobStatus.Failed ? job.ErrorMessage : null
        };

        return Ok(response);
    }

    /// <summary>
    /// Gets preview rows (first 10) from a completed async job
    /// </summary>
    [HttpGet("jobs/{jobId}/preview")]
    public IActionResult GetJobPreview(string jobId)
    {
        var job = _jobManager.GetJob(jobId);
        if (job == null)
        {
            return NotFound(new { error = "Job not found" });
        }

        var userName = GetSamAccountName(HttpContext.User);
        if (!job.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        if (job.Status != JobStatus.Completed)
        {
            return BadRequest(new { error = $"Job status is {job.Status.ToString().ToLower()}, not completed" });
        }

        if (string.IsNullOrWhiteSpace(job.ResultsCacheKey) ||
            !_cache.TryGetValue(job.ResultsCacheKey, out PlanExecutionResult? result) ||
            result == null)
        {
            return NotFound(new { error = "Results expired or not available" });
        }

        var previewRowCount = _configuration.GetValue<int>("QueryDefaults:PreviewRowCount", 10);
        var previewRows = result.Data.Take(previewRowCount).ToList();

        return Ok(new
        {
            rows = previewRows,
            totalRows = result.Data.Count,
            hasMore = result.Data.Count > 10
        });
    }

    /// <summary>
    /// Cancels a running async query job
    /// </summary>
    [HttpPost("jobs/{jobId}/cancel")]
    public IActionResult CancelJob(string jobId)
    {
        var job = _jobManager.GetJob(jobId);
        if (job == null)
        {
            return NotFound(new { error = "Job not found" });
        }

        var userName = GetSamAccountName(HttpContext.User);
        if (!job.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        if (job.Status != JobStatus.Running && job.Status != JobStatus.Queued)
        {
            return BadRequest(new { error = $"Job is {job.Status.ToString().ToLower()}, cannot cancel" });
        }

        _jobManager.CancelJob(jobId);
        return Ok(new { message = "Cancellation requested" });
    }

    /// <summary>
    /// Downloads results from a completed async job
    /// </summary>
    [HttpGet("download-async/{jobId}")]
    public IActionResult DownloadAsync(string jobId, [FromQuery] string? format = null)
    {
        if (string.IsNullOrWhiteSpace(jobId))
        {
            return BadRequest("Job ID is required.");
        }

        var job = _jobManager.GetJob(jobId);
        if (job == null)
        {
            return NotFound(new { error = "Job not found" });
        }

        var userName = GetSamAccountName(HttpContext.User);
        if (!job.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        if (job.Status != JobStatus.Completed)
        {
            return BadRequest(new { error = $"Job status is {job.Status.ToString().ToLower()}, not completed" });
        }

        if (string.IsNullOrWhiteSpace(job.ResultsCacheKey) ||
            !_cache.TryGetValue(job.ResultsCacheKey, out PlanExecutionResult? result) ||
            result == null)
        {
            return NotFound(new { error = "Results expired or not available" });
        }

        var normalizedFormat = string.IsNullOrWhiteSpace(format) ? "csv" : format.Trim().ToLowerInvariant();
        if (!SupportedFormats.Contains(normalizedFormat))
        {
            return BadRequest("Unsupported download format.");
        }

        var headers = DetermineHeaders(result.Data);
        var metadata = GetFormatMetadata(normalizedFormat);
        var timestampUtc = DateTime.UtcNow;
        var userDirectory = QueryLogHelper.GetUserDirectory(userName);
        var baseFileName = QueryLogHelper.BuildFileBaseName(userName, timestampUtc);
        var fileName = $"{baseFileName}.{metadata.Extension}";
        var outputPath = Path.Combine(userDirectory, fileName);

        var queryMetadata = new QueryMetadata
        {
            Query = job.Query,
            User = userName,
            Timestamp = timestampUtc,
            RecordCount = result.Data.Count,
            Model = job.ModelUsed
        };

        var fileContent = GenerateFileContent(result.Data, headers, normalizedFormat, job.Aggregation, result.Warnings, queryMetadata);

        // Save to E:\WWWOutput for audit trail
        System.IO.File.WriteAllBytes(outputPath, fileContent);

        // Log download event (create log if doesn't exist for this job)
        var logPath = Path.Combine(userDirectory, $"{baseFileName}.log");
        if (!System.IO.File.Exists(logPath))
        {
            // Create minimal log for async job
            var logBuilder = new StringBuilder();
            logBuilder.AppendLine($"TimestampUtc: {timestampUtc:o}");
            logBuilder.AppendLine($"JobId: {jobId}");
            logBuilder.AppendLine($"User: {userName}");
            logBuilder.AppendLine($"Success: True");
            logBuilder.AppendLine($"Records: {result.Data.Count}");
            logBuilder.AppendLine($"Query: {job.Query}");
            logBuilder.AppendLine($"OutputFile: {outputPath}");
            logBuilder.AppendLine("DownloadHistory:");
            System.IO.File.WriteAllText(logPath, logBuilder.ToString());
        }

        QueryLogHelper.AppendDownloadEvent(logPath, normalizedFormat);

        return File(fileContent, metadata.ContentType, fileName);
    }

    /// <summary>
    /// Submit user feedback on query results
    /// </summary>
    [HttpPost("feedback")]
    public async Task<IActionResult> SubmitFeedback([FromServices] IFeedbackStore feedbackStore, [FromBody] SubmitFeedbackRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userName = GetSamAccountName(HttpContext.User);

        var feedback = new QueryFeedback
        {
            JobId = request.JobId,
            UserName = userName,
            Query = request.Query,
            ModelUsed = request.ModelUsed,
            Sentiment = request.Sentiment,
            Comment = request.Comment,
            OriginalJobId = request.OriginalJobId,
            UserRequestedRetry = request.UserRequestedRetry,
            ResultCount = request.ResultCount,
            ResponseTimeMs = request.ResponseTimeMs
        };

        try
        {
            await feedbackStore.SaveFeedbackAsync(feedback);
            return Ok(new { success = true, message = "Feedback saved successfully" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save feedback for job {JobId}", request.JobId);
            return StatusCode(500, new { error = "Failed to save feedback" });
        }
    }

    /// <summary>
    /// Retry a query with alternate model after negative feedback
    /// </summary>
    [HttpPost("retry-with-alternate-model")]
    public async Task<ActionResult<object>> RetryWithAlternateModel([FromBody] RetryWithAlternateModelRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        var userName = GetSamAccountName(HttpContext.User);

        // Get original job
        var originalJob = _jobManager.GetJob(request.OriginalJobId);
        if (originalJob == null)
        {
            _logger.LogWarning(
                "Retry-with-alternate-model requested for missing job {OriginalJobId} by {User}",
                request.OriginalJobId,
                userName);

            return NotFound(new { error = "Original job not found" });
        }

        // Verify ownership
        if (!originalJob.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Retry-with-alternate-model denied for job {OriginalJobId}. Owner {Owner}, requester {User}",
                request.OriginalJobId,
                originalJob.UserName,
                userName);

            return StatusCode(StatusCodes.Status403Forbidden, new { error = "You can only retry your own queries" });
        }

        // Create new job with Opus model
        var newJobId = Guid.NewGuid().ToString();
        var job = new QueryJob
        {
            JobId = newJobId,
            UserName = userName,
            Query = originalJob.Query,
            Context = originalJob.Context,
            RequestedResultLimit = originalJob.RequestedResultLimit,
            Status = JobStatus.Queued,
            CreatedAt = DateTime.UtcNow
        };

        try
        {
            // Save job and queue with Opus override
            var alternateModelToUse = string.IsNullOrWhiteSpace(_alternateModelId) ? null : _alternateModelId;
            if (string.IsNullOrWhiteSpace(alternateModelToUse))
            {
                _logger.LogWarning(
                    "Retry-with-alternate-model fallback to default because no alternate model configured. Job {OriginalJobId}",
                    request.OriginalJobId);
                alternateModelToUse = DefaultAlternateModel;
            }

            await _jobManager.EnqueueJobAsync(job, forceModel: alternateModelToUse);

            _logger.LogInformation(
                "User {User} requested Opus retry for job {OriginalJobId}, created new job {NewJobId}",
                userName,
                request.OriginalJobId,
                newJobId
            );

            return Ok(new
            {
                success = true,
                job_id = newJobId,
                message = $"Query resubmitted with alternate model ({_alternateModelDisplayName})"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retry query with Opus for job {JobId}", request.OriginalJobId);
            return StatusCode(500, new { error = "Failed to retry query" });
        }
    }

    /// <summary>
    /// Processes CSV data with natural language query using LLM-generated enrichment plan.
    /// The LLM acts as "operator" - it decides what to do (match column, attributes to fetch, filters).
    /// The backend does all the actual work (iterating CSV rows, AD lookups, merging results).
    /// </summary>
    [HttpPost("csv-enrich")]
    public async Task<ActionResult<object>> CsvEnrich([FromBody] CsvEnrichmentRequest request)
    {
        if (!ModelState.IsValid)
        {
            return BadRequest(ModelState);
        }

        if (request.CsvHeaders == null || !request.CsvHeaders.Any())
        {
            return BadRequest(new { error = "No CSV headers provided" });
        }

        if (request.CsvData == null || !request.CsvData.Any())
        {
            return BadRequest(new { error = "No CSV data provided" });
        }

        var userName = GetSamAccountName(HttpContext.User);
        var timestampUtc = DateTime.UtcNow;
        var userDirectory = QueryLogHelper.GetUserDirectory(userName);
        var baseFileName = QueryLogHelper.BuildFileBaseName(userName, timestampUtc);
        var logPath = Path.Combine(userDirectory, $"{baseFileName}_csv.log");
        var outputPath = Path.Combine(userDirectory, $"{baseFileName}_csv.csv");
        string? rawModelResponse = null;
        string? modelPlanJson = null;

        _logger.LogInformation("CSV enrichment request from {User}: '{Query}' with {RowCount} rows, columns: {Columns}",
            userName, request.Query, request.CsvData.Count, string.Join(", ", request.CsvHeaders));

        try
        {
            // Detect data patterns for each column (CUI-safe: describes format, not actual values)
            var columnPatterns = DetectColumnPatterns(request.CsvHeaders, request.CsvData);

            // Step 1: Call LLM to generate a CsvEnrichmentPlan (simple instruction set)
            // LLM is the "operator" - decides WHAT to do (which column, which AD attribute, what to retrieve)
            var planResponse = await _claudeService.GenerateCsvEnrichmentPlanAsync(
                request.Query,
                request.CsvHeaders,
                request.CsvData.Count,
                HttpContext.RequestAborted,
                columnPatterns);

            rawModelResponse = planResponse.RawResponse;

            if (!planResponse.Success || planResponse.Plan == null)
            {
                var errorMessage = $"Failed to generate enrichment plan: {planResponse.ErrorMessage}";
                _logger.LogWarning("Claude failed to generate CSV enrichment plan for {User}: {Error}",
                    userName, planResponse.ErrorMessage);

                WriteCsvLog(logPath, timestampUtc, userName, request.Query, errorMessage, rawModelResponse, null);
                return BadRequest(new { error = errorMessage });
            }

            var plan = planResponse.Plan;
            modelPlanJson = JsonSerializer.Serialize(plan, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                WriteIndented = true
            });

            _logger.LogInformation("Generated CSV enrichment plan: match '{MatchColumn}' on AD '{MatchAttribute}', retrieve: {Attributes}",
                plan.MatchColumn, plan.MatchAttribute, string.Join(", ", plan.RetrieveAttributes));

            // Step 2: Execute the plan - backend does all the work
            // Iterates through CSV rows, does per-user AD lookups, merges results
            var enrichmentResult = await _csvEnrichmentService.ExecuteAsync(
                plan,
                request.CsvHeaders,
                request.CsvData,
                HttpContext.RequestAborted);

            var requestId = Guid.NewGuid().ToString();
            var previewRowCount = _configuration.GetValue<int>("QueryDefaults:PreviewRowCount", 10);
            var previewRows = enrichmentResult.Data.Take(previewRowCount).ToList();

            // Save results to file
            var headers = DetermineHeaders(enrichmentResult.Data);
            var csvContent = GenerateFileContent(enrichmentResult.Data, headers, "csv");
            System.IO.File.WriteAllBytes(outputPath, csvContent);

            // Cache for download
            CacheQueryResult(
                requestId,
                enrichmentResult.Data,
                userName,
                request.Query,
                $"CSV enrichment: {plan.Description}",
                logPath,
                outputPath,
                timestampUtc,
                null);

            // Log the operation
            WriteCsvLog(logPath, timestampUtc, userName, request.Query, null, rawModelResponse, modelPlanJson,
                enrichmentResult.TotalRows, enrichmentResult.MatchedRows, enrichmentResult.FilteredRows,
                enrichmentResult.Warnings, outputPath);

            _logger.LogInformation("CSV enrichment completed for {User}: {Total} rows, {Matched} matched, {Output} in output",
                userName, enrichmentResult.TotalRows, enrichmentResult.MatchedRows, enrichmentResult.Data.Count);

            return Ok(new
            {
                success = enrichmentResult.Success,
                jobId = requestId,
                data = previewRows,
                recordCount = enrichmentResult.Data.Count,
                totalRows = enrichmentResult.TotalRows,
                matchedRows = enrichmentResult.MatchedRows,
                filteredRows = enrichmentResult.FilteredRows,
                plan = new
                {
                    matchColumn = plan.MatchColumn,
                    matchAttribute = plan.MatchAttribute,
                    retrieveAttributes = plan.RetrieveAttributes,
                    outputMode = plan.OutputMode,
                    description = plan.Description
                },
                warnings = enrichmentResult.Warnings.Any() ? enrichmentResult.Warnings : null,
                errors = enrichmentResult.Errors.Any() ? enrichmentResult.Errors : null,
                tokenUsage = planResponse.TokenUsage
            });
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("CSV enrichment request was cancelled for {User}", userName);
            return StatusCode(408, new { error = "Request was cancelled or timed out" });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "CSV enrichment failed for user {User}", userName);
            WriteCsvLog(logPath, timestampUtc, userName, request.Query, ex.Message, rawModelResponse, modelPlanJson);
            return StatusCode(500, new { error = "CSV enrichment failed", message = ex.Message });
        }
    }

    /// <summary>
    /// Detects data patterns for each column WITHOUT exposing actual CUI values.
    /// Returns format descriptions like "email/UPN format (*@domain)" not actual data.
    /// </summary>
    private static Dictionary<string, string> DetectColumnPatterns(List<string> headers, List<List<string>> data)
    {
        var patterns = new Dictionary<string, string>();
        if (data.Count == 0) return patterns;

        // Sample up to 100 rows for pattern detection
        var sampleSize = Math.Min(100, data.Count);

        for (int colIndex = 0; colIndex < headers.Count; colIndex++)
        {
            var header = headers[colIndex];
            var sampleValues = new List<string>();

            for (int rowIndex = 0; rowIndex < sampleSize; rowIndex++)
            {
                if (colIndex < data[rowIndex].Count)
                {
                    var val = data[rowIndex][colIndex];
                    if (!string.IsNullOrWhiteSpace(val))
                    {
                        sampleValues.Add(val);
                    }
                }
            }

            if (sampleValues.Count == 0)
            {
                patterns[header] = "empty or null values";
                continue;
            }

            // Detect pattern type (CUI-safe: describes format, not values)
            var pattern = DetectValuePattern(sampleValues);
            patterns[header] = pattern;
        }

        return patterns;
    }

    /// <summary>
    /// Analyzes sample values to determine the data format pattern.
    /// Returns a CUI-safe description of the format (no actual values exposed).
    /// </summary>
    private static string DetectValuePattern(List<string> values)
    {
        if (values.Count == 0) return "no data";

        // Check for email/UPN pattern (contains @)
        var emailCount = values.Count(v => v.Contains('@'));
        if (emailCount > values.Count * 0.8)
        {
            // Check if they all share a common domain
            var domains = values
                .Where(v => v.Contains('@'))
                .Select(v => v.Split('@').LastOrDefault()?.ToLowerInvariant() ?? "")
                .Where(d => !string.IsNullOrEmpty(d))
                .GroupBy(d => d)
                .OrderByDescending(g => g.Count())
                .FirstOrDefault();

            if (domains != null && domains.Count() > values.Count * 0.5)
            {
                return $"email/UPN format (*@{domains.Key}) - use userPrincipalName or mail";
            }
            return "email/UPN format (*@domain) - use userPrincipalName or mail";
        }

        // Check for numeric pattern (employee IDs)
        var numericCount = values.Count(v => v.All(c => char.IsDigit(c) || c == '-'));
        if (numericCount > values.Count * 0.8)
        {
            var avgLength = values.Average(v => v.Length);
            return $"numeric IDs (avg {avgLength:F0} digits) - use employeeID";
        }

        // Check for name format (contains comma - "Last, First")
        var commaCount = values.Count(v => v.Contains(','));
        if (commaCount > values.Count * 0.5)
        {
            return "name format (Last, First) - use displayName with 'equals' operator";
        }

        // Check for "First Last" name format (spaces, mostly letters, 2-4 words)
        var namePatternCount = values.Count(v =>
        {
            var parts = v.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 &&
                   parts.Length <= 4 &&
                   parts.All(p => p.All(c => char.IsLetter(c) || c == '-' || c == '\''));
        });
        if (namePatternCount > values.Count * 0.7)
        {
            return "name format (First Last) - use displayName with 'contains' operator (WARNING: may have duplicates)";
        }

        // Check for sAMAccountName pattern (short alphanumeric, no spaces, no @)
        var shortAlphanumeric = values.Count(v =>
            v.Length <= 20 &&
            !v.Contains('@') &&
            !v.Contains(' ') &&
            !v.Contains(',') &&
            v.All(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.'));

        if (shortAlphanumeric > values.Count * 0.8)
        {
            var avgLength = values.Average(v => v.Length);
            if (avgLength <= 8)
            {
                return $"short alphanumeric (avg {avgLength:F0} chars) - likely sAMAccountName";
            }
            return $"alphanumeric identifiers (avg {avgLength:F0} chars)";
        }

        // Check for DN pattern
        var dnCount = values.Count(v => v.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));
        if (dnCount > values.Count * 0.5)
        {
            return "Distinguished Name format (CN=...) - use distinguishedName";
        }

        // Default: describe general characteristics
        var avgLen = values.Average(v => v.Length);
        var hasSpaces = values.Count(v => v.Contains(' ')) > values.Count * 0.3;

        if (hasSpaces)
        {
            return $"text with spaces (avg {avgLen:F0} chars) - may be displayName or description";
        }

        return $"mixed format (avg {avgLen:F0} chars)";
    }

    private static void WriteCsvLog(
        string logPath,
        DateTime timestampUtc,
        string userName,
        string query,
        string? errorMessage,
        string? rawModelResponse,
        string? planJson,
        int totalRows = 0,
        int matchedRows = 0,
        int filteredRows = 0,
        List<string>? warnings = null,
        string? outputPath = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"TimestampUtc: {timestampUtc:o}");
        sb.AppendLine($"User: {userName}");
        sb.AppendLine($"Query: {query}");
        sb.AppendLine($"Success: {string.IsNullOrEmpty(errorMessage)}");
        if (!string.IsNullOrEmpty(errorMessage))
        {
            sb.AppendLine($"Error: {errorMessage}");
        }
        sb.AppendLine($"TotalRows: {totalRows}");
        sb.AppendLine($"MatchedRows: {matchedRows}");
        sb.AppendLine($"FilteredRows: {filteredRows}");
        if (warnings != null && warnings.Any())
        {
            sb.AppendLine($"Warnings: {string.Join("; ", warnings)}");
        }
        if (!string.IsNullOrEmpty(outputPath))
        {
            sb.AppendLine($"OutputFile: {outputPath}");
        }
        if (!string.IsNullOrEmpty(planJson))
        {
            sb.AppendLine($"EnrichmentPlan: {planJson}");
        }
        if (!string.IsNullOrEmpty(rawModelResponse))
        {
            sb.AppendLine($"RawModelResponse: {rawModelResponse}");
        }

        try
        {
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }
            System.IO.File.WriteAllText(logPath, sb.ToString());
        }
        catch
        {
            // Best effort logging
        }
    }

    private static string DeriveModelDisplayName(string modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId))
        {
            return "alternate-model";
        }

        var trimmed = modelId.Trim().Trim('@');
        var withoutVersion = trimmed;
        var versionSeparator = trimmed.LastIndexOf('@');
        if (versionSeparator > 0)
        {
            withoutVersion = trimmed.Substring(0, versionSeparator);
        }

        var lastSlash = withoutVersion.LastIndexOf('/');
        var baseName = lastSlash >= 0 ? withoutVersion.Substring(lastSlash + 1) : withoutVersion;

        const string anthropicPrefix = "anthropic.";
        if (baseName.StartsWith(anthropicPrefix, StringComparison.OrdinalIgnoreCase))
        {
            baseName = baseName.Substring(anthropicPrefix.Length);
        }

        return string.IsNullOrWhiteSpace(baseName) ? "alternate-model" : baseName;
    }

    private object? BuildAggregationSummary(QueryJob job)
    {
        if (job.Aggregation == null || !job.Aggregation.Any())
        {
            return null;
        }

        return new
        {
            grouped_counts = job.Aggregation.ContainsKey("grouped_counts")
                ? job.Aggregation["grouped_counts"]
                : null,
            level_metadata = job.Aggregation.ContainsKey("level_metadata")
                ? job.Aggregation["level_metadata"]
                : null,
            group_by_fields = job.Plan?.Projection?.Aggregation?.GroupBy
        };
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

/// <summary>
/// Metadata about the query for display in downloads
/// </summary>
internal class QueryMetadata
{
    public string Query { get; set; } = string.Empty;
    public string User { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public int RecordCount { get; set; }
    public string? Model { get; set; }
}

/// <summary>
/// Request model for CSV enrichment
/// </summary>
public class CsvEnrichmentRequest
{
    /// <summary>
    /// Natural language query describing what to do with the CSV users
    /// </summary>
    [Required]
    [StringLength(2000, MinimumLength = 1)]
    public string Query { get; set; } = string.Empty;

    /// <summary>
    /// CSV column headers
    /// </summary>
    [Required]
    public List<string> CsvHeaders { get; set; } = new();

    /// <summary>
    /// CSV data rows (each row is a list of values)
    /// </summary>
    [Required]
    public List<List<string>> CsvData { get; set; } = new();
}







