using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class CsvEnrichmentServiceLookupTests
{
    [Fact]
    public async Task ExecuteAsync_FoundRecordProducesMatchedRow()
    {
        var directory = new ScriptedDirectoryService(ReturnRecords(CreateRecord("Ada Lovelace")));
        var logger = new CapturingLogger<CsvEnrichmentService>();
        var service = CreateService(directory, logger);

        var result = await service.ExecuteAsync(
            CreatePlan(),
            ["Employee"],
            [["ada"]],
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(CsvEnrichmentFailureKind.None, result.FailureKind);
        Assert.Equal(1, result.TotalRows);
        Assert.Equal(1, result.MatchedRows);
        Assert.Equal(1, result.FilteredRows);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        var row = Assert.Single(result.Data);
        Assert.Equal("Ada Lovelace", row["AD_displayName"]);
        Assert.Equal("Matched", row["AD_Status"]);
        Assert.Single(directory.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_EmptySearchIsSuccessfulNotFound()
    {
        var directory = new ScriptedDirectoryService(ReturnRecords());
        var logger = new CapturingLogger<CsvEnrichmentService>();
        var service = CreateService(directory, logger);

        var result = await service.ExecuteAsync(
            CreatePlan(),
            ["Employee"],
            [["missing-user"]],
            TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(CsvEnrichmentFailureKind.None, result.FailureKind);
        Assert.Equal(1, result.TotalRows);
        Assert.Equal(0, result.MatchedRows);
        Assert.Equal(0, result.FilteredRows);
        Assert.Empty(result.Errors);
        Assert.Equal(
            "1 of 1 users not found in Active Directory",
            Assert.Single(result.Warnings));
        Assert.Equal("Not found", Assert.Single(result.Data)["AD_Status"]);
        Assert.Single(directory.Requests);
    }

    [Fact]
    public async Task ExecuteAsync_LaterOperationalFailureDiscardsPriorSuccessAndStops()
    {
        var directory = new ScriptedDirectoryService(
            ReturnRecords(CreateRecord("Ada Lovelace")),
            Throw(new InvalidOperationException("directory unavailable")),
            ReturnRecords(CreateRecord("Grace Hopper")));
        var logger = new CapturingLogger<CsvEnrichmentService>();
        var service = CreateService(directory, logger);

        var result = await service.ExecuteAsync(
            CreatePlan(),
            ["Employee"],
            [["ada"], ["failed-user"], ["grace"]],
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(CsvEnrichmentFailureKind.DirectoryOperation, result.FailureKind);
        Assert.Equal(3, result.TotalRows);
        Assert.Equal(0, result.MatchedRows);
        Assert.Equal(0, result.FilteredRows);
        Assert.Empty(result.Data);
        Assert.Empty(result.Warnings);
        Assert.Equal(
            "Active Directory lookup failed. Retry the CSV enrichment.",
            Assert.Single(result.Errors));
        Assert.Equal(2, directory.Requests.Count);
        Assert.Equal(1, directory.RemainingSteps);
    }

    [Fact]
    public async Task ExecuteAsync_DirectoryCancellationPropagatesWithoutFailureLog()
    {
        var directory = new ScriptedDirectoryService(
            (_, cancellationToken) => Task.FromException<IReadOnlyList<DirectoryRecord>>(
                new OperationCanceledException("directory canceled", cancellationToken)));
        var logger = new CapturingLogger<CsvEnrichmentService>();
        var service = CreateService(directory, logger);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync(
            CreatePlan(),
            ["Employee"],
            [["ada"]],
            TestContext.Current.CancellationToken));

        Assert.Single(directory.Requests);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteAsync_CancellationWinsWhenDirectoryThrowsAnotherException()
    {
        using var cancellation = new CancellationTokenSource();
        var directory = new ScriptedDirectoryService(
            (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromException<IReadOnlyList<DirectoryRecord>>(
                    new InvalidOperationException("directory failed during cancellation"));
            });
        var logger = new CapturingLogger<CsvEnrichmentService>();
        var service = CreateService(directory, logger);

        _ = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExecuteAsync(
            CreatePlan(),
            ["Employee"],
            [["ada"]],
            cancellation.Token));

        Assert.Single(directory.Requests);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteAsync_OperationalFailureLogContainsOnlySafeStructure()
    {
        const string MatchValue = "secret-match-value";
        const string OtherCell = "secret-row-value";
        var exceptionMessage = $"provider echoed {MatchValue} and {OtherCell}";
        var directory = new ScriptedDirectoryService(
            Throw(new InvalidOperationException(exceptionMessage)));
        var logger = new CapturingLogger<CsvEnrichmentService>();
        var service = CreateService(directory, logger);

        var result = await service.ExecuteAsync(
            CreatePlan(),
            ["Employee", "Notes"],
            [[MatchValue, OtherCell]],
            TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Null(warning.Exception);
        Assert.Equal(0, warning.State["RowIndex"]);
        Assert.Equal("sAMAccountName", warning.State["MatchAttribute"]);
        Assert.Equal(typeof(InvalidOperationException).FullName, warning.State["ExceptionType"]);
        Assert.DoesNotContain("MatchValue", warning.State.Keys);
        Assert.DoesNotContain(
            logger.Entries,
            entry => entry.Level == LogLevel.Information &&
                     entry.Message.Contains("completed", StringComparison.OrdinalIgnoreCase));

        var capturedText = Flatten(logger);
        Assert.DoesNotContain(MatchValue, capturedText, StringComparison.Ordinal);
        Assert.DoesNotContain(OtherCell, capturedText, StringComparison.Ordinal);
        Assert.DoesNotContain(exceptionMessage, capturedText, StringComparison.Ordinal);
        Assert.DoesNotContain(MatchValue, string.Join(Environment.NewLine, result.Errors), StringComparison.Ordinal);
        Assert.DoesNotContain(OtherCell, string.Join(Environment.NewLine, result.Errors), StringComparison.Ordinal);
    }

    private static CsvEnrichmentService CreateService(
        ScriptedDirectoryService directory,
        ILogger<CsvEnrichmentService> logger)
    {
        var evaluator = new CsvEnrichmentFilterEvaluator();
        var policy = new AllowingDirectorySecurityPolicy();
        return new CsvEnrichmentService(
            logger,
            directory,
            new CsvEnrichmentPlanValidator(policy, evaluator),
            evaluator);
    }

    private static CsvEnrichmentPlan CreatePlan()
    {
        return new CsvEnrichmentPlan
        {
            MatchColumn = "Employee",
            MatchAttribute = "sAMAccountName",
            RetrieveAttributes = ["displayName"],
            OutputMode = "all"
        };
    }

    private static DirectoryRecord CreateRecord(string displayName)
    {
        var record = new DirectoryRecord
        {
            ObjectType = DirectoryObjectType.User,
            DistinguishedName = $"CN={displayName},DC=example,DC=com"
        };
        record["displayName"] = displayName;
        return record;
    }

    private static Func<DirectorySearchRequest, CancellationToken, Task<IReadOnlyList<DirectoryRecord>>> ReturnRecords(
        params DirectoryRecord[] records)
    {
        return (_, _) => Task.FromResult<IReadOnlyList<DirectoryRecord>>(records);
    }

    private static Func<DirectorySearchRequest, CancellationToken, Task<IReadOnlyList<DirectoryRecord>>> Throw(
        Exception exception)
    {
        return (_, _) => Task.FromException<IReadOnlyList<DirectoryRecord>>(exception);
    }

    private static string Flatten(CapturingLogger<CsvEnrichmentService> logger)
    {
        return string.Join(
            Environment.NewLine,
            logger.Entries.Select(entry => string.Join(
                " | ",
                entry.Message,
                entry.Exception?.ToString() ?? string.Empty,
                string.Join("; ", entry.State.Select(value => $"{value.Key}={value.Value}")))));
    }

    private sealed class AllowingDirectorySecurityPolicy : IDirectorySecurityPolicy
    {
        private static readonly HashSet<string> AllowedAttributes = new(
            ["sAMAccountName", "displayName", "distinguishedName"],
            StringComparer.OrdinalIgnoreCase);

        public bool HasAllowedAttributes(DirectoryObjectType objectType)
        {
            return objectType == DirectoryObjectType.User;
        }

        public bool IsAttributeAllowed(DirectoryObjectType objectType, string? attribute)
        {
            return objectType == DirectoryObjectType.User &&
                   attribute is not null &&
                   AllowedAttributes.Contains(attribute);
        }

        public bool IsFilterOperatorAllowed(string? operatorValue)
        {
            return string.Equals(operatorValue, "equals", StringComparison.OrdinalIgnoreCase);
        }
    }

    private sealed class ScriptedDirectoryService : IActiveDirectoryService
    {
        private readonly Queue<Func<DirectorySearchRequest, CancellationToken, Task<IReadOnlyList<DirectoryRecord>>>> _steps;

        public ScriptedDirectoryService(
            params Func<DirectorySearchRequest, CancellationToken, Task<IReadOnlyList<DirectoryRecord>>>[] steps)
        {
            _steps = new Queue<Func<DirectorySearchRequest, CancellationToken, Task<IReadOnlyList<DirectoryRecord>>>>(steps);
        }

        public List<DirectorySearchRequest> Requests { get; } = [];

        public int RemainingSteps => _steps.Count;

        public Task<IReadOnlyList<DirectoryRecord>> SearchAsync(
            DirectorySearchRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            if (!_steps.TryDequeue(out var step))
            {
                throw new InvalidOperationException("No scripted directory response remains.");
            }

            return step(request, cancellationToken);
        }

        public Task<IReadOnlyList<DirectoryRecord>> ExpandGroupMembersAsync(
            IEnumerable<string> groupDistinguishedNames,
            bool recursive,
            IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<DirectoryRecord>> LookupAsync(
            IEnumerable<string> distinguishedNames,
            DirectoryObjectType targetType,
            IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
        }

        public Task<IReadOnlyList<DirectoryRecord>> GetDirectReportsBatch(
            IEnumerable<string> managerDistinguishedNames,
            IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
        {
            throw new NotSupportedException();
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
}
