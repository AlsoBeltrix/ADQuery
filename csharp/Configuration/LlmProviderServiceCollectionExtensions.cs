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
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<LlmProviderOptions>, LlmProviderOptionsValidator>();
        services.AddSingleton<LlmMessagesRequestBuilder>();

        return services;
    }
}
