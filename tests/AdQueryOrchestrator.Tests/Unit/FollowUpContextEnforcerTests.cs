using System.Text;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F01 Slice C1 guard: the follow-up context enforcer is the authoritative UTF-8 byte
/// bound. It drops whole components in the fixed order values → plan summary → prior
/// question, never splits a UTF-8 code point, and drops context entirely when even the
/// highest-priority component overflows.
/// </summary>
public sealed class FollowUpContextEnforcerTests
{
    private static FollowUpContextEnforcer Enforcer(int maxBytes) =>
        new(Options.Create(new FollowUpOptions { MaxContextBytes = maxBytes }));

    [Fact]
    public void Compose_AllFit_KeepsEveryComponentInFixedOrder()
    {
        var result = Enforcer(1000).Compose(
            new FollowUpContextComponents(Values: "values", PlanSummary: "plan", PriorQuestion: "question"));

        // Assembly order is question, plan, values regardless of drop priority.
        Assert.Equal("question\nplan\nvalues", result);
    }

    [Fact]
    public void Compose_OverCap_DropsValuesFirst()
    {
        // Cap admits question+plan but not the values component too.
        var components = new FollowUpContextComponents(
            Values: new string('v', 100),
            PlanSummary: "plan",
            PriorQuestion: "question");
        var cap = Encoding.UTF8.GetByteCount("question\nplan");

        var result = Enforcer(cap).Compose(components);

        Assert.Equal("question\nplan", result);
    }

    [Fact]
    public void Compose_TighterCap_DropsPlanNext_KeepsQuestion()
    {
        var components = new FollowUpContextComponents(
            Values: new string('v', 100),
            PlanSummary: new string('p', 100),
            PriorQuestion: "question");

        var result = Enforcer(Encoding.UTF8.GetByteCount("question")).Compose(components);

        Assert.Equal("question", result);
    }

    [Fact]
    public void Compose_EvenQuestionOverflows_DropsContextEntirely()
    {
        var components = new FollowUpContextComponents(
            Values: "v",
            PlanSummary: "p",
            PriorQuestion: new string('q', 50));

        Assert.Null(Enforcer(10).Compose(components));
    }

    [Fact]
    public void Compose_NeverSplitsAUtf8CodePoint()
    {
        // The prior question is multi-byte UTF-8 (each emoji is 4 bytes). Any cap that
        // does not admit the whole question drops it whole; the result is either the
        // exact full question or null — never a byte-truncated fragment.
        var question = "😀😀😀"; // 12 UTF-8 bytes
        var components = new FollowUpContextComponents(
            Values: null, PlanSummary: null, PriorQuestion: question);

        // A cap one byte short of the whole question must drop it entirely.
        Assert.Null(Enforcer(11).Compose(components));

        // A cap that admits it returns it intact and byte-clean (round-trips).
        var kept = Enforcer(12).Compose(components);
        Assert.Equal(question, kept);
        Assert.Equal(question, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(kept!)));
    }

    [Fact]
    public void Compose_BlankComponents_AreIgnored()
    {
        var result = Enforcer(1000).Compose(
            new FollowUpContextComponents(Values: "   ", PlanSummary: null, PriorQuestion: "question"));

        Assert.Equal("question", result);
    }

    [Fact]
    public void EnforceStored_WithinCap_ReturnsUnchanged()
    {
        Assert.Equal("in-bounds", Enforcer(1000).EnforceStored("in-bounds"));
    }

    [Fact]
    public void EnforceStored_OverCap_DropsEntirely_NoFragment()
    {
        // An opaque over-cap string has unknown component boundaries, so it is dropped
        // whole rather than truncated into a fragment.
        Assert.Null(Enforcer(4).EnforceStored(new string('x', 100)));
    }

    [Fact]
    public void EnforceStored_NullOrBlank_ReturnsNull()
    {
        Assert.Null(Enforcer(1000).EnforceStored(null));
        Assert.Null(Enforcer(1000).EnforceStored("   "));
    }
}
