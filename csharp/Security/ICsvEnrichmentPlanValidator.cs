using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Security;

public interface ICsvEnrichmentPlanValidator
{
    CsvEnrichmentPlanValidationResult Validate(
        CsvEnrichmentPlan? plan,
        IReadOnlyList<string>? csvHeaders);
}

public sealed class CsvEnrichmentPlanValidationResult
{
    internal CsvEnrichmentPlanValidationResult(
        IReadOnlyList<string> errors,
        ValidatedCsvEnrichmentPlan? executionPlan)
    {
        Errors = errors;
        ExecutionPlan = executionPlan;
    }

    public bool IsValid => Errors.Count == 0 && ExecutionPlan is not null;

    public IReadOnlyList<string> Errors { get; }

    internal ValidatedCsvEnrichmentPlan? ExecutionPlan { get; }
}
