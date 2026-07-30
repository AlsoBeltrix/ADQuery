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
            // The tightest legitimate pair: no prior questions, and a cap sized exactly to
            // the current turn's question plus the fixed components.
            ["FollowUp:MaxPriorQuestions"] = "0",
            ["FollowUp:MaxContextBytes"] = "14196",
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
        Assert.Equal(14196, options.MaxContextBytes);
        Assert.Equal(0, options.MaxPriorQuestions);
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
    public void CheckedInAppSettings_BindAndValidate()
    {
        // The shipped pair must itself pass startup validation — including the F04-D6
        // derived floor, which relates the two shipped values to each other.
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "appsettings.json");
        var configuration = new ConfigurationBuilder().AddJsonFile(path).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddFollowUpConfiguration(configuration);
        using var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<FollowUpOptions>>().Value;

        Assert.True(
            options.MaxContextBytes >= FollowUpOptions.WorstCaseContextBytes(options.MaxPriorQuestions),
            $"the shipped cap {options.MaxContextBytes} is below the worst case "
            + $"{options.MaxPriorQuestions} prior questions compose");
    }

    [Fact]
    public void PriorQuestionDefault_LeavesRoomForTheCurrentTurn()
    {
        // The current turn's own question occupies the remaining slot of the thread
        // ceiling, so the prior-question default is one below it.
        Assert.Equal(FollowUpOptions.MaxThreadQuestions - 1, new FollowUpOptions().MaxPriorQuestions);
    }

    [Theory]
    [InlineData("-1")]
    public void NegativePriorQuestions_FailsValidation(string value)
    {
        var settings = new Dictionary<string, string?> { ["FollowUp:MaxPriorQuestions"] = value };
        AssertValidationFails(settings, "MaxPriorQuestions");
    }

    [Fact]
    public void PriorQuestionsAtTheThreadCeiling_FailsValidation()
    {
        // At the ceiling the thread would be MaxThreadQuestions prior questions *plus* the
        // current turn's, composing past what the transport guard was derived from.
        var settings = new Dictionary<string, string?>
        {
            ["FollowUp:MaxPriorQuestions"] = FollowUpOptions.MaxThreadQuestions.ToString(),
        };

        AssertValidationFails(settings, "thread ceiling");
    }

    [Fact]
    public void CapBelowTheWorstCaseThread_FailsValidation()
    {
        // F04-D6's derived floor. This is the plan's rejected pair in miniature: a cap that
        // looks generous but that a legitimate maximum-length thread overflows, which would
        // make the backstop the shaper.
        var settings = new Dictionary<string, string?>
        {
            ["FollowUp:MaxPriorQuestions"] = "10",
            ["FollowUp:MaxContextBytes"] = (FollowUpOptions.WorstCaseContextBytes(10) - 1).ToString(),
        };

        AssertValidationFails(settings, "is a backstop, not a shaper");
    }

    [Fact]
    public void CapExactlyAtTheWorstCaseThread_Validates()
    {
        // The floor is inclusive: a pair sized exactly to its own worst case is the
        // tightest legitimate configuration and must boot.
        var options = Bind(new Dictionary<string, string?>
        {
            ["FollowUp:MaxPriorQuestions"] = "10",
            ["FollowUp:MaxContextBytes"] = FollowUpOptions.WorstCaseContextBytes(10).ToString(),
        });

        Assert.Equal(FollowUpOptions.WorstCaseContextBytes(10), options.MaxContextBytes);
    }

    [Fact]
    public void WorstCase_CountsTheCurrentTurnsQuestionToo()
    {
        // The knob counts *prior* questions; the composed context also carries the current
        // turn's. A worst case that forgot it would under-size the floor by one question.
        var oneQuestion = AnswerOptions.QuestionTransportCodeUnitLimit * 3;

        Assert.Equal(
            oneQuestion,
            FollowUpOptions.WorstCaseContextBytes(1) - FollowUpOptions.WorstCaseContextBytes(0));
        Assert.Equal(
            (FollowUpOptions.FixedComponentCodeUnits * 3) + oneQuestion,
            FollowUpOptions.WorstCaseContextBytes(0));
    }

    [Fact]
    public void WorstCaseAtTheThreadCeiling_FitsTheTransportGuard()
    {
        // The two derivations must not disagree: the loosest configurable pair
        // (MaxThreadQuestions - 1 prior questions) has to remain transport-legal, or the
        // knob's upper bound and the byte cap's upper bound would contradict each other.
        Assert.True(
            FollowUpOptions.WorstCaseContextBytes(FollowUpOptions.MaxThreadQuestions - 1)
                <= FollowUpOptions.ContextTransportCodeUnitLimit,
            "the loosest configurable thread composes past the transport guard");
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
