using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Configuration;

public static class FollowUpServiceCollectionExtensions
{
    public static IServiceCollection AddFollowUpConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<FollowUpOptions>()
            .Bind(configuration.GetSection(FollowUpOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<FollowUpOptions>,
            FollowUpOptionsValidator>();

        return services;
    }
}
