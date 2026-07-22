using System.Text.Json;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class LlmMessagesRequestBuilderTests
{
    private const string TargetModel = "@integration/provider.model";

    [Fact]
    public void Build_WithoutMatchingProfile_OmitsTemperatureAndPinsWireNames()
    {
        var request = CreateBuilder(new LlmProviderOptions()).Build(
            TargetModel,
            maxTokens: 1234,
            systemGuidance: "system guidance",
            userContent: "user content");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        var root = document.RootElement;

        Assert.Equal(
            ["model", "max_tokens", "system", "messages"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.False(root.TryGetProperty("temperature", out _));
        Assert.Equal(TargetModel, root.GetProperty("model").GetString());
        Assert.Equal(JsonValueKind.Number, root.GetProperty("max_tokens").ValueKind);
        Assert.Equal(1234, root.GetProperty("max_tokens").GetInt32());
        Assert.Equal("system guidance", root.GetProperty("system").GetString());

        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal(
            ["role", "content"],
            message.EnumerateObject().Select(property => property.Name));
        Assert.Equal("user", message.GetProperty("role").GetString());
        Assert.Equal("user content", message.GetProperty("content").GetString());
    }

    [Fact]
    public void Build_WithExactTemperatureProfile_IncludesTemperature()
    {
        var options = CreateOptions(TargetModel, LlmSamplingModes.Temperature, "0.25");

        var request = CreateBuilder(options).Build(TargetModel, 1234, "system", "user");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        var root = document.RootElement;
        Assert.Equal(
            ["model", "max_tokens", "temperature", "system", "messages"],
            root.EnumerateObject().Select(property => property.Name));
        Assert.Equal(JsonValueKind.Number, root.GetProperty("temperature").ValueKind);
        Assert.Equal(0.25, root.GetProperty("temperature").GetDouble());
    }

    [Theory]
    [InlineData("@integration/provider.model ")]
    [InlineData("@INTEGRATION/PROVIDER.MODEL")]
    [InlineData("claude")]
    [InlineData("gpt")]
    [InlineData("vertex")]
    [InlineData("azure")]
    public void Build_WithoutExactProfileMatch_OmitsTemperature(string effectiveModel)
    {
        var options = CreateOptions(TargetModel, LlmSamplingModes.Temperature, "0.25");

        var request = CreateBuilder(options).Build(effectiveModel, 1234, "system", "user");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        Assert.False(document.RootElement.TryGetProperty("temperature", out _));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1")]
    public void Validate_AcceptsInclusiveTemperatureBoundaries(string temperature)
    {
        var options = CreateOptions(TargetModel, LlmSamplingModes.Temperature, temperature);

        var result = Validate(options);

        Assert.True(result.Succeeded);
    }

    [Theory]
    [InlineData("", "Temperature", "0.5", "TargetModel")]
    [InlineData("model", "Unknown", "0.5", "Mode")]
    [InlineData("model", "Temperature", null, "Temperature")]
    [InlineData("model", "Temperature", "not-a-number", "Temperature")]
    [InlineData("model", "Temperature", "NaN", "Temperature")]
    [InlineData("model", "Temperature", "Infinity", "Temperature")]
    [InlineData("model", "Temperature", "-0.1", "Temperature")]
    [InlineData("model", "Temperature", "1.1", "Temperature")]
    public void Validate_RejectsInvalidEnabledProfile(
        string targetModel,
        string mode,
        string? temperature,
        string expectedFailure)
    {
        var options = CreateOptions(targetModel, mode, temperature);

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains(expectedFailure, string.Join(" ", result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_RejectsDuplicateExactTargets()
    {
        var options = CreateOptions(TargetModel, LlmSamplingModes.Temperature, "0.25");
        options.SamplingProfiles.Add(new LlmSamplingProfileOptions
        {
            TargetModel = TargetModel,
            Mode = LlmSamplingModes.Omit
        });

        var result = Validate(options);

        Assert.True(result.Failed);
        Assert.Contains("duplicate TargetModel", string.Join(" ", result.Failures), StringComparison.Ordinal);
    }

    [Fact]
    public void Build_WithOmitProfile_IgnoresConfiguredValue()
    {
        var options = CreateOptions(TargetModel, LlmSamplingModes.Omit, "NaN");
        Assert.True(Validate(options).Succeeded);

        var request = CreateBuilder(options).Build(TargetModel, 1234, "system", "user");

        using var document = JsonDocument.Parse(JsonSerializer.Serialize(request));
        Assert.False(document.RootElement.TryGetProperty("temperature", out _));
    }

    private static LlmProviderOptions CreateOptions(string targetModel, string mode, string? temperature)
    {
        return new LlmProviderOptions
        {
            SamplingProfiles =
            [
                new LlmSamplingProfileOptions
                {
                    TargetModel = targetModel,
                    Mode = mode,
                    Temperature = temperature
                }
            ]
        };
    }

    private static LlmMessagesRequestBuilder CreateBuilder(LlmProviderOptions options)
    {
        return new LlmMessagesRequestBuilder(Options.Create(options));
    }

    private static ValidateOptionsResult Validate(LlmProviderOptions options)
    {
        var validator = new LlmProviderOptionsValidator(
            NullLogger<LlmProviderOptionsValidator>.Instance);
        return validator.Validate(Options.DefaultName, options);
    }
}
