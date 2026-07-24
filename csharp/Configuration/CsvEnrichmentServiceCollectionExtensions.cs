using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Configuration;

public static class CsvEnrichmentServiceCollectionExtensions
{
    public static IServiceCollection AddCsvEnrichmentConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<CsvEnrichmentLimitsOptions>()
            .Bind(configuration.GetSection(CsvEnrichmentLimitsOptions.SectionName))
            .ValidateOnStart();
        services.AddSingleton<
            IValidateOptions<CsvEnrichmentLimitsOptions>,
            CsvEnrichmentLimitsOptionsValidator>();

        return services;
    }
}
