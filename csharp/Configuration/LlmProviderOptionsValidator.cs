using System.Globalization;
using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Configuration;

public sealed class LlmProviderOptionsValidator : IValidateOptions<LlmProviderOptions>
{
    private readonly ILogger<LlmProviderOptionsValidator> _logger;

    public LlmProviderOptionsValidator(ILogger<LlmProviderOptionsValidator> logger)
    {
        _logger = logger;
    }

    public ValidateOptionsResult Validate(string? name, LlmProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var targets = new HashSet<string>(StringComparer.Ordinal);
        var profiles = options.SamplingProfiles ?? [];

        if (options.Temperature is not null)
        {
            _logger.LogWarning(
                "Ignoring legacy Claude:Temperature because sampling requires an exact SamplingProfiles entry.");
        }

        for (var index = 0; index < profiles.Count; index++)
        {
            var profile = profiles[index];
            var target = profile.TargetModel;

            if (string.IsNullOrWhiteSpace(target))
            {
                failures.Add($"Claude:SamplingProfiles:{index}:TargetModel is required.");
                continue;
            }

            if (!targets.Add(target))
            {
                failures.Add($"Claude:SamplingProfiles contains duplicate TargetModel entries at index {index}.");
            }

            if (string.Equals(profile.Mode, LlmSamplingModes.Omit, StringComparison.OrdinalIgnoreCase))
            {
                if (profile.Temperature is not null)
                {
                    _logger.LogWarning(
                        "Ignoring the sampling value for profile index {ProfileIndex} because its mode is Omit.",
                        index);
                }

                continue;
            }

            if (!string.Equals(profile.Mode, LlmSamplingModes.Temperature, StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"Claude:SamplingProfiles:{index}:Mode must be Omit or Temperature.");
                continue;
            }

            if (!TryParseTemperature(profile.Temperature, out _))
            {
                failures.Add(
                    $"Claude:SamplingProfiles:{index}:Temperature must be a finite number from 0.0 through 1.0 when Mode is Temperature.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    internal static bool TryParseTemperature(string? value, out double temperature)
    {
        return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out temperature) &&
            double.IsFinite(temperature) &&
            temperature is >= 0.0 and <= 1.0;
    }
}
