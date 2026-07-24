using System.Text.Json;
using AdQuery.Orchestrator.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class CsvEnrichmentLimitsOptionsTests
{
    private static readonly IReadOnlyDictionary<string, string?> ValidD1Settings =
        new Dictionary<string, string?>
        {
            ["CsvEnrichment:Limits:MaxDataRows"] = "100000",
            ["CsvEnrichment:Limits:MaxColumns"] = "64",
            ["CsvEnrichment:Limits:MaxRetrieveAttributes"] = "16",
            ["CsvEnrichment:Limits:MaxFieldCodeUnits"] = "1024",
            ["CsvEnrichment:Limits:MaxRequestBodyBytes"] = "100663296",
            ["CsvEnrichment:Limits:LdapReceiveCeilingBytes"] = "10485760",
        };

    [Fact]
    public void CheckedInConfiguration_BindsTheApprovedD1Caps()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "appsettings.json");

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var limits = document.RootElement
            .GetProperty("CsvEnrichment")
            .GetProperty("Limits");

        Assert.Equal(100000, limits.GetProperty("MaxDataRows").GetInt32());
        Assert.Equal(64, limits.GetProperty("MaxColumns").GetInt32());
        Assert.Equal(16, limits.GetProperty("MaxRetrieveAttributes").GetInt32());
        Assert.Equal(1024, limits.GetProperty("MaxFieldCodeUnits").GetInt32());
        Assert.Equal(100663296L, limits.GetProperty("MaxRequestBodyBytes").GetInt64());
        Assert.Equal(10485760L, limits.GetProperty("LdapReceiveCeilingBytes").GetInt64());
    }

    [Fact]
    public void ValidD1Settings_BindAndDeriveConsequences()
    {
        var options = Bind(ValidD1Settings);

        Assert.Equal(100000, options.MaxDataRows);
        Assert.Equal(64, options.MaxColumns);
        Assert.Equal(16, options.MaxRetrieveAttributes);
        Assert.Equal(1024, options.MaxFieldCodeUnits);
        Assert.Equal(100663296L, options.MaxRequestBodyBytes);
        Assert.Equal(10485760L, options.LdapReceiveCeilingBytes);

        // Derived, never configured.
        Assert.Equal(options.MaxDataRows, options.OutputRowLimit);
        Assert.Equal(6_400_000L, options.MaxGridCells);
    }

    [Fact]
    public void AmbiguityThresholdAndMatchSchema_AreTheSettledSemanticConstants()
    {
        Assert.Equal(2, CsvEnrichmentLimitsOptions.AmbiguityThreshold);

        var schema = CsvEnrichmentLimitsOptions.MatchAttributeSchemaLengths;
        Assert.Equal(5, schema.Count);
        Assert.Equal(256, schema["sAMAccountName"]);
        Assert.Equal(1024, schema["userPrincipalName"]);
        Assert.Equal(256, schema["mail"]);
        Assert.Equal(256, schema["displayName"]);
        Assert.Equal(16, schema["employeeID"]);

        // Case-insensitive keying: the LLM plan may vary casing.
        Assert.Equal(256, schema["samaccountname"]);
    }

    [Theory]
    [InlineData("MaxDataRows")]
    [InlineData("MaxColumns")]
    [InlineData("MaxRetrieveAttributes")]
    [InlineData("MaxFieldCodeUnits")]
    [InlineData("MaxRequestBodyBytes")]
    [InlineData("LdapReceiveCeilingBytes")]
    public void ZeroValue_FailsValidation(string field)
    {
        var settings = new Dictionary<string, string?>(ValidD1Settings)
        {
            [$"CsvEnrichment:Limits:{field}"] = "0",
        };

        AssertValidationFails(settings, field);
    }

    [Theory]
    [InlineData("MaxDataRows")]
    [InlineData("MaxColumns")]
    [InlineData("MaxRequestBodyBytes")]
    public void NegativeValue_FailsValidation(string field)
    {
        var settings = new Dictionary<string, string?>(ValidD1Settings)
        {
            [$"CsvEnrichment:Limits:{field}"] = "-1",
        };

        AssertValidationFails(settings, field);
    }

    [Fact]
    public void GridCellOverflow_FailsValidation()
    {
        var settings = new Dictionary<string, string?>(ValidD1Settings)
        {
            ["CsvEnrichment:Limits:MaxDataRows"] = int.MaxValue.ToString(),
            ["CsvEnrichment:Limits:MaxColumns"] = int.MaxValue.ToString(),
        };

        AssertValidationFails(settings, "overflow");
    }

    [Fact]
    public async Task ProductionRegistration_InvalidLimitFailsHostStartup()
    {
        var settings = new Dictionary<string, string?>(ValidD1Settings)
        {
            ["CsvEnrichment:Limits:MaxDataRows"] = "0",
        };
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddCsvEnrichmentConfiguration(context.Configuration);
            })
            .Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("MaxDataRows", StringComparison.Ordinal));
    }

    private static CsvEnrichmentLimitsOptions Bind(IReadOnlyDictionary<string, string?> settings)
    {
        using var provider = BuildProvider(settings);
        return provider.GetRequiredService<IOptions<CsvEnrichmentLimitsOptions>>().Value;
    }

    private static void AssertValidationFails(IReadOnlyDictionary<string, string?> settings, string expected)
    {
        using var provider = BuildProvider(settings);
        var options = provider.GetRequiredService<IOptions<CsvEnrichmentLimitsOptions>>();

        var exception = Assert.Throws<OptionsValidationException>(() => _ = options.Value);
        Assert.Contains(
            exception.Failures,
            failure => failure.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    private static ServiceProvider BuildProvider(IReadOnlyDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCsvEnrichmentConfiguration(configuration);

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
    }
}
