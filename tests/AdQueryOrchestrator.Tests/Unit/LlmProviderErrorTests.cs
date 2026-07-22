using System.Net;
using System.Text;
using System.Text.Json;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class LlmProviderErrorTests
{
    private const string BaseModel = "@primary-integration/provider.model";
    private const string ApiKeyCanary = "API_KEY_CANARY_4b61";
    private const string AuthTokenCanary = "AUTH_TOKEN_CANARY_7d29";
    private const string QueryCanary = "QUERY_CANARY_921c";
    private const string ContextCanary = "CONTEXT_CANARY_31fa";
    private const string RawBodyCanary = "RAW_BODY_CANARY_8e42";
    private const string PromptLine = "Produce only the JSON plan. Do not include explanations.";
    private const string NormalSystemLine =
        "- The `manager` attribute stores the manager's distinguished name (DN). When finding direct reports, drive lookups with distinguishedName values instead of display names or SMTP addresses.";
    private const string CsvSystemLine =
        "You are an expert Active Directory analyst. Your role is to interpret user requests about CSV data enrichment and output structured JSON instructions.";

    [Theory]
    [InlineData(
        "{\"error\":{\"message\":\"vertex failure\",\"type\":\"invalid_request_error\",\"code\":null},\"provider\":\"vertex-ai\"}",
        "vertex failure",
        "vertex-ai",
        "invalid_request_error",
        null,
        null)]
    [InlineData(
        "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"message\":\"anthropic failure\"},\"request_id\":\"req_body_123\"}",
        "anthropic failure",
        null,
        "invalid_request_error",
        null,
        "req_body_123")]
    [InlineData(
        "{\"error\":{\"message\":\"openai failure\",\"type\":\"invalid_request_error\",\"code\":\"unsupported_value\"}}",
        "openai failure",
        null,
        "invalid_request_error",
        "unsupported_value",
        null)]
    [InlineData(
        "{\"provider\":\"gateway\",\"error\":{\"message\":\"outer\",\"type\":\"outer_type\",\"error\":{\"message\":\"inner\",\"type\":\"inner_type\",\"code\":42}}}",
        "inner",
        "gateway",
        "inner_type",
        "42",
        null)]
    public void Parse_KnownProviderNeutralEnvelopesRetainStructuredDetails(
        string body,
        string expectedMessage,
        string? expectedProvider,
        string? expectedType,
        string? expectedCode,
        string? expectedCorrelationId)
    {
        var details = LlmProviderErrorParser.Parse(
            new LlmProviderErrorBody(body, IsTruncated: false),
            responseCorrelationId: null,
            sensitiveValues: []);

        Assert.Equal(expectedMessage, details.Message);
        Assert.Equal(expectedProvider, details.Provider);
        Assert.Equal(expectedType, details.Type);
        Assert.Equal(expectedCode, details.Code);
        Assert.Equal(expectedCorrelationId, details.CorrelationId);
    }

    [Fact]
    public void Parse_RedactsNormalizesAndBoundsEveryRetainedField()
    {
        const string secret = "SENSITIVE_CANARY_f43a";
        var body = JsonSerializer.Serialize(
            new
            {
                error = new
                {
                    message = $"message {secret}\r\n\u0001\u200B{new string('M', 600)}",
                    type = new string('T', 200) + "\r\n\u200B",
                    code = new string('C', 200) + "\t"
                },
                provider = new string('P', 200) + "\n",
                request_id = "body-correlation"
            });

        var details = LlmProviderErrorParser.Parse(
            new LlmProviderErrorBody(body, IsTruncated: false),
            responseCorrelationId: new string('R', 200) + "\u200B",
            sensitiveValues: [secret]);

        Assert.Equal(LlmProviderErrorParser.MaxMessageCharacters, details.Message.Length);
        Assert.Equal(128, Assert.IsType<string>(details.Provider).Length);
        Assert.Equal(128, Assert.IsType<string>(details.Type).Length);
        Assert.Equal(128, Assert.IsType<string>(details.Code).Length);
        Assert.Equal(128, Assert.IsType<string>(details.CorrelationId).Length);
        var retained = string.Join(
            " ",
            details.Message,
            details.Provider,
            details.Type,
            details.Code,
            details.CorrelationId);
        Assert.DoesNotContain(secret, retained, StringComparison.Ordinal);
        Assert.DoesNotContain(
            retained,
            character =>
                char.IsControl(character) ||
                char.GetUnicodeCategory(character) == System.Globalization.UnicodeCategory.Format);
    }

    [Fact]
    public void Parse_ExcessiveRedactionWorkFailsClosed()
    {
        const string body =
            "{\"error\":{\"message\":\"provider detail\",\"type\":\"type\",\"code\":\"code\"},\"provider\":\"provider\"}";
        var tooManyCharacters = new string(
            'S',
            LlmProviderErrorParser.MaxSensitiveInputCharacters + 1);
        var tooManyCandidates = string.Join(
            '\n',
            Enumerable.Range(0, LlmProviderErrorParser.MaxRedactionCandidates + 1)
                .Select(index => $"sensitive-line-{index:D4}"));

        foreach (var sensitiveValue in new[] { tooManyCharacters, tooManyCandidates })
        {
            var details = LlmProviderErrorParser.Parse(
                new LlmProviderErrorBody(body, IsTruncated: false),
                responseCorrelationId: "correlation",
                sensitiveValues: [sensitiveValue]);

            Assert.Equal("Provider returned an error response.", details.Message);
            Assert.Null(details.Provider);
            Assert.Null(details.Type);
            Assert.Null(details.Code);
            Assert.Null(details.CorrelationId);
        }
    }

    [Fact]
    public async Task NormalRequest_ReportedVertexFailureReturnsSafeMessageAndMetadataLog()
    {
        const string body =
            "{\"error\":{\"message\":\"vertex-ai error: `temperature` is deprecated for this model.\",\"type\":\"invalid_request_error\",\"param\":null,\"code\":null},\"provider\":\"vertex-ai\"}";
        var providerResponse = ErrorResponse(HttpStatusCode.BadRequest, body);
        providerResponse.Headers.TryAddWithoutValidation("x-portkey-trace-id", "trace-123");
        var logger = new CapturingLogger<ClaudeService>();
        var service = CreateService(new QueueHttpMessageHandler(providerResponse), logger);

        var response = await service.GenerateExecutionPlanAsync(
            "safe query",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Empty(response.RawResponse);
        Assert.Equal(
            "Claude API error: BadRequest - vertex-ai error: `temperature` is deprecated for this model.",
            response.ErrorMessage);

        var failure = Assert.Single(
            logger.Entries,
            entry => entry.Level == LogLevel.Error && entry.State.ContainsKey("StatusCode"));
        Assert.Equal("provider.example", StateValue(failure, "EndpointHost"));
        Assert.Equal(BaseModel, StateValue(failure, "Model"));
        Assert.Equal("BadRequest", StateValue(failure, "StatusCode"));
        Assert.Equal("vertex-ai", StateValue(failure, "Provider"));
        Assert.Equal("invalid_request_error", StateValue(failure, "ErrorType"));
        Assert.Null(failure.State["ErrorCode"]);
        Assert.Equal("trace-123", StateValue(failure, "CorrelationId"));
        Assert.DoesNotContain("temperature", FlattenLogs(logger), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CsvRequest_DoubleNestedFailureUsesTheSameParserAndRetainsCodeInLog()
    {
        const string body =
            "{\"error\":{\"error\":{\"message\":\"Unsupported parameter.\",\"type\":\"invalid_request_error\",\"code\":\"unsupported_value\"}}}";
        var logger = new CapturingLogger<ClaudeService>();
        var service = CreateService(
            new QueueHttpMessageHandler(ErrorResponse(HttpStatusCode.TooManyRequests, body)),
            logger);

        var response = await service.GenerateCsvEnrichmentPlanAsync(
            "safe query",
            ["employee"],
            rowCount: 3,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Empty(response.RawResponse);
        Assert.Equal(
            "Claude API error: TooManyRequests - Unsupported parameter.",
            response.ErrorMessage);
        var failure = Assert.Single(
            logger.Entries,
            entry => entry.Level == LogLevel.Error && entry.State.ContainsKey("StatusCode"));
        Assert.Equal("invalid_request_error", StateValue(failure, "ErrorType"));
        Assert.Equal("unsupported_value", StateValue(failure, "ErrorCode"));
        Assert.DoesNotContain("Unsupported parameter", FlattenLogs(logger), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProviderMetadata_RedactsSensitiveValuesBeforeStructuredLogging()
    {
        var body = JsonSerializer.Serialize(
            new
            {
                error = new
                {
                    message = "safe provider message",
                    type = $"type-{AuthTokenCanary}",
                    code = $"code-{QueryCanary}"
                },
                provider = $"provider-{ApiKeyCanary}"
            });
        var providerResponse = ErrorResponse(HttpStatusCode.BadRequest, body);
        providerResponse.Headers.TryAddWithoutValidation(
            "x-portkey-trace-id",
            $"trace-{ContextCanary}");
        var logger = new CapturingLogger<ClaudeService>();
        var service = CreateService(
            new QueueHttpMessageHandler(providerResponse),
            logger,
            new Dictionary<string, string?>
            {
                ["Claude:ApiKey"] = ApiKeyCanary,
                ["Claude:BaseUrl"] = "https://api.portkey.ai",
                ["Claude:AuthToken"] = AuthTokenCanary
            });

        var response = await service.GenerateExecutionPlanAsync(
            QueryCanary,
            ContextCanary,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        var failure = Assert.Single(
            logger.Entries,
            entry => entry.Level == LogLevel.Error && entry.State.ContainsKey("StatusCode"));
        var retainedMetadata = string.Join(
            " ",
            StateValue(failure, "Provider"),
            StateValue(failure, "ErrorType"),
            StateValue(failure, "ErrorCode"),
            StateValue(failure, "CorrelationId"));
        Assert.Contains("[redacted]", retainedMetadata, StringComparison.Ordinal);
        AssertNoValues(
            retainedMetadata,
            ApiKeyCanary,
            AuthTokenCanary,
            QueryCanary,
            ContextCanary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProviderFailure_DoesNotExposeRequestSecretsPromptsOrRawBody(bool csvRequest)
    {
        var systemLine = csvRequest ? CsvSystemLine : NormalSystemLine;
        var sensitiveMessage = string.Join(
            "\r\n",
            "SAFE_PROVIDER_MESSAGE",
            ApiKeyCanary,
            AuthTokenCanary,
            QueryCanary,
            csvRequest ? string.Empty : ContextCanary,
            systemLine,
            PromptLine,
            new string('A', 1_000));
        var body = JsonSerializer.Serialize(
            new
            {
                error = new
                {
                    message = sensitiveMessage,
                    type = $"invalid_request_error {ApiKeyCanary}\r\nTYPE_INJECTION",
                    code = $"bad_request {AuthTokenCanary}\tCODE_INJECTION"
                },
                provider = $"gateway {QueryCanary}\nPROVIDER_INJECTION",
                raw = RawBodyCanary
            });
        var providerResponse = ErrorResponse(HttpStatusCode.BadRequest, body);
        providerResponse.Headers.TryAddWithoutValidation(
            "x-portkey-trace-id",
            $"trace-{ApiKeyCanary}");
        var logger = new CapturingLogger<ClaudeService>();
        var service = CreateService(
            new QueueHttpMessageHandler(providerResponse),
            logger,
            new Dictionary<string, string?>
            {
                ["Claude:ApiKey"] = ApiKeyCanary,
                ["Claude:BaseUrl"] = "https://api.portkey.ai",
                ["Claude:AuthToken"] = AuthTokenCanary
            });

        var result = await InvokeAsync(service, csvRequest, QueryCanary, ContextCanary);
        var publicAndLogs = string.Concat(result.ErrorMessage, Environment.NewLine, FlattenLogs(logger));

        Assert.False(result.Success);
        Assert.Empty(result.RawResponse);
        Assert.Contains("SAFE_PROVIDER_MESSAGE", result.ErrorMessage, StringComparison.Ordinal);
        Assert.InRange(result.ErrorMessage.Length, 1, 600);
        Assert.DoesNotContain(result.ErrorMessage, character => char.IsControl(character));
        AssertNoValues(
            publicAndLogs,
            ApiKeyCanary,
            AuthTokenCanary,
            QueryCanary,
            systemLine,
            PromptLine,
            RawBodyCanary);
        if (!csvRequest)
        {
            Assert.DoesNotContain(ContextCanary, publicAndLogs, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    [InlineData(false, "malformed")]
    [InlineData(true, "malformed")]
    [InlineData(false, "unknown")]
    [InlineData(true, "unknown")]
    [InlineData(false, "oversized")]
    [InlineData(true, "oversized")]
    public async Task UnknownProviderFailure_UsesFixedBoundedFallback(
        bool csvRequest,
        string bodyKind)
    {
        var body = bodyKind switch
        {
            "empty" => string.Empty,
            "malformed" => $"<html>{RawBodyCanary}",
            "unknown" => $"{{\"unexpected\":\"{RawBodyCanary}\"}}",
            "oversized" => JsonSerializer.Serialize(
                new
                {
                    error = new
                    {
                        message = RawBodyCanary + new string('X', LlmProviderErrorParser.MaxBodyBytes)
                    }
                }),
            _ => throw new InvalidOperationException("Unknown test body kind.")
        };
        var logger = new CapturingLogger<ClaudeService>();
        var service = CreateService(
            new QueueHttpMessageHandler(ErrorResponse(HttpStatusCode.BadRequest, body)),
            logger);

        var result = await InvokeAsync(service, csvRequest, "safe query", context: null);

        Assert.False(result.Success);
        Assert.Empty(result.RawResponse);
        Assert.Equal(
            "Claude API error: BadRequest - Provider returned an error response.",
            result.ErrorMessage);
        Assert.DoesNotContain(RawBodyCanary, result.ErrorMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(RawBodyCanary, FlattenLogs(logger), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ErrorResponseStreamingRead_StopsAtTheByteLimit(bool csvRequest)
    {
        var payload = Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(
                new
                {
                    error = new
                    {
                        message = new string('X', LlmProviderErrorParser.MaxBodyBytes * 2)
                    }
                }));
        var content = new CountingStreamingContent(payload);
        var providerResponse = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = content
        };
        var logger = new CapturingLogger<ClaudeService>();
        var service = CreateService(new QueueHttpMessageHandler(providerResponse), logger);

        var result = await InvokeAsync(service, csvRequest, "safe query", context: null);

        Assert.False(result.Success);
        Assert.Equal(LlmProviderErrorParser.MaxBodyBytes + 1, content.TransferredBytes);
        Assert.True(content.TransferredBytes < payload.Length);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.BadRequest)]
    public async Task NormalRequest_CredentialFailurePreservesExistingFixedMessage(
        HttpStatusCode statusCode)
    {
        var logger = new CapturingLogger<ClaudeService>();
        var service = CreateService(
            new QueueHttpMessageHandler(
                ErrorResponse(
                    statusCode,
                    $"{{\"error\":{{\"message\":\"API Key Not Found {RawBodyCanary}\"}}}}")),
            logger);

        var response = await service.GenerateExecutionPlanAsync(
            "safe query",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(response.Success);
        Assert.Equal(
            "Claude API key is missing or invalid. Please verify Claude:ApiKey.",
            response.ErrorMessage);
        Assert.DoesNotContain(RawBodyCanary, FlattenLogs(logger), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TransportException_DoesNotLogExceptionMessageOrRequestContent(bool csvRequest)
    {
        var exceptionMessage = $"{ApiKeyCanary} {AuthTokenCanary} {QueryCanary} {ContextCanary}";
        var logger = new CapturingLogger<ClaudeService>();
        var service = CreateService(
            new ThrowingHttpMessageHandler(new InvalidOperationException(exceptionMessage)),
            logger,
            new Dictionary<string, string?>
            {
                ["Claude:ApiKey"] = ApiKeyCanary,
                ["Claude:BaseUrl"] = "https://api.portkey.ai",
                ["Claude:AuthToken"] = AuthTokenCanary
            });

        var result = await InvokeAsync(service, csvRequest, QueryCanary, ContextCanary);
        var publicAndLogs = string.Concat(result.ErrorMessage, Environment.NewLine, FlattenLogs(logger));

        Assert.False(result.Success);
        AssertNoValues(
            publicAndLogs,
            ApiKeyCanary,
            AuthTokenCanary,
            QueryCanary,
            ContextCanary);
        Assert.Contains("InvalidOperationException", publicAndLogs, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InvalidSuccessfulPayload_IsNotReturnedOrLogged(bool csvRequest)
    {
        var logger = new CapturingLogger<ClaudeService>();
        var service = CreateService(
            new QueueHttpMessageHandler(
                ErrorResponse(
                    HttpStatusCode.OK,
                    $"{{\"unexpected\":\"{RawBodyCanary} {QueryCanary}\"}}")),
            logger);

        var result = await InvokeAsync(service, csvRequest, QueryCanary, context: null);
        var publicAndLogs = string.Concat(result.ErrorMessage, Environment.NewLine, FlattenLogs(logger));

        Assert.False(result.Success);
        Assert.Empty(result.RawResponse);
        AssertNoValues(publicAndLogs, RawBodyCanary, QueryCanary);
    }

    private static async Task<InvocationResult> InvokeAsync(
        ClaudeService service,
        bool csvRequest,
        string query,
        string? context)
    {
        if (csvRequest)
        {
            var response = await service.GenerateCsvEnrichmentPlanAsync(
                query,
                ["employee"],
                rowCount: 3,
                cancellationToken: TestContext.Current.CancellationToken);
            return new InvocationResult(response.Success, response.ErrorMessage ?? string.Empty, response.RawResponse);
        }

        var normalResponse = await service.GenerateExecutionPlanAsync(
            query,
            context,
            cancellationToken: TestContext.Current.CancellationToken);
        return new InvocationResult(
            normalResponse.Success,
            normalResponse.ErrorMessage ?? string.Empty,
            normalResponse.RawResponse);
    }

    private static ClaudeService CreateService(
        HttpMessageHandler handler,
        CapturingLogger<ClaudeService> logger,
        IReadOnlyDictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            ["Claude:ApiKey"] = "test-api-key",
            ["Claude:BaseUrl"] = "https://provider.example",
            ["Claude:Endpoint"] = "/v1/messages",
            ["Claude:Model"] = BaseModel,
            ["Claude:MaxTokens"] = "1234",
            ["Claude:PromptTemplate"] = "missing-provider-error-test-template.txt"
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
            logger,
            configuration,
            providerOptions,
            new LlmMessagesRequestBuilder(providerOptions));
    }

    private static HttpResponseMessage ErrorResponse(HttpStatusCode statusCode, string body)
    {
        return new HttpResponseMessage(statusCode)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    private static string StateValue(LogEntry entry, string key)
    {
        return entry.State[key]?.ToString() ?? string.Empty;
    }

    private static string FlattenLogs(CapturingLogger<ClaudeService> logger)
    {
        return string.Join(
            Environment.NewLine,
            logger.Entries.Select(entry => string.Join(
                " | ",
                entry.Message,
                entry.Exception?.ToString() ?? string.Empty,
                string.Join(
                    "; ",
                    entry.State.Select(value => $"{value.Key}={value.Value}")))));
    }

    private static void AssertNoValues(string actual, params string[] forbiddenValues)
    {
        foreach (var forbiddenValue in forbiddenValues)
        {
            Assert.DoesNotContain(forbiddenValue, actual, StringComparison.Ordinal);
        }
    }

    private sealed record InvocationResult(bool Success, string ErrorMessage, string RawResponse);

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> State);

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
        {
            return null;
        }

        public bool IsEnabled(LogLevel logLevel)
        {
            return true;
        }

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var structuredState = state is IEnumerable<KeyValuePair<string, object?>> values
                ? values.ToDictionary(value => value.Key, value => value.Value, StringComparer.Ordinal)
                : new Dictionary<string, object?>(StringComparer.Ordinal);
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception, structuredState));
        }
    }

    private sealed class CountingStreamingContent : HttpContent
    {
        private readonly byte[] _payload;
        private long _transferredBytes;

        public CountingStreamingContent(byte[] payload)
        {
            _payload = payload;
        }

        public long TransferredBytes => Interlocked.Read(ref _transferredBytes);

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            Interlocked.Add(ref _transferredBytes, _payload.Length);
            return stream.WriteAsync(_payload, 0, _payload.Length);
        }

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            return Task.FromResult<Stream>(
                new CountingMemoryStream(
                    _payload,
                    count => Interlocked.Add(ref _transferredBytes, count)));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _payload.Length;
            return true;
        }
    }

    private sealed class CountingMemoryStream : MemoryStream
    {
        private readonly Action<int> _countRead;

        public CountingMemoryStream(byte[] buffer, Action<int> countRead)
            : base(buffer, writable: false)
        {
            _countRead = countRead;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = base.Read(buffer.Span);
            _countRead(read);
            return ValueTask.FromResult(read);
        }
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses;

        public QueueHttpMessageHandler(params HttpResponseMessage[] responses)
        {
            _responses = new Queue<HttpResponseMessage>(responses);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No provider response was queued for the test.");
            }

            return Task.FromResult(_responses.Dequeue());
        }
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingHttpMessageHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            return Task.FromException<HttpResponseMessage>(_exception);
        }
    }
}
