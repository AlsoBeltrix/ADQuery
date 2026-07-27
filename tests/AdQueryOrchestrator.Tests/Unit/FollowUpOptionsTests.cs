using System.ComponentModel.DataAnnotations;
using System.Reflection;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F01 Slice C1 guard: the follow-up byte cap is a finite, startup-validated knob,
/// reconciled with the transport-level <c>[StringLength(2000)]</c> guard on
/// <c>QueryRequest.Context</c>.
/// </summary>
public sealed class FollowUpOptionsTests
{
    private static readonly IReadOnlyDictionary<string, string?> ValidSettings =
        new Dictionary<string, string?>
        {
            ["FollowUp:MaxContextBytes"] = "2000",
        };

    [Fact]
    public void CheckedInDefault_MatchesTheTransportGuard()
    {
        Assert.Equal(2000, new FollowUpOptions().MaxContextBytes);
        Assert.Equal(2000, FollowUpOptions.ContextTransportCodeUnitLimit);
    }

    [Fact]
    public void TransportCodeUnitLimit_MirrorsTheStringLengthAttribute()
    {
        // Reconciliation: the byte cap ceiling must equal the actual [StringLength]
        // maximum on QueryRequest.Context, so an in-bounds byte input can never be
        // pre-empted by binding-time rejection. If the attribute is widened, this
        // constant (and the validator ceiling it feeds) must be revisited deliberately.
        var attribute = typeof(QueryRequest)
            .GetProperty(nameof(QueryRequest.Context), BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttribute<StringLengthAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(FollowUpOptions.ContextTransportCodeUnitLimit, attribute!.MaximumLength);
    }

    [Fact]
    public void ValidSettings_Bind()
    {
        var options = Bind(ValidSettings);
        Assert.Equal(2000, options.MaxContextBytes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void NonPositive_FailsValidation(string value)
    {
        var settings = new Dictionary<string, string?> { ["FollowUp:MaxContextBytes"] = value };
        AssertValidationFails(settings, "MaxContextBytes");
    }

    [Fact]
    public void AboveTransportGuard_FailsValidation()
    {
        var settings = new Dictionary<string, string?>
        {
            ["FollowUp:MaxContextBytes"] = (FollowUpOptions.ContextTransportCodeUnitLimit + 1).ToString(),
        };
        AssertValidationFails(settings, "transport code-unit guard");
    }

    [Fact]
    public async Task ProductionRegistration_InvalidCapFailsHostStartup()
    {
        var settings = new Dictionary<string, string?> { ["FollowUp:MaxContextBytes"] = "0" };
        using var host = new HostBuilder()
            .ConfigureAppConfiguration(configuration => configuration.AddInMemoryCollection(settings))
            .ConfigureServices((context, services) =>
            {
                services.AddLogging();
                services.AddFollowUpConfiguration(context.Configuration);
            })
            .Build();

        var exception = await Assert.ThrowsAsync<OptionsValidationException>(
            () => host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains(
            exception.Failures,
            failure => failure.Contains("MaxContextBytes", StringComparison.Ordinal));
    }

    private static FollowUpOptions Bind(IReadOnlyDictionary<string, string?> settings)
    {
        using var provider = BuildProvider(settings);
        return provider.GetRequiredService<IOptions<FollowUpOptions>>().Value;
    }

    private static void AssertValidationFails(IReadOnlyDictionary<string, string?> settings, string expected)
    {
        using var provider = BuildProvider(settings);
        var options = provider.GetRequiredService<IOptions<FollowUpOptions>>();

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
        services.AddFollowUpConfiguration(configuration);

        return services.BuildServiceProvider(
            new ServiceProviderOptions
            {
                ValidateOnBuild = true,
                ValidateScopes = true,
            });
    }
}
