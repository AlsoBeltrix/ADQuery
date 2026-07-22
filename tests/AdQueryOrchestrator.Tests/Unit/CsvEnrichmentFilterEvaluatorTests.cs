using AdQuery.Orchestrator.Services;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class CsvEnrichmentFilterEvaluatorTests
{
    [Fact]
    public void SupportedOperators_AreExactlyTheCsvCapabilities()
    {
        var evaluator = new CsvEnrichmentFilterEvaluator();

        Assert.Equal(
            ["contains", "ends_with", "equals", "not_contains", "not_equals", "starts_with"],
            evaluator.SupportedOperators.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData(CsvEnrichmentFilterOperator.Equals, "alpha", true)]
    [InlineData(CsvEnrichmentFilterOperator.Equals, "gamma", false)]
    [InlineData(CsvEnrichmentFilterOperator.NotEquals, "gamma", true)]
    [InlineData(CsvEnrichmentFilterOperator.NotEquals, "alpha", false)]
    [InlineData(CsvEnrichmentFilterOperator.Contains, "PH", true)]
    [InlineData(CsvEnrichmentFilterOperator.Contains, "zzz", false)]
    [InlineData(CsvEnrichmentFilterOperator.NotContains, "zzz", true)]
    [InlineData(CsvEnrichmentFilterOperator.NotContains, "a", false)]
    [InlineData(CsvEnrichmentFilterOperator.StartsWith, "be", true)]
    [InlineData(CsvEnrichmentFilterOperator.StartsWith, "ta", false)]
    [InlineData(CsvEnrichmentFilterOperator.EndsWith, "TA", true)]
    [InlineData(CsvEnrichmentFilterOperator.EndsWith, "be", false)]
    public void Evaluate_PreservesScalarAndMultiValueSemantics(
        CsvEnrichmentFilterOperator filterOperator,
        string expectedValue,
        bool expected)
    {
        var evaluator = new CsvEnrichmentFilterEvaluator();
        var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["department"] = new[] { "Alpha", "Beta" }
        };

        var actual = evaluator.Evaluate(
            attributes,
            "DEPARTMENT",
            filterOperator,
            expectedValue);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Evaluate_TreatsScalarStringAsOneCandidate()
    {
        var evaluator = new CsvEnrichmentFilterEvaluator();
        var attributes = new Dictionary<string, object?>
        {
            ["department"] = "Engineering"
        };

        var actual = evaluator.Evaluate(
            attributes,
            "department",
            CsvEnrichmentFilterOperator.Equals,
            "engineering");

        Assert.True(actual);
    }

    [Fact]
    public void UnknownOperator_DoesNotParseOrEvaluateAsEquals()
    {
        var evaluator = new CsvEnrichmentFilterEvaluator();
        var attributes = new Dictionary<string, object?>
        {
            ["department"] = "Engineering"
        };

        Assert.False(evaluator.TryParseOperator("unknown", out _));
        Assert.False(evaluator.TryParseOperator(" equals ", out _));
        _ = Assert.Throws<ArgumentOutOfRangeException>(() => evaluator.Evaluate(
            attributes,
            "department",
            (CsvEnrichmentFilterOperator)999,
            "Engineering"));
    }
}
