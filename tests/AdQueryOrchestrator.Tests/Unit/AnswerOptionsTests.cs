using System.Collections.Generic;
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
/// F04 Slice 2 guard: the Narrate reduction cap is a finite, startup-validated knob whose
/// default is derived from the builder's own component maxima rather than an estimate of
/// typical usage. Zero never means unlimited.
/// </summary>
public sealed class AnswerOptionsTests
{
    private static readonly IReadOnlyDictionary<string, string?> ValidSettings =
        new Dictionary<string, string?>
        {
            ["Answer:MaxReductionBytes"] = "4000",
        };

    [Fact]
    public void CheckedInDefault_EqualsTheDerivedCeiling()
    {
        Assert.Equal(AnswerOptions.ReductionCeilingBytes, new AnswerOptions().MaxReductionBytes);
    }

    [Fact]
    public void QuestionTransportLimit_MirrorsTheStringLengthAttribute()
    {
        // The question is clipped at the transport's own maximum, so the reduction ceiling
        // stays reachable. Widening the attribute must be a deliberate revisit here.
        var attribute = typeof(QueryRequest)
            .GetProperty(nameof(QueryRequest.Query), BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttribute<StringLengthAttribute>();

        Assert.NotNull(attribute);
        Assert.Equal(AnswerOptions.QuestionTransportCodeUnitLimit, attribute!.MaximumLength);
    }

    [Fact]
    public void GroupBucketBound_MirrorsTheHeadlineBound()
    {
        Assert.Equal(
            AdQuery.Orchestrator.Services.HeadlineClassifier.MaxHeadlineGroups,
            AnswerOptions.MaxGroupBuckets);
    }

    [Fact]
    public void CheckedInAppSettings_BindsAndValidates()
    {
        // The shipped appsettings value must itself pass startup validation.
        var options = Build(new Dictionary<string, string?>
        {
            ["Answer:MaxReductionBytes"] = AnswerOptions.ReductionCeilingBytes.ToString(),
        });

        Assert.Equal(AnswerOptions.ReductionCeilingBytes, options.MaxReductionBytes);
    }

    [Fact]
    public void ValidSettings_Bind()
    {
        Assert.Equal(4000, Build(ValidSettings).MaxReductionBytes);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    public void NonPositiveCap_FailsStartup(string value)
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => Build(new Dictionary<string, string?> { ["Answer:MaxReductionBytes"] = value }));

        Assert.Contains("never means unlimited", string.Join(" ", exception.Failures));
    }

    [Fact]
    public void CapAboveTheCeiling_FailsStartup()
    {
        var exception = Assert.Throws<OptionsValidationException>(
            () => Build(new Dictionary<string, string?>
            {
                ["Answer:MaxReductionBytes"] = (AnswerOptions.ReductionCeilingBytes + 1).ToString(),
            }));

        Assert.Contains("must not exceed the ceiling", string.Join(" ", exception.Failures));
    }

    private static AnswerOptions Build(IReadOnlyDictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddAnswerConfiguration(builder.Configuration);

        using var host = builder.Build();
        return host.Services.GetRequiredService<IOptions<AnswerOptions>>().Value;
    }
}
