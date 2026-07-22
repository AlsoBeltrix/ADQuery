using System.Net;
using System.Text;
using System.Text.Json;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class LlmProviderRequestTests
{
    private const string BaseModel = "@primary-integration/provider.model";
    private const string AlternateModel = "@alternate-integration/other.model";

    [Fact]
    public async Task GenerateExecutionPlanAsync_PreservesRequiredWireContractAndDirectHeaders()
    {
        var handler = new RecordingHttpMessageHandler();
        var service = CreateService(handler);

        var response = await service.GenerateExecutionPlanAsync(
            "show active users",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://provider.example/v1/messages", request.Uri.AbsoluteUri);
        Assert.Equal("test-api-key", Assert.Single(request.Headers["x-api-key"]));
        Assert.Equal("2023-06-01", Assert.Single(request.Headers["anthropic-version"]));
        AssertRequiredWireContract(request.Body, BaseModel, "show active users");
    }

    [Fact]
    public async Task GenerateCsvEnrichmentPlanAsync_PreservesRequiredWireContract()
    {
        var handler = new RecordingHttpMessageHandler();
        var service = CreateService(handler);

        var response = await service.GenerateCsvEnrichmentPlanAsync(
            "add department",
            ["employee"],
            rowCount: 3,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(response.Success);
        var request = Assert.Single(handler.Requests);
        AssertRequiredWireContract(request.Body, BaseModel, "CSV FILE INFO:");
        var prompt = GetUserMessageContent(request.Body);
        Assert.Contains("all|filtered", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("all|matched", prompt, StringComparison.Ordinal);
        Assert.Contains(
            "Output mode: 'all' (include unmatched rows) or 'filtered' (only filtered rows)",
            prompt,
            StringComparison.Ordinal);
        Assert.DoesNotContain("'matched'", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GenerateExecutionPlanAsync_PreservesExactModelOverrideAndPortkeyHeaders()
    {
        var handler = new RecordingHttpMessageHandler();
        var service = CreateService(
            handler,
            new Dictionary<string, string?>
            {
                ["Claude:BaseUrl"] = "https://api.portkey.ai",
                ["Claude:AuthToken"] = "test-auth-token"
            });

        var response = await service.GenerateExecutionPlanAsync(
            "show active users",
            cancellationToken: TestContext.Current.CancellationToken,
            modelOverride: AlternateModel);

        Assert.True(response.Success);
        Assert.Equal(AlternateModel, response.ModelUsed);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("test-api-key", Assert.Single(request.Headers["x-portkey-api-key"]));
        Assert.Equal("Bearer test-auth-token", Assert.Single(request.Headers["Authorization"]));
        AssertRequiredWireContract(request.Body, AlternateModel, "show active users");
    }

    [Fact]
    public async Task CheckHealthAsync_UsesTheNormalGenerationRequestPath()
    {
        var handler = new RecordingHttpMessageHandler();
        var service = CreateService(handler);

        var response = await service.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.True(response.IsHealthy);
        var request = Assert.Single(handler.Requests);
        AssertRequiredWireContract(request.Body, BaseModel, "Return a simple confirmation");
    }

    [Fact]
    public async Task ExactBaseProfile_AppliesTemperatureAcrossNormalCsvAndHealthRequests()
    {
        var handler = new RecordingHttpMessageHandler();
        var service = CreateService(
            handler,
            new Dictionary<string, string?>
            {
                ["Claude:SamplingProfiles:0:TargetModel"] = BaseModel,
                ["Claude:SamplingProfiles:0:Mode"] = LlmSamplingModes.Temperature,
                ["Claude:SamplingProfiles:0:Temperature"] = "0.25"
            });

        var normalResponse = await service.GenerateExecutionPlanAsync(
            "show active users",
            cancellationToken: TestContext.Current.CancellationToken);
        var csvResponse = await service.GenerateCsvEnrichmentPlanAsync(
            "add department",
            ["employee"],
            rowCount: 3,
            cancellationToken: TestContext.Current.CancellationToken);
        var healthResponse = await service.CheckHealthAsync(TestContext.Current.CancellationToken);

        Assert.True(normalResponse.Success);
        Assert.True(csvResponse.Success);
        Assert.True(healthResponse.IsHealthy);
        Assert.Equal(3, handler.Requests.Count);
        AssertRequiredWireContract(handler.Requests[0].Body, BaseModel, "show active users", 0.25);
        AssertRequiredWireContract(handler.Requests[1].Body, BaseModel, "CSV FILE INFO:", 0.25);
        AssertRequiredWireContract(handler.Requests[2].Body, BaseModel, "Return a simple confirmation", 0.25);
    }

    [Fact]
    public async Task AlternateProfile_AppliesOnlyToItsExactModelOverride()
    {
        const string arbitraryModel = "@unconfigured-integration/arbitrary.model";
        var handler = new RecordingHttpMessageHandler();
        var service = CreateService(
            handler,
            new Dictionary<string, string?>
            {
                ["Claude:SamplingProfiles:0:TargetModel"] = AlternateModel,
                ["Claude:SamplingProfiles:0:Mode"] = LlmSamplingModes.Temperature,
                ["Claude:SamplingProfiles:0:Temperature"] = "0.65"
            });

        var normalResponse = await service.GenerateExecutionPlanAsync(
            "show active users",
            cancellationToken: TestContext.Current.CancellationToken);
        var csvResponse = await service.GenerateCsvEnrichmentPlanAsync(
            "add department",
            ["employee"],
            rowCount: 3,
            cancellationToken: TestContext.Current.CancellationToken);
        var healthResponse = await service.CheckHealthAsync(TestContext.Current.CancellationToken);
        var alternateResponse = await service.GenerateExecutionPlanAsync(
            "show alternate users",
            cancellationToken: TestContext.Current.CancellationToken,
            modelOverride: AlternateModel);
        var arbitraryResponse = await service.GenerateExecutionPlanAsync(
            "show arbitrary users",
            cancellationToken: TestContext.Current.CancellationToken,
            modelOverride: arbitraryModel);

        Assert.True(normalResponse.Success);
        Assert.True(csvResponse.Success);
        Assert.True(healthResponse.IsHealthy);
        Assert.True(alternateResponse.Success);
        Assert.True(arbitraryResponse.Success);
        Assert.Equal(AlternateModel, alternateResponse.ModelUsed);
        Assert.Equal(arbitraryModel, arbitraryResponse.ModelUsed);
        Assert.Equal(5, handler.Requests.Count);
        AssertRequiredWireContract(handler.Requests[0].Body, BaseModel, "show active users");
        AssertRequiredWireContract(handler.Requests[1].Body, BaseModel, "CSV FILE INFO:");
        AssertRequiredWireContract(handler.Requests[2].Body, BaseModel, "Return a simple confirmation");
        AssertRequiredWireContract(handler.Requests[3].Body, AlternateModel, "show alternate users", 0.65);
        AssertRequiredWireContract(handler.Requests[4].Body, arbitraryModel, "show arbitrary users");
    }

    private static ClaudeService CreateService(
        RecordingHttpMessageHandler handler,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Claude:ApiKey"] = "test-api-key",
            ["Claude:BaseUrl"] = "https://provider.example",
            ["Claude:Endpoint"] = "/v1/messages",
            ["Claude:Model"] = BaseModel,
            ["Claude:MaxTokens"] = "1234",
            ["Claude:PromptTemplate"] = "missing-test-prompt-template.txt",
            ["OrganizationADSchema:NamingConventions:ActiveUsers:DisplayName"] = "Last, First"
        };

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                settings[key] = value;
            }
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var providerOptions = Options.Create(
            configuration
                .GetSection(LlmProviderOptions.SectionName)
                .Get<LlmProviderOptions>() ?? new LlmProviderOptions());

        return new ClaudeService(
            new HttpClient(handler),
            NullLogger<ClaudeService>.Instance,
            configuration,
            providerOptions,
            new LlmMessagesRequestBuilder(providerOptions));
    }

    private static void AssertRequiredWireContract(
        string body,
        string expectedModel,
        string expectedPromptContent,
        double? expectedTemperature = null)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        Assert.Equal(
            expectedTemperature.HasValue
                ? ["model", "max_tokens", "temperature", "system", "messages"]
                : ["model", "max_tokens", "system", "messages"],
            root.EnumerateObject().Select(property => property.Name));

        var model = root.GetProperty("model");
        Assert.Equal(JsonValueKind.String, model.ValueKind);
        Assert.Equal(expectedModel, model.GetString());

        var maxTokens = root.GetProperty("max_tokens");
        Assert.Equal(JsonValueKind.Number, maxTokens.ValueKind);
        Assert.Equal(1234, maxTokens.GetInt32());

        if (expectedTemperature.HasValue)
        {
            var temperature = root.GetProperty("temperature");
            Assert.Equal(JsonValueKind.Number, temperature.ValueKind);
            Assert.Equal(expectedTemperature.Value, temperature.GetDouble());
        }
        else
        {
            Assert.False(root.TryGetProperty("temperature", out _));
        }

        var system = root.GetProperty("system");
        Assert.Equal(JsonValueKind.String, system.ValueKind);

        var messages = root.GetProperty("messages");
        Assert.Equal(JsonValueKind.Array, messages.ValueKind);
        var message = Assert.Single(messages.EnumerateArray());
        Assert.Equal(JsonValueKind.Object, message.ValueKind);
        Assert.Equal(
            ["role", "content"],
            message.EnumerateObject().Select(property => property.Name));
        Assert.Equal("user", message.GetProperty("role").GetString());

        var content = message.GetProperty("content");
        Assert.Equal(JsonValueKind.String, content.ValueKind);
        Assert.Contains(expectedPromptContent, content.GetString(), StringComparison.Ordinal);
    }

    private static string GetUserMessageContent(string body)
    {
        using var document = JsonDocument.Parse(body);
        return document.RootElement
            .GetProperty("messages")[0]
            .GetProperty("content")
            .GetString() ?? string.Empty;
    }

    private sealed class RecordingHttpMessageHandler : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var headers = request.Headers.ToDictionary(
                header => header.Key,
                header => header.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);

            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri ?? throw new InvalidOperationException("Request URI was not set."),
                headers,
                body));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"content\":[{\"text\":\"{}\"}],\"usage\":{\"input_tokens\":3,\"output_tokens\":4}}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }

    private sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        IReadOnlyDictionary<string, string[]> Headers,
        string Body);
}
