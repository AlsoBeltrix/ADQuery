using AdQuery.Orchestrator.Configuration;
using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Services;

internal sealed class LlmMessagesRequestBuilder
{
    private readonly LlmProviderOptions _options;

    public LlmMessagesRequestBuilder(IOptions<LlmProviderOptions> options)
    {
        _options = options.Value;
    }

    public LlmMessagesRequest Build(
        string effectiveModel,
        int maxTokens,
        string systemGuidance,
        string userContent)
    {
        var matchingProfile = (_options.SamplingProfiles ?? []).SingleOrDefault(
            profile => string.Equals(profile.TargetModel, effectiveModel, StringComparison.Ordinal));
        double? temperature = null;

        if (matchingProfile is not null &&
            string.Equals(
                matchingProfile.Mode,
                LlmSamplingModes.Temperature,
                StringComparison.OrdinalIgnoreCase))
        {
            if (!LlmProviderOptionsValidator.TryParseTemperature(
                    matchingProfile.Temperature,
                    out var configuredTemperature))
            {
                throw new InvalidOperationException("The enabled sampling profile was not validated.");
            }

            temperature = configuredTemperature;
        }

        return new LlmMessagesRequest
        {
            Model = effectiveModel,
            MaxTokens = maxTokens,
            Temperature = temperature,
            System = systemGuidance,
            Messages =
            [
                new LlmMessage
                {
                    Role = "user",
                    Content = userContent
                }
            ]
        };
    }
}
