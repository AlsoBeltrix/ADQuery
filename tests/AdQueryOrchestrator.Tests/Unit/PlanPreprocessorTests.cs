using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

public sealed class PlanPreprocessorTests
{
    [Fact]
    public void EnsurePlanLimit_AppliesPositiveRequestedLimitToPlan()
    {
        var plan = new DirectoryQueryPlan();
        var sut = CreatePreprocessor();

        sut.EnsurePlanLimit(plan, 100);

        Assert.Equal((int?)100, plan.ResultLimit);
    }

    [Fact]
    public void EnsurePlanLimit_PreservesExistingStricterPlanLimit()
    {
        var plan = new DirectoryQueryPlan { ResultLimit = 25 };
        var sut = CreatePreprocessor();

        sut.EnsurePlanLimit(plan, 100);

        Assert.Equal((int?)25, plan.ResultLimit);
    }

    [Fact]
    public void EnsurePlanLimit_AppliesEffectiveLimitToProjectionRowStep()
    {
        var decoy = CreateSearchStep("decoy");
        var row = CreateSearchStep("row");
        var plan = new DirectoryQueryPlan
        {
            ResultLimit = 25,
            Steps = [decoy, row],
            Projection = new ProjectionDefinition { RowStep = "row" }
        };
        var sut = CreatePreprocessor();

        sut.EnsurePlanLimit(plan, 50);

        Assert.Equal((int?)25, plan.ResultLimit);
        Assert.Null(decoy.SizeLimit);
        Assert.Equal((int?)25, row.SizeLimit);
    }

    [Fact]
    public void EnsurePlanLimit_PreservesExistingStricterRowStepLimit()
    {
        var row = CreateSearchStep("row");
        row.SizeLimit = 25;
        var plan = new DirectoryQueryPlan
        {
            Steps = [row],
            Projection = new ProjectionDefinition { RowStep = "row" }
        };
        var sut = CreatePreprocessor();

        sut.EnsurePlanLimit(plan, 100);

        Assert.Equal((int?)25, row.SizeLimit);
    }

    [Fact]
    public void ApplyCustomMappings_NormalizesNestedOperatorsRecursively()
    {
        var leaf = new DirectoryFilter
        {
            Attribute = "department",
            Operator = "DOES NOT CONTAIN",
            Value = "Finance"
        };
        var child = new DirectoryFilter
        {
            Operator = " OR ",
            Conditions = [leaf]
        };
        var root = new DirectoryFilter
        {
            Operator = " AND ",
            Conditions = [child]
        };
        var plan = CreatePlanWithStepFilter(root);
        var sut = CreatePreprocessor();

        sut.ApplyCustomMappings(plan);

        Assert.Equal("and", root.Operator);
        Assert.Equal("or", child.Operator);
        Assert.Equal("not_contains", leaf.Operator);
    }

    [Fact]
    public void ApplyCustomMappings_TrimsLeafAttributeAndValue()
    {
        var filter = new DirectoryFilter
        {
            Attribute = "  department ",
            Operator = "equals",
            Value = "  Finance  "
        };
        var plan = CreatePlanWithStepFilter(filter);
        var sut = CreatePreprocessor();

        sut.ApplyCustomMappings(plan);

        Assert.Equal("department", filter.Attribute);
        Assert.Equal("Finance", filter.Value);
    }

    [Fact]
    public void ApplyCustomMappings_MapsConfiguredLicenseAlias()
    {
        var filter = new DirectoryFilter
        {
            Attribute = "  LICENSEDSKU ",
            Operator = "equals",
            Value = "E5"
        };
        var plan = CreatePlanWithStepFilter(filter);
        var sut = CreatePreprocessor(new Dictionary<string, string?>
        {
            ["CustomMappings:ExtensionAttributes:ExtensionAttribute11"] = "licensedSku"
        });

        sut.ApplyCustomMappings(plan);

        Assert.Equal("extensionAttribute11", filter.Attribute);
    }

    [Fact]
    public void ApplyCustomMappings_NormalizesProjectionFilter()
    {
        var projectionFilter = new DirectoryFilter
        {
            Attribute = " license ",
            Operator = "does not contain",
            Value = "  E3  "
        };
        var plan = new DirectoryQueryPlan
        {
            Steps = [CreateSearchStep("row")],
            Projection = new ProjectionDefinition
            {
                RowStep = "row",
                Filter = projectionFilter
            }
        };
        var sut = CreatePreprocessor();

        sut.ApplyCustomMappings(plan);

        Assert.Equal("extensionAttribute11", projectionFilter.Attribute);
        Assert.Equal("not_contains", projectionFilter.Operator);
        Assert.Equal("E3", projectionFilter.Value);
    }

    private static PlanPreprocessor CreatePreprocessor(
        IEnumerable<KeyValuePair<string, string?>>? settings = null)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .Build();

        return new PlanPreprocessor(configuration);
    }

    private static DirectoryQueryPlan CreatePlanWithStepFilter(DirectoryFilter filter)
    {
        var step = CreateSearchStep("row");
        step.Filters = [filter];

        return new DirectoryQueryPlan
        {
            Steps = [step],
            Projection = new ProjectionDefinition { RowStep = "row" }
        };
    }

    private static DirectoryPlanStep CreateSearchStep(string name)
    {
        return new DirectoryPlanStep
        {
            Name = name,
            Operation = "search",
            TargetType = DirectoryObjectType.User
        };
    }
}
