using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F03 Slice 1 guard: proves the Claude API key resolves from a DPAPI-encrypted
/// store outside the web root when config leaves <c>Claude:ApiKey</c> blank, and
/// that an explicit config key still wins. DPAPI blobs are machine-specific, so
/// each test writes its own blob at a temp path at runtime rather than committing
/// a fixture.
/// </summary>
public sealed class ProtectedApiKeyProviderTests : IDisposable
{
    private readonly string _storePath =
        Path.Combine(Path.GetTempPath(), $"adquery-apikey-{Guid.NewGuid():N}.dat");

    [Fact]
    public void BlankConfigKey_WithPresentStore_UsesStoredKey()
    {
        const string plaintext = "sk-from-dpapi-store";
        ProtectedApiKeyProvider.WriteApiKey(_storePath, plaintext);

        var options = ResolveOptions(new Dictionary<string, string?>
        {
            ["Claude:ApiKey"] = "",
            ["Claude:ApiKeyFile"] = _storePath,
        });

        Assert.Equal(plaintext, options.ApiKey);
    }

    [Fact]
    public void NonBlankConfigKey_WinsOverStore()
    {
        ProtectedApiKeyProvider.WriteApiKey(_storePath, "sk-from-dpapi-store");

        var options = ResolveOptions(new Dictionary<string, string?>
        {
            ["Claude:ApiKey"] = "sk-from-config",
            ["Claude:ApiKeyFile"] = _storePath,
        });

        Assert.Equal("sk-from-config", options.ApiKey);
    }

    [Fact]
    public void BlankConfigKey_WithMissingStore_LeavesKeyBlank()
    {
        // No blob written: the store path does not exist.
        var options = ResolveOptions(new Dictionary<string, string?>
        {
            ["Claude:ApiKey"] = "",
            ["Claude:ApiKeyFile"] = _storePath,
        });

        Assert.True(string.IsNullOrEmpty(options.ApiKey));
    }

    [Fact]
    public void WriteThenRead_RoundTripsPlaintext()
    {
        const string plaintext = "sk-round-trip-value";
        ProtectedApiKeyProvider.WriteApiKey(_storePath, plaintext);

        Assert.Equal(plaintext, ProtectedApiKeyProvider.TryReadApiKey(_storePath));
    }

    private static LlmProviderOptions ResolveOptions(IReadOnlyDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders());
        services.AddLlmProviderConfiguration(configuration);

        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<LlmProviderOptions>>().Value;
    }

    public void Dispose()
    {
        if (File.Exists(_storePath))
        {
            File.Delete(_storePath);
        }
    }
}
