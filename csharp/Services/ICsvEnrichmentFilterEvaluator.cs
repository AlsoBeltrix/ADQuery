namespace AdQuery.Orchestrator.Services;

public enum CsvEnrichmentFilterOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    StartsWith,
    EndsWith
}

public interface ICsvEnrichmentFilterEvaluator
{
    IReadOnlySet<string> SupportedOperators { get; }

    bool TryParseOperator(
        string? operatorValue,
        out CsvEnrichmentFilterOperator parsedOperator);

    bool Evaluate(
        IReadOnlyDictionary<string, object?> attributes,
        string attribute,
        CsvEnrichmentFilterOperator filterOperator,
        string expectedValue);
}
