using System.Collections.Concurrent;
using System.Text.Json;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class LlmProviderConfigurationTests
{
    private const string TargetModel = "@integration/provider.model";

    [Fact]
    public void CheckedInConfiguration_HasNoActiveSamplingProfileOrLegacyTemperature()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "appsettings.json");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var provider = document.RootElement.GetProperty(LlmProviderOptions.SectionName);

        Assert.DoesNotContain(
            provider.EnumerateObject(),
            property => string.Equals(property.Name, "Temperature", StringComparison.OrdinalIgnoreCase));
        var profiles = provider.GetProperty("SamplingProfiles");
        Assert.Equal(JsonValueKind.Array, profiles.ValueKind);
        Assert.Empty(profiles.EnumerateArray());
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1.1")]
    [InlineData("NaN")]
    public void LegacyGlobalTemperature_WarnsOnceAndNeverEnablesSampling(string legacyTemperature)
    {
        using var services = CreateServices(
            new Dictionary<string, string?>
            {
                ["Claude:Temperature"] = legacyTemperature
            },
            out var logs);

        var options = services.GetRequiredService<IOptions<LlmProviderOptions>>();
        var first = options.Value;
        var second = options.Value;

        Assert.Same(first, second);
        Assert.Single(
            logs.Entries,
            entry =>
                entry.Level == LogLevel.Warning &&
                entry.Message.Contains("Ignoring legacy Claude:Temperature", StringComparison.Ordinal));
        AssertTemperatureAbsent(
            services.GetRequiredService<LlmMessagesRequestBuilder>().Build(
                TargetModel,
                1234,
                "system",
                "user"));
    }

    [Fact]
    public void OmitProfileValue_WarnsOnceAndNeverEnablesSampling()
    {
        using var services = CreateServices(
            new Dictionary<string, string?>
            {
                ["Claude:SamplingProfiles:0:TargetModel"] = TargetModel,
                ["Claude:SamplingProfiles:0:Mode"] = LlmSamplingModes.Omit,
                ["Claude:SamplingProfiles:0:Temperature"] = "NaN"
            },
            out var logs);

        var options = services.GetRequiredService<IOptions<LlmProviderOptions>>();
        _ = options.Value;
        _ = options.Value;

        Assert.Single(
            logs.Entries,
            entry =>
                entry.Level == LogLevel.Warning &&
                entry.Message.Contains("because its mode is Omit", StringComparison.Ordinal));
        AssertTemperatureAbsent(
            services.GetRequiredService<LlmMessagesRequestBuilder>().Build(
                TargetModel,
                1234,
                "system",
                "user"));
    }

    [Fact]
    public async Task ProductionRegistration_InvalidEnabledProfileFailsHostStartup()
    {
        var settings = new Dictionary<string, string?>
        {
            ["Claude:SamplingProfiles:0:TargetModel"] = TargetModel,
            ["Claude:SamplingProfiles:0:Mode"] = LlmSamplingModes.Temperature,
            ["Claude:SamplingProfiles:0:Temperature"] = "not-a-number"
        };
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddLlmProviderConfiguration(context.Configuration);
            })
            .Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("Temperature must be a finite number", StringComparison.Ordinal));
    }

    private static ServiceProvider CreateServices(
        IReadOnlyDictionary<string, string?> settings,
        out CapturingLoggerProvider logs)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var loggerProvider = new CapturingLoggerProvider();
        logs = loggerProvider;
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.ClearProviders();
            builder.AddProvider(loggerProvider);
        });
        services.AddLlmProviderConfiguration(configuration);

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true
            });
    }

    private static void AssertTemperatureAbsent(LlmMessagesRequest request)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        Assert.False(document.RootElement.TryGetProperty("temperature", out _));
    }

    private sealed record LogEntry(LogLevel Level, string Category, string Message);

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyCollection<LogEntry> Entries => _entries.ToArray();

        public ILogger CreateLogger(string categoryName)
        {
            return new CapturingLogger(categoryName, _entries);
        }

        public void Dispose()
        {
        }

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly ConcurrentQueue<LogEntry> _entries;

            public CapturingLogger(string category, ConcurrentQueue<LogEntry> entries)
            {
                _category = category;
                _entries = entries;
            }

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
                _entries.Enqueue(new LogEntry(logLevel, _category, formatter(state, exception)));
            }
        }
    }
}
