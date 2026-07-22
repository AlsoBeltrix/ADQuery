using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Models;
using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Service for integrating with Claude AI to generate JSON directory plans.
/// </summary>
internal sealed class ClaudeService : IClaudeService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ClaudeService> _logger;
    private readonly IConfiguration _configuration;
    private readonly LlmProviderOptions _providerOptions;
    private readonly LlmMessagesRequestBuilder _requestBuilder;
    private readonly string? _promptTemplate;

    public ClaudeService(
        HttpClient httpClient,
        ILogger<ClaudeService> logger,
        IConfiguration configuration,
        IOptions<LlmProviderOptions> providerOptions,
        LlmMessagesRequestBuilder requestBuilder)
    {
        _httpClient = httpClient;
        _logger = logger;
        _configuration = configuration;
        _providerOptions = providerOptions.Value;
        _requestBuilder = requestBuilder;

        var baseUrl = _providerOptions.BaseUrl;
        if (!string.IsNullOrWhiteSpace(baseUrl))
        {
            _httpClient.BaseAddress = new Uri(baseUrl);
        }

        var apiKey = _providerOptions.ApiKey;
        var authToken = _providerOptions.AuthToken;

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

        // Load prompt template from file if it exists
        var promptTemplatePath = _providerOptions.PromptTemplate ?? "Configuration/prompt_template.txt";
        if (File.Exists(promptTemplatePath))
        {
            try
            {
                _promptTemplate = File.ReadAllText(promptTemplatePath);
                _logger.LogInformation("Loaded prompt template from {Path}", promptTemplatePath);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load prompt template from {Path}, using built-in template", promptTemplatePath);
            }
        }
    }

    public async Task<ClaudeResponse> GenerateExecutionPlanAsync(
      string userQuery,
      string? context = null,
      int? requestedResultLimit = null,
      CancellationToken cancellationToken = default,
      string? modelOverride = null)
    {
        var response = new ClaudeResponse();
        var stopwatch = Stopwatch.StartNew();
        var effectiveModel = !string.IsNullOrWhiteSpace(modelOverride)
          ? modelOverride
          : _providerOptions.Model ?? "claude-3-sonnet-20240229";
        var endpoint = _providerOptions.Endpoint ?? "/v1/messages";

        try
        {
            if (string.IsNullOrWhiteSpace(_providerOptions.ApiKey))
            {
                response.Success = false;
                response.ErrorMessage = "Claude API key is not configured.";
                _logger.LogWarning("Cannot generate directory plan because Claude API key is missing.");
                return response;
            }

            _logger.LogInformation("Generating directory plan using model {Model}", effectiveModel);

            var prompt = BuildExecutionPlanPrompt(userQuery, context, requestedResultLimit);
            var systemGuidance = BuildSystemGuidance(_configuration);

            var claudeRequest = _requestBuilder.Build(
                effectiveModel,
                int.Parse(_providerOptions.MaxTokens),
                systemGuidance,
                prompt);

            var requestJson = JsonSerializer.Serialize(claudeRequest);
            using var apiRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };
            using var apiResponse = await _httpClient.SendAsync(
                apiRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!apiResponse.IsSuccessStatusCode)
            {
                var errorBody = await LlmProviderErrorParser.ReadBoundedBodyAsync(
                    apiResponse.Content,
                    cancellationToken);
                var errorDetails = LlmProviderErrorParser.Parse(
                    errorBody,
                    LlmProviderErrorParser.GetCorrelationId(apiResponse),
                    [
                        _providerOptions.ApiKey,
                        _providerOptions.AuthToken,
                        userQuery,
                        context,
                        systemGuidance,
                        prompt,
                        requestJson
                    ]);

                if (apiResponse.StatusCode == HttpStatusCode.Unauthorized ||
                  errorBody.Content.Contains("API Key Not Found", StringComparison.OrdinalIgnoreCase))
                {
                    response.Success = false;
                    response.ErrorMessage = "Claude API key is missing or invalid. Please verify Claude:ApiKey.";
                    LogProviderFailure(
                        endpoint,
                        effectiveModel,
                        apiResponse.StatusCode,
                        errorDetails,
                        authenticationFailure: true);
                    return response;
                }

                response.Success = false;
                response.ErrorMessage =
                    $"Claude API error: {apiResponse.StatusCode} - {errorDetails.ToPublicDescription()}";
                LogProviderFailure(
                    endpoint,
                    effectiveModel,
                    apiResponse.StatusCode,
                    errorDetails,
                    authenticationFailure: false);
                return response;
            }

            var responseContent = await apiResponse.Content.ReadAsStringAsync(cancellationToken);

            var claudeResponse = JsonSerializer.Deserialize<ClaudeApiResponse>(
              responseContent,
              new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var assistantMessage = claudeResponse?.Content?
              .FirstOrDefault(block => !string.IsNullOrWhiteSpace(block.Text));

            if (assistantMessage?.Text is null)
            {
                response.Success = false;
                response.ErrorMessage = "Invalid response format from Claude API";
                LogProviderProtocolFailure(endpoint, effectiveModel, "assistant_content_missing");
                return response;
            }

            var planJson = ExtractJsonFromResponse(assistantMessage.Text);
            if (string.IsNullOrWhiteSpace(planJson))
            {
                response.Success = false;
                response.ErrorMessage = "Claude response did not contain a JSON plan.";
                LogProviderProtocolFailure(endpoint, effectiveModel, "json_plan_missing");
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
                LogProviderProtocolFailure(endpoint, effectiveModel, "json_plan_invalid");
                return response;
            }

            response.RawResponse = responseContent;
            response.Success = true;
            response.ModelUsed = effectiveModel;
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
            LogProviderException(endpoint, effectiveModel, ex);
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
            result.ErrorMessage = "Provider health check failed.";
            _logger.LogError(
                "LLM provider health check failed with {ExceptionType}",
                ex.GetType().Name);
        }
        finally
        {
            stopwatch.Stop();
            result.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
            result.LastSuccessfulResponse = result.IsHealthy ? DateTime.UtcNow : result.LastSuccessfulResponse;
        }

        return result;
    }

    public async Task<CsvEnrichmentPlanResponse> GenerateCsvEnrichmentPlanAsync(
      string userQuery,
      List<string> csvHeaders,
      int rowCount,
      CancellationToken cancellationToken = default,
      Dictionary<string, string>? columnPatterns = null)
    {
        var response = new CsvEnrichmentPlanResponse();
        var stopwatch = Stopwatch.StartNew();
        var effectiveModel = _providerOptions.Model ?? "claude-3-sonnet-20240229";
        var endpoint = _providerOptions.Endpoint ?? "/v1/messages";

        try
        {
            if (string.IsNullOrWhiteSpace(_providerOptions.ApiKey))
            {
                response.Success = false;
                response.ErrorMessage = "Claude API key is not configured.";
                return response;
            }

            _logger.LogInformation("Generating CSV enrichment plan using model {Model}", effectiveModel);

            var prompt = BuildCsvEnrichmentPrompt(userQuery, csvHeaders, rowCount, columnPatterns);
            var systemGuidance = BuildCsvEnrichmentSystemGuidance();

            var claudeRequest = _requestBuilder.Build(
                effectiveModel,
                int.Parse(_providerOptions.MaxTokens),
                systemGuidance,
                prompt);

            var requestJson = JsonSerializer.Serialize(claudeRequest);
            using var apiRequest = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = new StringContent(requestJson, Encoding.UTF8, "application/json")
            };
            using var apiResponse = await _httpClient.SendAsync(
                apiRequest,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!apiResponse.IsSuccessStatusCode)
            {
                var errorBody = await LlmProviderErrorParser.ReadBoundedBodyAsync(
                    apiResponse.Content,
                    cancellationToken);
                var errorDetails = LlmProviderErrorParser.Parse(
                    errorBody,
                    LlmProviderErrorParser.GetCorrelationId(apiResponse),
                    [
                        _providerOptions.ApiKey,
                        _providerOptions.AuthToken,
                        userQuery,
                        systemGuidance,
                        prompt,
                        requestJson
                    ]);
                response.Success = false;
                response.ErrorMessage =
                    $"Claude API error: {apiResponse.StatusCode} - {errorDetails.ToPublicDescription()}";
                LogProviderFailure(
                    endpoint,
                    effectiveModel,
                    apiResponse.StatusCode,
                    errorDetails,
                    authenticationFailure: false);
                return response;
            }

            var responseContent = await apiResponse.Content.ReadAsStringAsync(cancellationToken);

            var claudeResponse = JsonSerializer.Deserialize<ClaudeApiResponse>(
              responseContent,
              new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

            var assistantMessage = claudeResponse?.Content?
              .FirstOrDefault(block => !string.IsNullOrWhiteSpace(block.Text));

            if (assistantMessage?.Text is null)
            {
                response.Success = false;
                response.ErrorMessage = "Invalid response format from Claude API";
                return response;
            }

            var planJson = ExtractJsonFromResponse(assistantMessage.Text);
            if (string.IsNullOrWhiteSpace(planJson))
            {
                response.Success = false;
                response.ErrorMessage = "Claude response did not contain a JSON plan.";
                return response;
            }

            var jsonOptions = new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
                PropertyNameCaseInsensitive = true
            };

            response.Plan = JsonSerializer.Deserialize<CsvEnrichmentPlan>(planJson, jsonOptions);
            if (response.Plan is null)
            {
                response.Success = false;
                response.ErrorMessage = "Failed to parse CSV enrichment plan.";
                return response;
            }

            response.RawResponse = responseContent;
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
            response.ErrorMessage = "Error generating CSV enrichment plan.";
            LogProviderException(endpoint, effectiveModel, ex);
        }
        finally
        {
            stopwatch.Stop();
            response.ResponseTimeMs = stopwatch.ElapsedMilliseconds;
        }

        return response;
    }

    private static string BuildCsvEnrichmentPrompt(string userQuery, List<string> csvHeaders, int rowCount, Dictionary<string, string>? columnPatterns)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert Active Directory analyst helping with CSV data enrichment.");
        sb.AppendLine();
        sb.AppendLine("The user has uploaded a CSV file and wants to enrich it with Active Directory data.");
        sb.AppendLine();
        sb.AppendLine("CSV FILE INFO:");
        sb.AppendLine($"- Row count: {rowCount}");
        sb.AppendLine($"- Column headers: {string.Join(", ", csvHeaders)}");
        sb.AppendLine();

        // Add detected patterns (CUI-safe: describes format, not actual values)
        if (columnPatterns != null && columnPatterns.Count > 0)
        {
            sb.AppendLine("DETECTED DATA PATTERNS (format analysis, no actual values):");
            foreach (var kvp in columnPatterns)
            {
                sb.AppendLine($"- Column '{kvp.Key}': {kvp.Value}");
            }
            sb.AppendLine();
            sb.AppendLine("CRITICAL: Use the detected patterns to choose the correct match_attribute:");
            sb.AppendLine("- If pattern shows 'email/UPN format (*@domain)' → use 'userPrincipalName' or 'mail'");
            sb.AppendLine("- If pattern shows 'short alphanumeric (8 chars or less)' → use 'sAMAccountName'");
            sb.AppendLine("- If pattern shows 'name format (Last, First)' → use 'displayName'");
            sb.AppendLine("- If pattern shows 'numeric IDs' → use 'employeeID'");
            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("IMPORTANT: No sample data is provided due to data sensitivity (CUI compliance).");
            sb.AppendLine("You must infer the identifier type from column names only.");
        }
        sb.AppendLine();
        sb.AppendLine("YOUR TASK:");
        sb.AppendLine("Generate a JSON instruction plan that tells the backend how to:");
        sb.AppendLine("1. Match CSV rows to AD users (which CSV column contains identifiers, which AD attribute to match)");
        sb.AppendLine("2. What AD attributes to retrieve for each matched user");
        sb.AppendLine("3. Optional: filter criteria to apply to results");
        sb.AppendLine("4. Output mode: 'all' (include unmatched rows) or 'filtered' (only filtered rows)");
        sb.AppendLine();
        sb.AppendLine("COMMON IDENTIFIER PATTERNS:");
        sb.AppendLine("- 'email', 'mail', 'e-mail', 'EmailAddress' → match on: userPrincipalName or mail");
        sb.AppendLine("- 'username', 'user', 'samaccountname', 'login', 'userid' → match on: sAMAccountName");
        sb.AppendLine("- 'upn', 'userprincipalname' → match on: userPrincipalName");
        sb.AppendLine("- 'name', 'displayname', 'full name', 'fullname' → match on: displayName");
        sb.AppendLine("- 'employeeid', 'employee id', 'empid', 'emp_id' → match on: employeeID");
        sb.AppendLine();
        sb.AppendLine("COMMON AD ATTRIBUTES TO RETRIEVE:");
        sb.AppendLine("- User info: displayName, mail, department, title, manager, employeeType");
        sb.AppendLine("- Account status: Enabled, AccountExpirationDate, LastLogonDate, LockedOut");
        sb.AppendLine("- Group membership: memberOf (for group membership checks)");
        sb.AppendLine("- Custom: extensionAttribute1-15, employeeID");
        sb.AppendLine();
        sb.AppendLine("JSON OUTPUT FORMAT:");
        sb.AppendLine("```json");
        sb.AppendLine("{");
        sb.AppendLine("  \"match_column\": \"<CSV column name containing user identifiers>\",");
        sb.AppendLine("  \"match_attribute\": \"<AD attribute to match: sAMAccountName|userPrincipalName|mail|displayName|employeeID>\",");
        sb.AppendLine("  \"retrieve_attributes\": [\"<AD attributes to fetch>\"],");
        sb.AppendLine("  \"filter\": {");
        sb.AppendLine("    \"attribute\": \"<optional: AD attribute to filter on>\",");
        sb.AppendLine("    \"operator\": \"<equals|not_equals|contains|not_contains|starts_with|ends_with>\",");
        sb.AppendLine("    \"value\": \"<filter value>\"");
        sb.AppendLine("  },");
        sb.AppendLine("  \"output_mode\": \"all|filtered\",");
        sb.AppendLine("  \"description\": \"<human-readable description of what this plan does>\"");
        sb.AppendLine("}");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("EXAMPLES:");
        sb.AppendLine();
        sb.AppendLine("Query: 'identify which users are contractors'");
        sb.AppendLine("→ retrieve_attributes should include 'employeeType', filter on employeeType equals 'CWK'");
        sb.AppendLine();
        sb.AppendLine("Query: 'add department and manager info'");
        sb.AppendLine("→ retrieve_attributes: ['department', 'manager', 'displayName'], no filter needed");
        sb.AppendLine();
        sb.AppendLine("Query: 'show which accounts are disabled'");
        sb.AppendLine("→ retrieve_attributes should include 'Enabled', filter on Enabled equals 'false'");
        sb.AppendLine();
        sb.AppendLine("Query: 'add mailbox location and license'");
        sb.AppendLine("→ retrieve_attributes: ['msExchRecipientTypeDetails', 'extensionAttribute11'] (license is in extensionAttribute11)");
        sb.AppendLine();
        sb.AppendLine("USER QUERY:");
        sb.AppendLine(userQuery);
        sb.AppendLine();
        sb.AppendLine("Produce only the JSON plan. Do not include explanations.");

        return sb.ToString();
    }

    private string BuildCsvEnrichmentSystemGuidance()
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are an expert Active Directory analyst. Your role is to interpret user requests about CSV data enrichment and output structured JSON instructions.");
        sb.AppendLine();
        sb.AppendLine("Key rules:");
        sb.AppendLine("- Always output valid JSON matching the specified schema");
        sb.AppendLine("- Use the DETECTED DATA PATTERNS to choose the correct match_attribute - this is critical!");
        sb.AppendLine("- For contractor queries, use employeeType = 'CWK'");
        sb.AppendLine("- For license queries, use extensionAttribute11");
        sb.AppendLine("- Default to output_mode 'all' unless user specifically wants filtered results only");
        sb.AppendLine("- Include enough attributes to answer the user's question fully");
        sb.AppendLine();
        sb.AppendLine("Match attribute reliability (prefer higher reliability):");
        sb.AppendLine("- userPrincipalName, mail: HIGH reliability (unique identifiers)");
        sb.AppendLine("- sAMAccountName: HIGH reliability (unique within domain)");
        sb.AppendLine("- employeeID: HIGH reliability (unique per employee)");
        sb.AppendLine("- displayName: MEDIUM reliability (may have duplicates, use exact match)");

        // Add org-specific displayName format if configured
        var displayFormat = _configuration["OrganizationADSchema:NamingConventions:ActiveUsers:DisplayName"];
        if (!string.IsNullOrWhiteSpace(displayFormat))
        {
            sb.AppendLine();
            sb.AppendLine($"Organization displayName format: {displayFormat}");
            sb.AppendLine("When matching on displayName, expect values in this format.");
        }

        return sb.ToString();
    }

    private string BuildExecutionPlanPrompt(string userQuery, string? context, int? requestedResultLimit)
    {
        // Use external template if loaded, otherwise fall back to built-in
        if (!string.IsNullOrWhiteSpace(_promptTemplate))
        {
            return BuildPromptFromTemplate(userQuery, context, requestedResultLimit);
        }

        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine("Act as an expert Active Directory analyst.");
        promptBuilder.AppendLine("Generate a JSON plan that a C# service can execute without shell commands.");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("PLAN REQUIREMENTS:");
        promptBuilder.AppendLine("- Output strictly valid JSON.");
        promptBuilder.AppendLine("- Use the schema below.");
        promptBuilder.AppendLine("- Supported operations: search, expand_members, lookup, expand_reports.");
        promptBuilder.AppendLine("- search: query a directory object type with filters and attributes.");
        promptBuilder.AppendLine("- expand_members: expand group membership from a prior step (set source, target_type, attributes, recursive flag).");
        promptBuilder.AppendLine("- lookup: fetch related directory objects using distinguished names from a prior step (set source, source_attribute, target_type, attributes).");
        promptBuilder.AppendLine("- expand_reports: RECURSIVELY traverse organizational hierarchy (manager→direct reports) starting from a source step. Use this for queries like 'show everyone under [person]' or 'entire org' or 'reporting structure'. Set source (step providing seed users), target_type (must be User), attributes, optional max_depth (default 10, max 100), optional max_nodes (default 10000, max 50000). This operation is MUCH more efficient than manual multi-level search steps.");
        promptBuilder.AppendLine("- IMPORTANT: When expand_reports follows a person search, the search step MUST include 'size_limit': 1 to ensure only ONE person is found. Otherwise, the expansion will traverse multiple org hierarchies and return incorrect results.");
        promptBuilder.AppendLine("- target_type must be one of: User, Group, Computer, OrganizationalUnit.");
        promptBuilder.AppendLine("- Filters support operators: equals, not_equals, contains, not_contains, starts_with, not_starts_with, ends_with, not_ends_with.");
        promptBuilder.AppendLine("- Use not_equals (or related negations) when the user asks to exclude a value (e.g., `extensionAttribute8` not_equals \"2\").");
        promptBuilder.AppendLine("- Active accounts are enabled and have `AccountExpirationDate` empty (or \"Never\") or in the future; inactive accounts are disabled or have `AccountExpirationDate` populated with a past timestamp. To test for a populated date string, use a `contains \"-\"` filter (dates are formatted as `yyyy-MM-dd HH:mm:ss`) and optionally exclude \"Never\".");
        promptBuilder.AppendLine("- Combine multiple conditions by using a filter with \"operator\" set to \"or\" or \"and\" and a \"conditions\" array of child filters.");
        promptBuilder.AppendLine("- Contractors have `EmployeeType` equal to `CWK`. To find inactive contractors, use an \"or\" filter group that checks for `Enabled` = \"false\" or an `AccountExpirationDate` value that is populated in the past (for example `contains \"-\"` and `not_equals \"Never\"`). Treat blank/\"Never\" as still active.");
        promptBuilder.AppendLine("- Emit every filter value as a string, even for Booleans or dates (for example, \"false\", \"2025-01-31T00:00:00Z\").");
        promptBuilder.AppendLine("- Attribute lists must only include directory attributes (displayName, manager, mail, etc.).");
        promptBuilder.AppendLine("- When the user references licenses or license tiers (E3, E5, etc.), filter on `extensionAttribute11`.");
        promptBuilder.AppendLine("- User displayName values are stored as `LastName, FirstName` with an optional `-X` suffix for the middle initial (for example, `Hernandez, Jose` or `Hernandez, Jose-C`). Match displayName using those exact formats.");
        promptBuilder.AppendLine("- `sAMAccountName` values are the first initial plus last name, truncated to 8 characters (duplicates gain numeric suffixes, e.g., `jhernan`, `jhernan1`). When filtering on `sAMAccountName`, truncate the value accordingly.");
        promptBuilder.AppendLine("- The `manager` attribute contains the full distinguished name (e.g., `CN=Hernandez\\, Jose,OU=Users,OU=NWD,OU=AMER,DC=ad,DC=analog,DC=com`). Always capture the manager's DN in a prior step and feed it directly into manager filters for direct reports.");
        promptBuilder.AppendLine("- When searching for a specific person, build an `or` group covering the canonical displayName formats plus supporting filters on `givenName`, `sn`, `sAMAccountName`, or `userPrincipalName` (for example, `givenName` equals the first name AND `sn` equals the last name). Avoid relying on a single `contains` clause.");
        promptBuilder.AppendLine("- When referencing data from prior steps inside filters, wrap the placeholder with double braces (e.g., `{{manager_lookup.distinguishedName}}`).");
        promptBuilder.AppendLine("- For direct-report queries, locate the manager first, then search users with `manager` equals that manager's distinguished name.");
        promptBuilder.AppendLine("- Projection must describe how to build rows for the caller.");
        promptBuilder.AppendLine("- Aggregation: When user asks to 'summarize', 'group by', 'count by', add an aggregation object to projection with group_by fields (e.g., [\"employeeType\", \"department\"]) and count: true. Maximum 5 group_by fields. Only use allow-listed attributes.");
        promptBuilder.AppendLine("- For 'unique list' or 'distinct values' queries, make projection columns exactly match aggregation group_by fields. The system will automatically return unique values with counts as data rows.");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("RESULT LIMITS - DEFAULT IS UNLIMITED:");
        promptBuilder.AppendLine("- DEFAULT: Return ALL matching records. Set result_limit: null and omit size_limit from steps.");
        promptBuilder.AppendLine("- ONLY set result_limit when user explicitly specifies a count (digits OR words):");
        promptBuilder.AppendLine("  * 'first 10 users' → result_limit: 10, size_limit: 10");
        promptBuilder.AppendLine("  * 'top five contractors' → result_limit: 5, size_limit: 5");
        promptBuilder.AppendLine("  * 'show twenty departments' → result_limit: 20, size_limit: 20");
        promptBuilder.AppendLine("  * 'a dozen managers' → result_limit: 12, size_limit: 12");
        promptBuilder.AppendLine("- UNLIMITED queries (result_limit: null, no size_limit):");
        promptBuilder.AppendLine("  * 'show all users in IT' → unlimited");
        promptBuilder.AppendLine("  * 'everyone under CEO' → unlimited");
        promptBuilder.AppendLine("  * 'list disabled accounts' → unlimited");
        promptBuilder.AppendLine("  * 'unique department names' → unlimited");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("EXAMPLES:");
        promptBuilder.AppendLine("- Query: \"show me the first 10 contractors\" – plan must include \"result_limit\": 10 and the row-producing step must have \"size_limit\": 10.");
        promptBuilder.AppendLine("- Query: \"list the top five helpdesk analysts\" – treat \"five\" as 5 and set both \"result_limit\" and the producing step's \"size_limit\" to 5.");
        promptBuilder.AppendLine("- Query: \"show all IT Digital Workplace Services users\" – leave \"result_limit\" null (no limit) and avoid introducing a size_limit.");
        promptBuilder.AppendLine("- Query: \"show everyone under John Smith\" – use expand_reports: step 1 finds John Smith with size_limit: 1 (critical!), step 2 uses operation expand_reports with source pointing to step 1, omit max_depth/max_nodes to use defaults.");
        promptBuilder.AppendLine("- Query: \"entire org under CEO, summarize by employee type\" – step 1 finds CEO with size_limit: 1 (critical!), step 2 expand_reports with max_depth: 50 and max_nodes: 50000 for full org, projection includes aggregation: { group_by: [\"employeeType\"], count: true }.");
        promptBuilder.AppendLine("- Query: \"show a unique list of all department names\" – search for all users with NO filters, projection columns = [{ name: 'Department', attribute: 'department' }], aggregation = { group_by: ['department'], count: true }. Returns unique departments with counts as data rows.");
        promptBuilder.AppendLine();

        promptBuilder.AppendLine("JSON FORMAT:");
        promptBuilder.AppendLine("```json");
        promptBuilder.AppendLine("{");
        promptBuilder.AppendLine(" \"description\": \"Human-readable summary\",");
        promptBuilder.AppendLine(" \"steps\": [");
        promptBuilder.AppendLine("  {");
        promptBuilder.AppendLine("   \"step\": 1,");
        promptBuilder.AppendLine("   \"name\": \"domain_admins\",");
        promptBuilder.AppendLine("   \"operation\": \"search\",");
        promptBuilder.AppendLine("   \"target_type\": \"Group\",");
        promptBuilder.AppendLine("   \"filters\": [ { \"attribute\": \"name\", \"operator\": \"equals\", \"value\": \"Domain Admins\" } ],");
        promptBuilder.AppendLine("   \"attributes\": [ \"distinguishedName\", \"name\" ],");
        promptBuilder.AppendLine("   \"size_limit\": 25");
        promptBuilder.AppendLine("  },");
        promptBuilder.AppendLine("  {");
        promptBuilder.AppendLine("   \"step\": 2,");
        promptBuilder.AppendLine("   \"name\": \"group_members\",");
        promptBuilder.AppendLine("   \"operation\": \"expand_members\",");
        promptBuilder.AppendLine("   \"source\": \"domain_admins\",");
        promptBuilder.AppendLine("   \"target_type\": \"User\",");
        promptBuilder.AppendLine("   \"recursive\": false,");
        promptBuilder.AppendLine("   \"attributes\": [ \"distinguishedName\", \"displayName\", \"manager\", \"mail\" ]");
        promptBuilder.AppendLine("  },");
        promptBuilder.AppendLine("  {");
        promptBuilder.AppendLine("   \"step\": 3,");
        promptBuilder.AppendLine("   \"name\": \"manager_details\",");
        promptBuilder.AppendLine("   \"operation\": \"lookup\",");
        promptBuilder.AppendLine("   \"source\": \"group_members\",");
        promptBuilder.AppendLine("   \"source_attribute\": \"manager\",");
        promptBuilder.AppendLine("   \"target_type\": \"User\",");
        promptBuilder.AppendLine("   \"attributes\": [ \"distinguishedName\", \"displayName\", \"mail\" ]");
        promptBuilder.AppendLine("  }");
        promptBuilder.AppendLine(" ],");
        promptBuilder.AppendLine(" \"result_limit\": 25,");
        promptBuilder.AppendLine(" \"projection\": {");
        promptBuilder.AppendLine("  \"row_step\": \"group_members\",");
        promptBuilder.AppendLine("  \"columns\": [");
        promptBuilder.AppendLine("   { \"name\": \"User\", \"attribute\": \"displayName\" },");
        promptBuilder.AppendLine("   { \"name\": \"Manager\", \"attribute\": \"displayName\", \"source_step\": \"manager_details\", \"match_on\": \"distinguishedName\", \"match_value_from\": \"manager\" },");
        promptBuilder.AppendLine("   { \"name\": \"ManagerEmail\", \"attribute\": \"mail\", \"source_step\": \"manager_details\", \"match_on\": \"distinguishedName\", \"match_value_from\": \"manager\", \"default\": \"\" }");
        promptBuilder.AppendLine("  ]");
        promptBuilder.AppendLine(" }");
        promptBuilder.AppendLine("}");
        promptBuilder.AppendLine("```");
        promptBuilder.AppendLine();
        promptBuilder.AppendLine("EXAMPLE WITH EXPAND_REPORTS AND AGGREGATION:");
        promptBuilder.AppendLine("```json");
        promptBuilder.AppendLine("{");
        promptBuilder.AppendLine(" \"description\": \"Show entire reporting structure under CEO with employee type summary\",");
        promptBuilder.AppendLine(" \"steps\": [");
        promptBuilder.AppendLine("  {");
        promptBuilder.AppendLine("   \"step\": 1,");
        promptBuilder.AppendLine("   \"name\": \"find_ceo\",");
        promptBuilder.AppendLine("   \"operation\": \"search\",");
        promptBuilder.AppendLine("   \"target_type\": \"User\",");
        promptBuilder.AppendLine("   \"filters\": [ { \"attribute\": \"title\", \"operator\": \"contains\", \"value\": \"Chief Executive\" } ],");
        promptBuilder.AppendLine("   \"attributes\": [ \"distinguishedName\", \"displayName\", \"title\" ],");
        promptBuilder.AppendLine("   \"size_limit\": 1");
        promptBuilder.AppendLine("  },");
        promptBuilder.AppendLine("  {");
        promptBuilder.AppendLine("   \"step\": 2,");
        promptBuilder.AppendLine("   \"name\": \"all_reports\",");
        promptBuilder.AppendLine("   \"operation\": \"expand_reports\",");
        promptBuilder.AppendLine("   \"source\": \"find_ceo\",");
        promptBuilder.AppendLine("   \"target_type\": \"User\",");
        promptBuilder.AppendLine("   \"max_depth\": 50,");
        promptBuilder.AppendLine("   \"max_nodes\": 50000,");
        promptBuilder.AppendLine("   \"attributes\": [ \"distinguishedName\", \"displayName\", \"employeeType\", \"department\", \"title\" ]");
        promptBuilder.AppendLine("  }");
        promptBuilder.AppendLine(" ],");
        promptBuilder.AppendLine(" \"projection\": {");
        promptBuilder.AppendLine("  \"row_step\": \"all_reports\",");
        promptBuilder.AppendLine("  \"columns\": [");
        promptBuilder.AppendLine("   { \"name\": \"Name\", \"attribute\": \"displayName\" },");
        promptBuilder.AppendLine("   { \"name\": \"EmployeeType\", \"attribute\": \"employeeType\" },");
        promptBuilder.AppendLine("   { \"name\": \"Department\", \"attribute\": \"department\" },");
        promptBuilder.AppendLine("   { \"name\": \"Title\", \"attribute\": \"title\" }");
        promptBuilder.AppendLine("  ],");
        promptBuilder.AppendLine("  \"aggregation\": {");
        promptBuilder.AppendLine("   \"group_by\": [ \"employeeType\" ],");
        promptBuilder.AppendLine("   \"count\": true");
        promptBuilder.AppendLine("  }");
        promptBuilder.AppendLine(" }");
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

    private string BuildPromptFromTemplate(string userQuery, string? context, int? requestedResultLimit)
    {
        var prompt = _promptTemplate!;

        // Build result limit guidance
        var resultLimitGuidance = "";
        if (requestedResultLimit.HasValue && requestedResultLimit.Value > 0)
        {
            resultLimitGuidance = $"The user explicitly requested only {requestedResultLimit.Value} rows. Ensure the plan sets \"result_limit\": {requestedResultLimit.Value} and that the search step supplying those rows uses \"size_limit\": {requestedResultLimit.Value}.\n";
        }

        // Build context section
        var contextSection = "";
        if (!string.IsNullOrWhiteSpace(context))
        {
            contextSection = $"CONTEXT:\n{context}\n";
        }

        // Replace placeholders
        prompt = prompt.Replace("{{RESULT_LIMIT_GUIDANCE}}", resultLimitGuidance);
        prompt = prompt.Replace("{{CONTEXT}}", contextSection);
        prompt = prompt.Replace("{{USER_QUERY}}", userQuery);

        return prompt;
    }

    private static string BuildSystemGuidance(IConfiguration configuration)
    {
        var builder = new StringBuilder();

        var displayFormat = configuration["OrganizationADSchema:NamingConventions:ActiveUsers:DisplayName"];
        if (!string.IsNullOrWhiteSpace(displayFormat))
        {
            builder.AppendLine($"- User displayName values follow this format: {displayFormat}. Prefer this when constructing displayName filters.");
        }

        builder.AppendLine("- The `manager` attribute stores the manager's distinguished name (DN). When finding direct reports, drive lookups with distinguishedName values instead of display names or SMTP addresses.");

        var searchPatternsSection = configuration.GetSection("OrganizationADSchema:SearchPatterns");
        foreach (var child in searchPatternsSection.GetChildren())
        {
            if (!string.IsNullOrWhiteSpace(child.Value))
            {
                builder.AppendLine($"- Pattern for {child.Key}: {child.Value}");
            }
        }

        return builder.ToString().Trim();
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

    private void LogProviderFailure(
        string endpoint,
        string effectiveModel,
        HttpStatusCode statusCode,
        LlmProviderErrorDetails details,
        bool authenticationFailure)
    {
        const string message =
            "LLM provider request failed for host {EndpointHost} using model {Model}: " +
            "status {StatusCode}, provider {Provider}, type {ErrorType}, code {ErrorCode}, " +
            "correlation {CorrelationId}";
        var arguments = new object?[]
        {
            GetEndpointHost(endpoint),
            effectiveModel,
            statusCode,
            details.Provider,
            details.Type,
            details.Code,
            details.CorrelationId
        };

        if (authenticationFailure)
        {
            _logger.LogWarning(message, arguments);
        }
        else
        {
            _logger.LogError(message, arguments);
        }
    }

    private void LogProviderProtocolFailure(
        string endpoint,
        string effectiveModel,
        string failureReason)
    {
        _logger.LogWarning(
            "LLM provider returned an unusable response for host {EndpointHost} using model {Model}: {FailureReason}",
            GetEndpointHost(endpoint),
            effectiveModel,
            failureReason);
    }

    private void LogProviderException(string endpoint, string effectiveModel, Exception exception)
    {
        _logger.LogError(
            "LLM provider request for host {EndpointHost} using model {Model} failed with {ExceptionType}",
            GetEndpointHost(endpoint),
            effectiveModel,
            exception.GetType().Name);
    }

    private string GetEndpointHost(string endpoint)
    {
        if (Uri.TryCreate(endpoint, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.Host;
        }

        if (_httpClient.BaseAddress is not null &&
            Uri.TryCreate(_httpClient.BaseAddress, endpoint, out var combinedUri))
        {
            return combinedUri.Host;
        }

        return "unknown";
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
