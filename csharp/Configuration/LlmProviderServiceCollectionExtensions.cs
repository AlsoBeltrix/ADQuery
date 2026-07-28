using AdQuery.Orchestrator.Security;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Configuration;

public static class LlmProviderServiceCollectionExtensions
{
    public static IServiceCollection AddLlmProviderConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<LlmProviderOptions>()
            .Bind(configuration.GetSection(LlmProviderOptions.SectionName))
            // F03 Slice 1: when the config ApiKey is blank, fall back to the
            // DPAPI-encrypted store outside the web root (Claude:ApiKeyFile, default
            // C:\ProgramData\ADQuery\claude-apikey.dat). An explicit non-blank
            // Claude:ApiKey in config still wins, so nothing changes for anyone
            // setting it directly. PostConfigure runs before ValidateOnStart.
            .PostConfigure(options =>
            {
                if (!string.IsNullOrWhiteSpace(options.ApiKey))
                {
                    return;
                }

                var filePath = configuration[$"{LlmProviderOptions.SectionName}:ApiKeyFile"];
                if (string.IsNullOrWhiteSpace(filePath))
                {
                    filePath = ProtectedApiKeyProvider.DefaultApiKeyFilePath;
                }

                var storedKey = ProtectedApiKeyProvider.TryReadApiKey(filePath);
                if (!string.IsNullOrWhiteSpace(storedKey))
                {
                    options.ApiKey = storedKey;
                }
            })
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<LlmProviderOptions>, LlmProviderOptionsValidator>();
        services.AddSingleton<LlmMessagesRequestBuilder>();

        return services;
    }
}
