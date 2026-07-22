namespace AdQuery.Orchestrator.Configuration;

public sealed class LlmProviderOptions
{
    public const string SectionName = "Claude";

    public string? ApiKey { get; set; }

    public string? BaseUrl { get; set; }

    public string? AuthToken { get; set; }

    public string? Endpoint { get; set; }

    public string? Model { get; set; }

    public string? AlternateModel { get; set; }

    public string? AlternateModelDisplayName { get; set; }

    public string MaxTokens { get; set; } = "4000";

    public string? PromptTemplate { get; set; }

    /// <summary>
    /// Legacy global value retained only so validation can warn that it is ignored.
    /// It never enables sampling.
    /// </summary>
    public string? Temperature { get; set; }

    public List<LlmSamplingProfileOptions> SamplingProfiles { get; set; } = [];
}

public sealed class LlmSamplingProfileOptions
{
    public string? TargetModel { get; set; }

    public string Mode { get; set; } = LlmSamplingModes.Omit;

    public string? Temperature { get; set; }
}

public static class LlmSamplingModes
{
    public const string Omit = "Omit";
    public const string Temperature = "Temperature";
}
