using System.Collections;
using System.Collections.Frozen;

namespace AdQuery.Orchestrator.Services;

public sealed class CsvEnrichmentFilterEvaluator : ICsvEnrichmentFilterEvaluator
{
    private static readonly FrozenDictionary<string, CsvEnrichmentFilterOperator> Operators =
        new Dictionary<string, CsvEnrichmentFilterOperator>(StringComparer.OrdinalIgnoreCase)
        {
            ["equals"] = CsvEnrichmentFilterOperator.Equals,
            ["not_equals"] = CsvEnrichmentFilterOperator.NotEquals,
            ["contains"] = CsvEnrichmentFilterOperator.Contains,
            ["not_contains"] = CsvEnrichmentFilterOperator.NotContains,
            ["starts_with"] = CsvEnrichmentFilterOperator.StartsWith,
            ["ends_with"] = CsvEnrichmentFilterOperator.EndsWith
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenSet<string> OperatorNames =
        Operators.Keys.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    public IReadOnlySet<string> SupportedOperators => OperatorNames;

    public bool TryParseOperator(
        string? operatorValue,
        out CsvEnrichmentFilterOperator parsedOperator)
    {
        if (operatorValue is not null && Operators.TryGetValue(operatorValue, out parsedOperator))
        {
            return true;
        }

        parsedOperator = default;
        return false;
    }

    public bool Evaluate(
        IReadOnlyDictionary<string, object?> attributes,
        string attribute,
        CsvEnrichmentFilterOperator filterOperator,
        string expectedValue)
    {
        if (!attributes.TryGetValue(attribute, out var value))
        {
            return false;
        }

        var candidates = ExtractCandidates(value).ToList();
        if (candidates.Count == 0)
        {
            candidates.Add(string.Empty);
        }

        return filterOperator switch
        {
            CsvEnrichmentFilterOperator.Equals =>
                candidates.Any(candidate => candidate.Equals(expectedValue, StringComparison.OrdinalIgnoreCase)),
            CsvEnrichmentFilterOperator.NotEquals =>
                candidates.All(candidate => !candidate.Equals(expectedValue, StringComparison.OrdinalIgnoreCase)),
            CsvEnrichmentFilterOperator.Contains =>
                candidates.Any(candidate => candidate.Contains(expectedValue, StringComparison.OrdinalIgnoreCase)),
            CsvEnrichmentFilterOperator.NotContains =>
                candidates.All(candidate => !candidate.Contains(expectedValue, StringComparison.OrdinalIgnoreCase)),
            CsvEnrichmentFilterOperator.StartsWith =>
                candidates.Any(candidate => candidate.StartsWith(expectedValue, StringComparison.OrdinalIgnoreCase)),
            CsvEnrichmentFilterOperator.EndsWith =>
                candidates.Any(candidate => candidate.EndsWith(expectedValue, StringComparison.OrdinalIgnoreCase)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(filterOperator),
                filterOperator,
                "Unsupported CSV enrichment filter operator.")
        };
    }

    private static IEnumerable<string> ExtractCandidates(object? value)
    {
        if (value is null)
        {
            yield break;
        }

        if (value is string text)
        {
            yield return text;
            yield break;
        }

        if (value is byte[] bytes)
        {
            yield return Convert.ToBase64String(bytes);
            yield break;
        }

        if (value is IEnumerable enumerable)
        {
            foreach (var item in enumerable)
            {
                yield return item?.ToString() ?? string.Empty;
            }
            yield break;
        }

        yield return value.ToString() ?? string.Empty;
    }
}
