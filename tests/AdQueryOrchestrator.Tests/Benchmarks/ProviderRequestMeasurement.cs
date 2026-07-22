using System.Net;
using System.Text;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Tests.Benchmarks;

/// <summary>
/// One measurement of the complete rendered provider request for a CSV enrichment
/// plan generation: the exact UTF-8 byte length of the serialized messages body the
/// service would POST, plus the configured output-token reserve. Because the real
/// <see cref="ClaudeService"/> is driven, the prompt text is the production prompt,
/// not a copy that could drift.
/// </summary>
internal readonly record struct ProviderRequestSample(long RequestBodyBytes, int OutputTokenReserve);

/// <summary>
/// Renders the real provider request for a fixture without any live call. A capturing
/// <see cref="HttpMessageHandler"/> intercepts the POST, records the serialized body,
/// and returns a canned success envelope so <c>GenerateCsvEnrichmentPlanAsync</c> runs
/// its full request-building path. No API key of any real provider is used — a dummy
/// key is supplied only to pass the service's "key configured" guard.
/// </summary>
internal static class ProviderRequestMeasurement
{
    /// <summary>
    /// Builds and measures the provider request the enrichment endpoint would send for
    /// the given headers / row count / detected column patterns. Row cell values never
    /// reach the provider (the prompt embeds only header names, the row count, and
    /// pattern descriptions), so callers pass just those.
    /// </summary>
    public static ProviderRequestSample Measure(
        string userQuery,
        List<string> csvHeaders,
        int rowCount,
        Dictionary<string, string> columnPatterns)
    {
        var options = new LlmProviderOptions
        {
            ApiKey = "benchmark-not-a-real-key",
            BaseUrl = "https://provider.invalid/",
            Endpoint = "/v1/messages",
            Model = "claude-sonnet-5",
            MaxTokens = "4000",
            // No prompt template file: force the built-in prompt so measurement is deterministic.
            PromptTemplate = "Configuration/does-not-exist-benchmark.txt",
        };

        var capture = new CapturingHandler();
        using var httpClient = new HttpClient(capture);
        var optionsWrapper = Options.Create(options);
        var configuration = new ConfigurationBuilder().Build();
        var builder = new LlmMessagesRequestBuilder(optionsWrapper);

        var service = new ClaudeService(
            httpClient,
            NullLogger<ClaudeService>.Instance,
            configuration,
            optionsWrapper,
            builder);

        // Fire and forget the parse result; we only need the captured request body.
        _ = service.GenerateCsvEnrichmentPlanAsync(userQuery, csvHeaders, rowCount, default, columnPatterns)
            .GetAwaiter()
            .GetResult();

        if (capture.CapturedBody is null)
        {
            throw new InvalidOperationException("Provider request was not captured.");
        }

        return new ProviderRequestSample(
            RequestBodyBytes: Encoding.UTF8.GetByteCount(capture.CapturedBody),
            OutputTokenReserve: int.Parse(options.MaxTokens));
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? CapturedBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CapturedBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            // Minimal well-formed success envelope so the service's happy path completes.
            const string Body =
                "{\"content\":[{\"text\":\"{\\\"match_column\\\":\\\"Employee\\\","
                + "\\\"match_attribute\\\":\\\"sAMAccountName\\\","
                + "\\\"retrieve_attributes\\\":[\\\"displayName\\\"],"
                + "\\\"output_mode\\\":\\\"all\\\","
                + "\\\"description\\\":\\\"benchmark\\\"}\"}],"
                + "\"usage\":{\"inputTokens\":0,\"outputTokens\":0}}";

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
