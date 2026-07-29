using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Configuration;

public static class AnswerServiceCollectionExtensions
{
    public static IServiceCollection AddAnswerConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<AnswerOptions>()
            .Bind(configuration.GetSection(AnswerOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<AnswerOptions>,
            AnswerOptionsValidator>();

        return services;
    }
}
