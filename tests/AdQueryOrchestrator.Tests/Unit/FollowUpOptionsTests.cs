using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Text;
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
/// reconciled with the transport-level <c>[StringLength]</c> guard on
/// <c>QueryRequest.Context</c>.
///
/// F04 Slice 6b widened that guard so an accumulated question thread fits. Three things
/// carry the widened value — the attribute, <c>FollowUpOptions.ContextTransportCodeUnitLimit</c>,
/// and the validator ceiling that reads it — and the tests below fail if any one of them
/// moves alone. The limit is derived from enforced maxima (thread depth × the
/// <c>QueryRequest.Query</c> length bound, plus the fixed last-turn components), never from
/// observed threads (f04-or-6).
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
        Assert.Equal(
            FollowUpOptions.ContextTransportCodeUnitLimit,
            new FollowUpOptions().MaxContextBytes);
    }

    [Fact]
    public void TransportCodeUnitLimit_MirrorsTheStringLengthAttribute()
    {
        // Reconciliation: the byte cap ceiling must equal the actual [StringLength]
        // maximum on QueryRequest.Context, so an in-bounds byte input can never be
        // pre-empted by binding-time rejection. The attribute is a literal by design —
        // reading the constant into it would make this assertion unfalsifiable — so
        // widening one without the other reddens here.
        var attribute = typeof(QueryRequest)
            .GetProperty(nameof(QueryRequest.Context), BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttribute<StringLengthAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(FollowUpOptions.ContextTransportCodeUnitLimit, attribute!.MaximumLength);
    }

    [Fact]
    public void TransportCodeUnitLimit_IsDerivedFromEnforcedMaxima()
    {
        // f04-or-6: the ceiling must be computed from the bounds the code actually enforces,
        // not extrapolated from sample threads. A maximum thread is MaxThreadQuestions
        // questions at the QueryRequest.Query [StringLength] maximum plus the fixed
        // last-turn components, and each UTF-16 code unit costs at most three UTF-8 bytes.
        var questionLimit = typeof(QueryRequest)
            .GetProperty(nameof(QueryRequest.Query), BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttribute<StringLengthAttribute>()!
            .MaximumLength;

        var worstCaseCodeUnits =
            (FollowUpOptions.MaxThreadQuestions * questionLimit) + FollowUpOptions.FixedComponentCodeUnits;

        Assert.Equal(FollowUpOptions.ContextTransportCodeUnitLimit, worstCaseCodeUnits * 3);
    }

    [Fact]
    public void MaximumThread_FitsTheWidenedTransportGuard()
    {
        // The Slice 6a bound this widening exists to permit: MaxThreadQuestions questions of
        // maximum length, in the worst-case encoding, composed with the fixed components.
        // It must fit — the byte cap is a backstop, never the shaper (F04-D6). This is the
        // claim the pre-widening 2000 could not support: three ASCII questions overflowed it.
        // A non-BMP character is two UTF-16 code units and four UTF-8 bytes, so this is a
        // question of exactly the transport maximum in its most expensive encoding.
        var question = string.Concat(
            Enumerable.Repeat("\U0001F600", AnswerOptions.QuestionTransportCodeUnitLimit / 2));
        var maximumThread = string.Join(
            '\n',
            Enumerable.Repeat(question, FollowUpOptions.MaxThreadQuestions));

        var composedBytes = Encoding.UTF8.GetByteCount(maximumThread)
            + FollowUpOptions.FixedComponentCodeUnits;

        Assert.True(
            composedBytes <= FollowUpOptions.ContextTransportCodeUnitLimit,
            $"a maximum thread composes {composedBytes} bytes, above the "
            + $"{FollowUpOptions.ContextTransportCodeUnitLimit}-byte transport guard");
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
    public void AtTheTransportGuard_Validates()
    {
        // The third of the three reconciled values: the validator's ceiling. Paired with
        // AboveTransportGuard_FailsValidation this pins the rejection boundary exactly at
        // the constant, so a validator ceiling left behind at the pre-widening value — or
        // moved past it — reddens on one side or the other.
        var options = Bind(new Dictionary<string, string?>
        {
            ["FollowUp:MaxContextBytes"] = FollowUpOptions.ContextTransportCodeUnitLimit.ToString(),
        });

        Assert.Equal(FollowUpOptions.ContextTransportCodeUnitLimit, options.MaxContextBytes);
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
