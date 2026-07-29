using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// slice5-or-2 guard. <c>AccountExpirationDate equals ""</c> is the one positive-operator
/// empty-value form the validator admits — the legacy "never expires" reading — and the LDAP
/// clause builder has always read it correctly. The in-memory evaluator did not: it fell
/// through to generic dispatch and compared the empty needle literally against the value the
/// search layer synthesizes, which is the string "Never", so a projection filter carrying this
/// form silently discarded every record it was written to select.
///
/// The invariant at stake is the one <c>EmptyValueFilterSemantics</c> exists to hold: a filter
/// the validator accepts must be one every evaluator reads identically.
/// </summary>
public sealed class NeverExpiresFilterTests
{
    private const string Attribute = "AccountExpirationDate";

    [Fact]
    public async Task NeverExpiresFilter_ReturnsTheNeverExpiringRecords()
    {
        // Pre-fix this returned the empty set: no record holds "", so nothing matched.
        var names = await ExecuteAsync("equals", value: "");

        Assert.Equal(["NeverExpires", "NeverExpiresLowercase"], names);
    }

    [Fact]
    public async Task WhitespaceValue_ReadsIdenticallyToEmpty()
    {
        // The validator's emptiness test is IsNullOrWhiteSpace, so "   " must take the same
        // branch rather than falling through as a real value to compare against.
        Assert.Equal(
            ["NeverExpires", "NeverExpiresLowercase"],
            await ExecuteAsync("equals", value: "   "));
    }

    [Fact]
    public async Task ARealDateValue_KeepsOrdinaryEqualsSemantics()
    {
        // The never-expires reading must not swallow the normal case.
        Assert.Equal(["Expires2030"], await ExecuteAsync("equals", value: "2030-01-01"));
    }

    [Fact]
    public void TheMarkerMatchesWhatTheSearchLayerWrites()
    {
        // ActiveDirectoryService.MapToRecord writes this literal for an account with no
        // expiration; the two must not drift apart independently.
        Assert.Equal("Never", EmptyValueFilterSemantics.NeverExpiresValue);
    }

    [Fact]
    public void OnlyAccountExpirationDateEquals_CarriesTheNeverExpiresReading()
    {
        // The over-removal sentinels. A negation stays the populated reading, another
        // attribute is not admitted at all, and a real value is an ordinary comparison.
        Assert.True(EmptyValueFilterSemantics.IsNeverExpiresFilter(Attribute, "equals", ""));
        Assert.True(EmptyValueFilterSemantics.IsNeverExpiresFilter(Attribute, null, "  "));

        Assert.False(EmptyValueFilterSemantics.IsNeverExpiresFilter(Attribute, "not_equals", ""));
        Assert.False(EmptyValueFilterSemantics.IsNeverExpiresFilter(Attribute, "equals", "2030-01-01"));
        Assert.False(EmptyValueFilterSemantics.IsNeverExpiresFilter("manager", "equals", ""));
    }

    [Fact]
    public async Task NegationStillMeansPopulated_NotNeverExpires()
    {
        // AccountExpirationDate is synthesized, so every record has it populated
        // (slice5-or-1). The never-expires branch must not capture the negation form.
        var names = await ExecuteAsync("not_equals", value: "");

        Assert.Equal(["NeverExpires", "NeverExpiresLowercase", "Expires2030"], names);
    }

    /// <summary>
    /// Records as the search layer leaves them: an expiring account carries a formatted date,
    /// a never-expiring one carries the synthesized literal — never the empty string.
    /// </summary>
    private static IReadOnlyList<DirectoryRecord> Matrix()
    {
        var never = Record("NeverExpires");
        never[Attribute] = "Never";

        var neverLower = Record("NeverExpiresLowercase");
        neverLower[Attribute] = "never";

        var expires = Record("Expires2030");
        expires[Attribute] = "2030-01-01";

        return [never, neverLower, expires];
    }

    private static DirectoryRecord Record(string name)
    {
        var record = new DirectoryRecord { DistinguishedName = $"CN={name},DC=x" };
        record["displayName"] = name;
        return record;
    }

    private static async Task<List<string?>> ExecuteAsync(string op, string value)
    {
        DirectoryFilter Filter() => new() { Attribute = Attribute, Operator = op, Value = value };

        var plan = new DirectoryQueryPlan
        {
            Description = "never-expires projection filter",
            Steps =
            {
                new DirectoryPlanStep
                {
                    Step = 1,
                    Name = "row",
                    Operation = "search",
                    TargetType = DirectoryObjectType.User,
                    Attributes = { "displayName", Attribute },
                    Filters = { Filter() },
                },
            },
            Projection = new ProjectionDefinition
            {
                RowStep = "row",
                Columns = { new ProjectionColumn { Name = "Name", Attribute = "displayName" } },
                Filter = Filter(),
            },
        };

        var executor = new DirectoryPlanExecutor(
            NullLogger<DirectoryPlanExecutor>.Instance,
            new PermissiveValidator(),
            new FixedDirectoryService(Matrix()));

        var result = await executor.ExecutePlanAsync(plan, TestContext.Current.CancellationToken);

        Assert.True(result.Success, string.Join(Environment.NewLine, result.Errors));
        return result.Data.Select(row => row["Name"] as string).ToList();
    }

    private sealed class FixedDirectoryService : IActiveDirectoryService
    {
        private readonly IReadOnlyList<DirectoryRecord> _records;

        public FixedDirectoryService(IReadOnlyList<DirectoryRecord> records) => _records = records;

        public Task<IReadOnlyList<DirectoryRecord>> SearchAsync(
            DirectorySearchRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult(_records);

        public Task<IReadOnlyList<DirectoryRecord>> ExpandGroupMembersAsync(
            IEnumerable<string> groupDistinguishedNames, bool recursive, IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DirectoryRecord>>([]);

        public Task<IReadOnlyList<DirectoryRecord>> LookupAsync(
            IEnumerable<string> distinguishedNames, DirectoryObjectType targetType, IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DirectoryRecord>>([]);

        public Task<IReadOnlyList<DirectoryRecord>> GetDirectReportsBatch(
            IEnumerable<string> managerDistinguishedNames, IEnumerable<string> attributes,
            CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<DirectoryRecord>>([]);
    }

    private sealed class PermissiveValidator : IPlanValidator
    {
        public Task<PlanSecurityResult> ValidateSecurityAsync(DirectoryQueryPlan plan)
            => Task.FromResult(new PlanSecurityResult());

        public bool ValidateHmac(DirectoryQueryPlan plan, string signature) => true;

        public bool ValidateComplexity(DirectoryQueryPlan plan) => true;
    }
}
