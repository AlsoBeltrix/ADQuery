using System.Collections.Generic;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F06 finding `f06s2-cr-1` (HIGH): an aggregation field must be checked against the object
/// type the rows actually come from, not always <see cref="DirectoryObjectType.User"/>.
///
/// `PlanValidator` carried "assume User type for aggregation fields" from before this work, so
/// a Computer plan grouping on a User-only attribute was already admitted. The synonym map
/// widened that existing hole rather than opening it — `l` reached `City` on the User list —
/// which is exactly the kind of interaction a boundary change has to be checked for.
///
/// The fix validates against the target type of `projection.row_step`. These guards hold both
/// halves: the widened case is refused, and the legitimate case still passes.
/// </summary>
public sealed class AggregationRespectsRowStepTypeTests
{
    private static PlanValidator Validator()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Security:AttributeFiles:User"] = "Configuration/user_allow_attr.txt",
                ["Security:AttributeFiles:Group"] = "Configuration/group_allow_attr.txt",
                ["Security:AttributeFiles:Computer"] = "Configuration/comp_allow_attr.txt",
                ["Security:AttributeFiles:OrganizationalUnit"] = "Configuration/ou_allow_attr.txt",
            })
            .Build();

        var policy = new DirectorySecurityPolicy(
            configuration,
            new StubEnvironment(),
            NullLogger<PlanValidator>.Instance);

        return new PlanValidator(NullLogger<PlanValidator>.Instance, configuration, policy);
    }

    private static DirectoryQueryPlan PlanGroupingOn(DirectoryObjectType rowType, string groupBy) => new()
    {
        Description = "grouped",
        Steps =
        {
            new DirectoryPlanStep
            {
                Step = 1,
                Name = "rows",
                Operation = "search",
                TargetType = rowType,
                Filters = { new DirectoryFilter { Attribute = "displayName", Operator = "contains", Value = "x" } },
            },
        },
        Projection = new ProjectionDefinition
        {
            RowStep = "rows",
            Aggregation = new AggregationDefinition { Count = true, GroupBy = [groupBy] },
        },
    };

    [Theory]
    // The LDAP name the synonym map resolves — the widened case the finding names.
    [InlineData("l")]
    // Its display name, which the pre-existing hardcoded-User check already admitted. Both
    // must now be refused: the fix closes the original hole, not only the part I added.
    [InlineData("City")]
    public async Task AComputerPlan_CannotGroupOnAUserOnlyAttribute(string groupBy)
    {
        var result = await Validator().ValidateSecurityAsync(PlanGroupingOn(DirectoryObjectType.Computer, groupBy));

        Assert.False(
            result.OperationsValid,
            $"grouping Computer rows on '{groupBy}' must be refused: it is not on the Computer allow-list.");
    }

    [Theory]
    [InlineData("l")]
    [InlineData("City")]
    [InlineData("Department")]
    public async Task AUserPlan_CanStillGroupOnAUserAttribute(string groupBy)
    {
        // Over-correction guard. Tightening the check must not break the ordinary case, under
        // either naming convention.
        var result = await Validator().ValidateSecurityAsync(PlanGroupingOn(DirectoryObjectType.User, groupBy));

        Assert.True(
            result.OperationsValid,
            $"grouping User rows on '{groupBy}' must be allowed: "
            + string.Join("; ", result.SecurityErrors));
    }

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "tests";
        public string ContentRootPath { get; set; } = System.AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
        public string EnvironmentName { get; set; } = "Test";
        public string WebRootPath { get; set; } = System.AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider WebRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
