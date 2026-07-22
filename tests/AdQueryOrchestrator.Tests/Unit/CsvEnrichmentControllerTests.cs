using System.Security.Claims;
using System.Text;
using System.Text.Json;
using AdQuery.Orchestrator.Controllers;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class CsvEnrichmentControllerTests
{
    [Fact]
    public async Task CsvEnrich_ValidationFailureReturns400WithoutPublication()
    {
        var plan = CreatePlan();
        plan.RetrieveAttributes = null!;
        var enrichment = new StubCsvEnrichmentService(new CsvEnrichmentResult
        {
            FailureKind = CsvEnrichmentFailureKind.Validation,
            Errors = ["retrieve_attributes is required."]
        });
        using var cache = new RecordingMemoryCache();
        var writer = new RecordingResultWriter(
            Path.Combine(Path.GetTempPath(), "unused-validation.csv"),
            throwOnWrite: true);
        var idGenerator = new RecordingResultIdGenerator();
        var logger = new CapturingLogger<QueryController>();
        var controller = CreateController(plan, enrichment, cache, writer, idGenerator, logger);

        var action = await controller.CsvEnrich(CreateRequest());

        var response = Assert.IsType<BadRequestObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        var body = SerializeBody(response);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal("CSV enrichment plan is invalid.", body.GetProperty("error").GetString());
        Assert.Equal("retrieve_attributes is required.", body.GetProperty("errors")[0].GetString());
        AssertFailureBodyHasNoPublishedResult(body);
        Assert.Equal(1, enrichment.Calls);
        Assert.Empty(writer.Calls);
        Assert.Equal(0, idGenerator.Calls);
        Assert.Empty(cache.CreatedKeys);
        AssertNoCompletionLog(logger);
    }

    [Fact]
    public async Task CsvEnrich_DirectoryFailureReturns500WithoutPublicationOrPartialPreview()
    {
        const string SensitiveSentinel = "SENSITIVE_PARTIAL_RESULT";
        var enrichment = new StubCsvEnrichmentService(new CsvEnrichmentResult
        {
            FailureKind = CsvEnrichmentFailureKind.DirectoryOperation,
            TotalRows = 2,
            MatchedRows = 1,
            FilteredRows = 1,
            Data =
            [
                new Dictionary<string, object?>
                {
                    ["Employee"] = SensitiveSentinel
                }
            ],
            Errors = [SensitiveSentinel]
        });
        using var cache = new RecordingMemoryCache();
        var writer = new RecordingResultWriter(
            Path.Combine(Path.GetTempPath(), "unused-directory.csv"),
            throwOnWrite: true);
        var idGenerator = new RecordingResultIdGenerator();
        var logger = new CapturingLogger<QueryController>();
        var controller = CreateController(CreatePlan(), enrichment, cache, writer, idGenerator, logger);

        var action = await controller.CsvEnrich(CreateRequest());

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, response.StatusCode);
        var body = SerializeBody(response);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Equal(
            "Active Directory lookup failed. No enrichment result was produced.",
            body.GetProperty("error").GetString());
        AssertFailureBodyHasNoPublishedResult(body);
        Assert.DoesNotContain(SensitiveSentinel, body.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveSentinel, Flatten(logger), StringComparison.Ordinal);
        Assert.Equal(1, enrichment.Calls);
        Assert.Empty(writer.Calls);
        Assert.Equal(0, idGenerator.Calls);
        Assert.Empty(cache.CreatedKeys);
        AssertNoCompletionLog(logger);
    }

    [Fact]
    public async Task CsvEnrich_CancellationReturns408WithoutPublication()
    {
        var enrichment = new StubCsvEnrichmentService(
            new OperationCanceledException("directory request canceled"));
        using var cache = new RecordingMemoryCache();
        var writer = new RecordingResultWriter(
            Path.Combine(Path.GetTempPath(), "unused-cancellation.csv"),
            throwOnWrite: true);
        var idGenerator = new RecordingResultIdGenerator();
        var logger = new CapturingLogger<QueryController>();
        var controller = CreateController(CreatePlan(), enrichment, cache, writer, idGenerator, logger);

        var action = await controller.CsvEnrich(CreateRequest());

        var response = Assert.IsType<ObjectResult>(action.Result);
        Assert.Equal(StatusCodes.Status408RequestTimeout, response.StatusCode);
        var body = SerializeBody(response);
        Assert.Equal("Request was cancelled or timed out", body.GetProperty("error").GetString());
        AssertFailureBodyHasNoPublishedResult(body);
        Assert.Equal(1, enrichment.Calls);
        Assert.Empty(writer.Calls);
        Assert.Equal(0, idGenerator.Calls);
        Assert.Empty(cache.CreatedKeys);
        AssertNoCompletionLog(logger);
    }

    [Fact]
    public async Task CsvEnrich_SuccessPublishesAndCachesExactlyOnce()
    {
        using var temporaryDirectory = new TemporaryDirectory();
        const string ResultId = "csv-result-123";
        var outputPath = Path.Combine(temporaryDirectory.Path, "result.csv");
        var enrichment = new StubCsvEnrichmentService(new CsvEnrichmentResult
        {
            Success = true,
            TotalRows = 2,
            MatchedRows = 2,
            FilteredRows = 2,
            Data =
            [
                CreateOutputRow("ada", "Ada Lovelace"),
                CreateOutputRow("grace", "Grace Hopper")
            ]
        });
        using var cache = new RecordingMemoryCache();
        var writer = new RecordingResultWriter(outputPath);
        var idGenerator = new RecordingResultIdGenerator(ResultId);
        var logger = new CapturingLogger<QueryController>();
        var controller = CreateController(CreatePlan(), enrichment, cache, writer, idGenerator, logger);

        var action = await controller.CsvEnrich(CreateRequest());

        var response = Assert.IsType<OkObjectResult>(action.Result);
        var body = SerializeBody(response);
        Assert.True(body.GetProperty("success").GetBoolean());
        Assert.Equal(ResultId, body.GetProperty("jobId").GetString());
        Assert.Equal(2, body.GetProperty("recordCount").GetInt32());
        Assert.Equal(1, body.GetProperty("data").GetArrayLength());
        Assert.Equal(1, enrichment.Calls);
        Assert.Equal(1, idGenerator.Calls);
        var write = Assert.Single(writer.Calls);
        var csv = Encoding.UTF8.GetString(write.Content);
        Assert.Contains("Ada Lovelace", csv, StringComparison.Ordinal);
        Assert.Contains("Grace Hopper", csv, StringComparison.Ordinal);
        Assert.Equal(ResultId, Assert.Single(cache.CreatedKeys));
        Assert.True(cache.TryGetValue(ResultId, out _));
        Assert.Equal(
            1,
            logger.Entries.Count(entry =>
                entry.Level == LogLevel.Information &&
                entry.Message.Contains("CSV enrichment completed", StringComparison.Ordinal)));
        Assert.True(File.Exists(Path.ChangeExtension(outputPath, ".log")));
    }

    private static QueryController CreateController(
        CsvEnrichmentPlan plan,
        StubCsvEnrichmentService enrichment,
        IMemoryCache cache,
        ICsvEnrichmentResultWriter writer,
        ICsvEnrichmentResultIdGenerator idGenerator,
        ILogger<QueryController> logger)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["QueryDefaults:PreviewRowCount"] = "1"
            })
            .Build();
        var controller = new QueryController(
            logger,
            new StubClaudeService(plan),
            null!,
            cache,
            configuration,
            null!,
            null!,
            null!,
            enrichment,
            writer,
            idGenerator)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.Name, "ANALOG\\csv-owner")],
                        "Test"))
                }
            }
        };
        return controller;
    }

    private static CsvEnrichmentRequest CreateRequest()
    {
        return new CsvEnrichmentRequest
        {
            Query = "add display names",
            CsvHeaders = ["Employee"],
            CsvData = [["ada"]]
        };
    }

    private static CsvEnrichmentPlan CreatePlan()
    {
        return new CsvEnrichmentPlan
        {
            MatchColumn = "Employee",
            MatchAttribute = "sAMAccountName",
            RetrieveAttributes = ["displayName"],
            OutputMode = "all",
            Description = "Add display names"
        };
    }

    private static Dictionary<string, object?> CreateOutputRow(string employee, string displayName)
    {
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Employee"] = employee,
            ["AD_displayName"] = displayName,
            ["AD_Status"] = "Matched"
        };
    }

    private static JsonElement SerializeBody(ObjectResult response)
    {
        return JsonSerializer.SerializeToElement(
            response.Value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static void AssertFailureBodyHasNoPublishedResult(JsonElement body)
    {
        Assert.False(body.TryGetProperty("jobId", out _));
        Assert.False(body.TryGetProperty("data", out _));
        Assert.False(body.TryGetProperty("recordCount", out _));
        Assert.False(body.TryGetProperty("plan", out _));
    }

    private static void AssertNoCompletionLog(CapturingLogger<QueryController> logger)
    {
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Level == LogLevel.Information &&
                     entry.Message.Contains("CSV enrichment completed", StringComparison.Ordinal));
    }

    private static string Flatten(CapturingLogger<QueryController> logger)
    {
        return string.Join(
            Environment.NewLine,
            logger.Entries.Select(entry => string.Join(
                " | ",
                entry.Message,
                entry.Exception?.ToString() ?? string.Empty,
                string.Join("; ", entry.State.Select(value => $"{value.Key}={value.Value}")))));
    }

    private sealed class StubClaudeService(CsvEnrichmentPlan plan) : IClaudeService
    {
        public Task<ClaudeResponse> GenerateExecutionPlanAsync(
            string userQuery,
            string? context = null,
            int? requestedResultLimit = null,
            CancellationToken cancellationToken = default,
            string? modelOverride = null)
        {
            throw new NotSupportedException();
        }

        public Task<CsvEnrichmentPlanResponse> GenerateCsvEnrichmentPlanAsync(
            string userQuery,
            List<string> csvHeaders,
            int rowCount,
            CancellationToken cancellationToken = default,
            Dictionary<string, string>? columnPatterns = null)
        {
            return Task.FromResult(new CsvEnrichmentPlanResponse
            {
                Success = true,
                Plan = plan
            });
        }

        public Task<ClaudeHealthResult> CheckHealthAsync(CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class StubCsvEnrichmentService : ICsvEnrichmentService
    {
        private readonly CsvEnrichmentResult? _result;
        private readonly Exception? _exception;

        public StubCsvEnrichmentService(CsvEnrichmentResult result)
        {
            _result = result;
        }

        public StubCsvEnrichmentService(Exception exception)
        {
            _exception = exception;
        }

        public int Calls { get; private set; }

        public Task<CsvEnrichmentResult> ExecuteAsync(
            CsvEnrichmentPlan? plan,
            List<string> csvHeaders,
            List<List<string>> csvData,
            CancellationToken cancellationToken)
        {
            Calls++;
            return _exception is null
                ? Task.FromResult(_result!)
                : Task.FromException<CsvEnrichmentResult>(_exception);
        }
    }

    private sealed record WriteCall(
        string? OwnerName,
        DateTime TimestampUtc,
        byte[] Content);

    private sealed class RecordingResultWriter(
        string outputPath,
        bool throwOnWrite = false) : ICsvEnrichmentResultWriter
    {
        public List<WriteCall> Calls { get; } = [];

        public string Write(string? ownerName, DateTime timestampUtc, byte[] content)
        {
            Calls.Add(new WriteCall(ownerName, timestampUtc, content.ToArray()));
            if (throwOnWrite)
            {
                throw new OperationCanceledException("Failed enrichment attempted result publication.");
            }

            return outputPath;
        }
    }

    private sealed class RecordingResultIdGenerator(string id = "unused-result-id")
        : ICsvEnrichmentResultIdGenerator
    {
        public int Calls { get; private set; }

        public string CreateId()
        {
            Calls++;
            return id;
        }
    }

    private sealed class RecordingMemoryCache : IMemoryCache
    {
        private readonly MemoryCache _inner = new(new MemoryCacheOptions());

        public List<object> CreatedKeys { get; } = [];

        public ICacheEntry CreateEntry(object key)
        {
            CreatedKeys.Add(key);
            return _inner.CreateEntry(key);
        }

        public void Remove(object key)
        {
            _inner.Remove(key);
        }

        public bool TryGetValue(object key, out object? value)
        {
            return _inner.TryGetValue(key, out value);
        }

        public void Dispose()
        {
            _inner.Dispose();
        }
    }

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

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"adquery-p04-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
