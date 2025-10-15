using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Service for integrating with Claude AI to generate JSON directory plans.
/// </summary>
public class ClaudeService : IClaudeService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ClaudeService> _logger;
    private readonly IConfiguration _configuration;

    public ClaudeService(HttpClient httpClient, ILogger<ClaudeService> logger, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;

        var baseUrl = _configuration["Claude:BaseUrl"];
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            _httpClient.BaseAddress = new Uri(baseUrl);
        }

        var apiKey = _configuration["Claude:ApiKey"];
        var authToken = _configuration["Claude:AuthToken"];

        if (!string.IsNullOrWhiteSpace(baseUrl) && baseUrl.Contains("portkey", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                _httpClient.DefaultRequestHeaders.Add("x-portkey-api-key", apiKey);
            }

            if (!string.IsNullOrWhiteSpace(authToken))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            }
        }
        else if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("x-api-key", apiKey);
            _httpClient.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
        }
    }

    public async Task<ClaudeResponse> GenerateExecutionPlanAsync(
        string userQuery,
        string? context = null,
        int? requestedResultLimit = null,
        CancellationToken cancellationToken = default)
    {
        var response = new ClaudeResponse();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            if (string.IsNullOrWhiteSpace(_configuration["Claude:ApiKey"]))
            {
                response.Success = false;
                response.ErrorMessage = "Claude API key is not configured.";
                _logger.LogWarning("Cannot generate directory plan because Claude API key is missing.");
                return response;
            }

            _logger.LogInformation("Generating directory plan for query: {Query}", userQuery);

            var prompt = BuildExecutionPlanPrompt(userQuery, context, requestedResultLimit);

            var claudeRequest = new
            {
                model = _configuration["Claude:Model"] ?? "claude-3-sonnet-20240229",
                max_tokens = int.Parse(_configuration["Claude:MaxTokens"] ?? "4000"),
                messages = new[]
                {
                    new { role = "user", content = prompt }
                }
            };

            var requestJson = JsonSerializer.Serialize(claudeRequest);
            using var requestContent = new StringContent(requestJson, Encoding.UTF8, "application/json");

            var endpoint = _configuration["Claude:Endpoint"] ?? "/v1/messages";
            var apiResponse = await _httpClient.PostAsync(endpoint, requestContent, cancellationToken);
            if (!apiResponse.IsSuccessStatusCode)
            {
                var errorBody = await apiResponse.Content.ReadAsStringAsync();

                if (apiResponse.StatusCode == HttpStatusCode.Unauthorized ||
                    errorBody.Contains("API Key Not Found", StringComparison.OrdinalIgnoreCase))
                {
                    response.Success = false;
                    response.ErrorMessage = "Claude API key is missing or invalid. Please verify Claude:ApiKey.";
                    _logger.LogWarning("Claude API returned Unauthorized. Verify Claude:ApiKey configuration.");
                    return response;
                }

                response.Success = false;
                response.ErrorMessage = $"Claude API error: {apiResponse.StatusCode} - {errorBody}";
                _logger.LogError("Claude API request failed: {StatusCode} - {Error}", apiResponse.StatusCode, response.ErrorMessage);
                return response;
            }

            var responseContent = await apiResponse.Content.ReadAsStringAsync(cancellationToken);
            response.RawResponse = responseContent;

            var claudeResponse = JsonSerializer.Deserialize<ClaudeApiResponse>(
                responseContent,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var assistantMessage = claudeResponse?.Content?
                .FirstOrDefault(block => !string.IsNullOrWhiteSpace(block.Text));

            if (assistantMessage?.Text is null)
            {
                response.Success = false;
                response.ErrorMessage = "Invalid response format from Claude API";
                _logger.LogWarning("Claude API returned unexpected payload: {Payload}", TruncateForLog(responseContent));
                return response;
            }

            var planJson = ExtractJsonFromResponse(assistantMessage.Text);
            if (string.IsNullOrWhiteSpace(planJson))
            {
                response.Success = false;
                response.ErrorMessage = "Claude response did not contain a JSON plan.";
                _logger.LogWarning("Claude response text missing JSON block: {Payload}", TruncateForLog(assistantMessage.Text));
                return response;
            }

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true
            };

            response.Plan = JsonSerializer.Deserialize<DirectoryQueryPlan>(planJson, jsonOptions);
            if (response.Plan is null)
            {
                response.Success = false;
                response.ErrorMessage = "Failed to parse directory plan.";
                _logger.LogWarning("Unable to deserialize plan: {PlanJson}", TruncateForLog(planJson));
                return response;
            }

            response.Success = true;
            response.TokenUsage = new TokenUsage
            {
                InputTokens = claudeResponse?.Usage?.InputTokens ?? 0,
                OutputTokens = claudeResponse?.Usage?.OutputTokens ?? 0
            };
        }
        catch (Exception ex)
        {
            response.Success = false;
            response.ErrorMessage = "Error generating directory plan.";
            _logger.LogError(ex, "Error generating directory plan for query: {Query}", userQuery);
        }
        finally
        {
            stopwatch.Stop();
            response.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
        }

        return response;
    }

    public async Task<ClaudeHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        var result = new ClaudeHealthResult();
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var testResponse = await GenerateExecutionPlanAsync(
                "Return a simple confirmation",
                context: null,
                requestedResultLimit: null,
                cancellationToken: cancellationToken);
            result.IsHealthy = testResponse.Success && testResponse.Plan is not null;
            result.JsonParsingWorking = testResponse.Plan is not null;
            result.ErrorMessage = testResponse.ErrorMessage;
        }
        catch (Exception ex)
        {
            result.IsHealthy = false;
            result.JsonParsingWorking = false;
            result.ErrorMessage = ex.Message;
            _logger.LogError(ex, "Claude health check failed");
        }
        finally
        {
            stopwatch.Stop();
            result.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
            result.LastSuccessfulResponse = result.IsHealthy ? DateTime.UtcNow : result.LastSuccessfulResponse;
        }

        return result;
    }

    private string BuildExecutionPlanPrompt(string userQuery, string? context, int? requestedResultLimit)
    {
        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("Act as an expert Active Directory analyst.");
        promptBuilder.AppendLine("Generate a JSON plan that a C# service can execute without shell commands.");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("PLAN REQUIREMENTS:");
        promptBuilder.AppendLine("- Output strictly valid JSON." );
        promptBuilder.AppendLine("- Use the schema below.");
        promptBuilder.AppendLine("- Supported operations: search, expand_members, lookup.");
        promptBuilder.AppendLine("- search: query a directory object type with filters and attributes.");
        promptBuilder.AppendLine("- expand_members: expand group membership from a prior step (set source, target_type, attributes, recursive flag).");
        promptBuilder.AppendLine("- lookup: fetch related directory objects using distinguished names from a prior step (set source, source_attribute, target_type, attributes).");
        promptBuilder.AppendLine("- target_type must be one of: User, Group, Computer, OrganizationalUnit.");
        promptBuilder.AppendLine("- Filters support operators: equals, contains, starts_with, ends_with.");
        promptBuilder.AppendLine("- Attribute lists must only include directory attributes (displayName, manager, mail, etc.).");
        promptBuilder.AppendLine("- Projection must describe how to build rows for the caller.");
        promptBuilder.AppendLine("- When the user requests a specific number of rows (\"first 5\", \"top 10\"), set result_limit to that number and apply size_limit to the step that produces those rows.");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("JSON FORMAT:");
        promptBuilder.AppendLine("```json");
        promptBuilder.AppendLine("{");
        promptBuilder.AppendLine("  \"description\": \"Human-readable summary\",");
        promptBuilder.AppendLine("  \"steps\": [");
        promptBuilder.AppendLine("    {");
        promptBuilder.AppendLine("      \"step\": 1,");
        promptBuilder.AppendLine("      \"name\": \"domain_admins\",");
        promptBuilder.AppendLine("      \"operation\": \"search\",");
        promptBuilder.AppendLine("      \"target_type\": \"Group\",");
        promptBuilder.AppendLine("      \"filters\": [ { \"attribute\": \"name\", \"operator\": \"equals\", \"value\": \"Domain Admins\" } ],");
        promptBuilder.AppendLine("      \"attributes\": [ \"distinguishedName\", \"name\" ],");
        promptBuilder.AppendLine("      \"size_limit\": 25");
        promptBuilder.AppendLine("    },");
        promptBuilder.AppendLine("    {");
        promptBuilder.AppendLine("      \"step\": 2,");
        promptBuilder.AppendLine("      \"name\": \"group_members\",");
        promptBuilder.AppendLine("      \"operation\": \"expand_members\",");
        promptBuilder.AppendLine("      \"source\": \"domain_admins\",");
        promptBuilder.AppendLine("      \"target_type\": \"User\",");
        promptBuilder.AppendLine("      \"recursive\": false,");
        promptBuilder.AppendLine("      \"attributes\": [ \"distinguishedName\", \"displayName\", \"manager\", \"mail\" ]");
        promptBuilder.AppendLine("    },");
        promptBuilder.AppendLine("    {");
        promptBuilder.AppendLine("      \"step\": 3,");
        promptBuilder.AppendLine("      \"name\": \"manager_details\",");
        promptBuilder.AppendLine("      \"operation\": \"lookup\",");
        promptBuilder.AppendLine("      \"source\": \"group_members\",");
        promptBuilder.AppendLine("      \"source_attribute\": \"manager\",");
        promptBuilder.AppendLine("      \"target_type\": \"User\",");
        promptBuilder.AppendLine("      \"attributes\": [ \"distinguishedName\", \"displayName\", \"mail\" ]");
        promptBuilder.AppendLine("    }");
        promptBuilder.AppendLine("  ],");
        promptBuilder.AppendLine("  \"result_limit\": 25,");
        promptBuilder.AppendLine("  \"projection\": {");
        promptBuilder.AppendLine("    \"row_step\": \"group_members\",");
        promptBuilder.AppendLine("    \"columns\": [");
        promptBuilder.AppendLine("      { \"name\": \"User\", \"attribute\": \"displayName\" },");
        promptBuilder.AppendLine("      { \"name\": \"Manager\", \"attribute\": \"displayName\", \"source_step\": \"manager_details\", \"match_on\": \"distinguishedName\", \"match_value_from\": \"manager\" },");
        promptBuilder.AppendLine("      { \"name\": \"ManagerEmail\", \"attribute\": \"mail\", \"source_step\": \"manager_details\", \"match_on\": \"distinguishedName\", \"match_value_from\": \"manager\", \"default\": \"\" }");
        promptBuilder.AppendLine("    ]");
        promptBuilder.AppendLine("  }");
        promptBuilder.AppendLine("}");
        promptBuilder.AppendLine("```");
        promptBuilder.AppendLine();
        if (requestedResultLimit.HasValue && requestedResultLimit.Value > 0)
        {
            promptBuilder.AppendLine($"The user explicitly requested only {requestedResultLimit.Value} rows. Ensure the plan sets \"result_limit\": {requestedResultLimit.Value} and that the search step supplying those rows uses \"size_limit\": {requestedResultLimit.Value}.");
            promptBuilder.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(context))
        {
            promptBuilder.AppendLine("CONTEXT:");
            promptBuilder.AppendLine(context);
            promptBuilder.AppendLine();
        }

        promptBuilder.AppendLine("USER QUERY:");
        promptBuilder.AppendLine(userQuery);
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("Produce only the JSON plan. Do not include explanations.");

        return promptBuilder.ToString();
    }

    private static string ExtractJsonFromResponse(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return string.Empty;
        }

        var jsonStart = responseText.IndexOf("```json", StringComparison.OrdinalIgnoreCase);
        if (jsonStart >= 0)
        {
            jsonStart += 7;
            var jsonEnd = responseText.IndexOf("```", jsonStart, StringComparison.OrdinalIgnoreCase);
            if (jsonEnd > jsonStart)
            {
                return responseText.Substring(jsonStart, jsonEnd - jsonStart).Trim();
            }
        }

        var braceStart = responseText.IndexOf('{');
        var braceEnd = responseText.LastIndexOf('}');
        if (braceStart >= 0 && braceEnd > braceStart)
        {
            return responseText.Substring(braceStart, braceEnd - braceStart + 1);
        }

        return string.Empty;
    }

    private static string TruncateForLog(string? value, int maxLength = 1500)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...(truncated)";
    }
}

internal class ClaudeApiResponse
{
    public ClaudeContent[]? Content { get; set; }
    public ClaudeUsage? Usage { get; set; }
}

internal class ClaudeContent
{
    public string? Text { get; set; }
}

internal class ClaudeUsage
{
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
}
