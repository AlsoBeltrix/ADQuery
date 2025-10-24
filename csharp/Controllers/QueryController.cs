using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using ClosedXML.Excel;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
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

    public QueryController(
        ILogger<QueryController> logger,
        IClaudeService claudeService,
        IDirectoryPlanExecutor planExecutor,
        IMemoryCache cache,
        IConfiguration configuration,
        IQueryJobManager jobManager,
        IPlanPreprocessor planPreprocessor)
    {
        _logger = logger;
        _claudeService = claudeService;
        _planExecutor = planExecutor;
        _cache = cache;
        _configuration = configuration;
        _jobManager = jobManager;
        _planPreprocessor = planPreprocessor;
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

            _planPreprocessor.PrepareForExecution(plan, requestedLimit);

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

    /// <summary>
    /// Gets client configuration settings
    /// </summary>
    [HttpGet("config")]
    [AllowAnonymous]
    public IActionResult GetConfig()
    {
        return Ok(new
        {
            previewRowCount = _configuration.GetValue<int>("QueryDefaults:PreviewRowCount", 10),
            summaryRowCount = _configuration.GetValue<int>("QueryDefaults:SummaryRowCount", 20)
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

    /// <summary>
    /// Creates an async query job (returns immediately with jobId for polling)
    /// </summary>
    [HttpPost("execute-async")]
    public IActionResult ExecuteQueryAsync([FromBody] QueryRequest request)
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
            var jobId = _jobManager.CreateJob(userName, request.Query, context, requestedLimit);

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
            createdAt = job.CreatedAt,
            startedAt = job.StartedAt,
            completedAt = job.CompletedAt,
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
        var userDirectory = GetUserDirectory(OutputRoot, userName);
        var baseFileName = $"adquery_{userName.ToUpperInvariant()}_{timestampUtc:yyyyMMdd_HHmmssfff}";
        var fileName = $"{baseFileName}.{metadata.Extension}";
        var outputPath = Path.Combine(userDirectory, fileName);

        var queryMetadata = new QueryMetadata
        {
            Query = job.Query,
            User = userName,
            Timestamp = timestampUtc,
            RecordCount = result.Data.Count
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

        AppendDownloadEvent(logPath, normalizedFormat);

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

        // Get original job
        var originalJob = _jobManager.GetJob(request.OriginalJobId);
        if (originalJob == null)
        {
            return NotFound(new { error = "Original job not found" });
        }

        var userName = GetSamAccountName(HttpContext.User);

        // Verify ownership
        if (!originalJob.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
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
            await _jobManager.EnqueueJobAsync(job, forceModel: "@vertexai-global/anthropic.claude-opus-4-1@20250805");

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
                message = "Query resubmitted with alternate model"
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to retry query with Opus for job {JobId}", request.OriginalJobId);
            return StatusCode(500, new { error = "Failed to retry query" });
        }
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
}







