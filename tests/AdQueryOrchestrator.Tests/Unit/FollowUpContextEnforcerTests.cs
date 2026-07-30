using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F01 Slice C1 guard: the follow-up context enforcer is the authoritative UTF-8 byte
/// bound. It never splits a UTF-8 code point and never emits a fragment.
///
/// F04 Slice 6a (F04-D6) retired the silent values → plan → question drop ladder: the
/// component bounds shape the context and the byte cap is only a backstop, so an over-cap
/// composition is a broken component bound. It is logged as an error and the context is
/// dropped whole, never quietly trimmed to a smaller one.
/// </summary>
public sealed class FollowUpContextEnforcerTests
{
    private static FollowUpContextEnforcer Enforcer(int maxBytes) =>
        Enforcer(maxBytes, out _);

    private static FollowUpContextEnforcer Enforcer(int maxBytes, out CapturingLogger logger)
    {
        logger = new CapturingLogger();
        return new FollowUpContextEnforcer(
            Options.Create(new FollowUpOptions { MaxContextBytes = maxBytes }), logger);
    }

    [Fact]
    public void Compose_AllFit_KeepsEveryComponentInFixedOrder()
    {
        var result = Enforcer(1000).Compose(
            new FollowUpContextComponents(Values: "values", PlanSummary: "plan", PriorQuestion: "question"));

        Assert.Equal("question\nplan\nvalues", result);
    }

    [Fact]
    public void Compose_OverCap_DropsWhole_AndLogsAnError()
    {
        // The retired ladder returned "question\nplan" here. Under F04-D6 a cap that the
        // components can overflow is a configuration defect the startup validator rejects,
        // so at runtime this is an error, not a routine trim.
        var components = new FollowUpContextComponents(
            Values: new string('v', 100),
            PlanSummary: "plan",
            PriorQuestion: "question");
        var enforcer = Enforcer(Encoding.UTF8.GetByteCount("question\nplan"), out var logger);

        Assert.Null(enforcer.Compose(components));
        Assert.Contains(logger.Entries, entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public void Compose_OverCap_LogsSizesButNeverTheContext()
    {
        // FOLLOWUP-D1 governs what may leave the process, logs included: the diagnostic
        // names sizes, never the AD values that overflowed.
        var components = new FollowUpContextComponents(
            Values: "VALUES_SENTINEL",
            PlanSummary: "PLAN_SENTINEL",
            PriorQuestion: "QUESTION_SENTINEL");
        var enforcer = Enforcer(4, out var logger);

        Assert.Null(enforcer.Compose(components));

        var logged = string.Join(" ", logger.Entries.Select(entry => entry.Message));
        Assert.DoesNotContain("SENTINEL", logged);
        Assert.Contains("component bound is broken", logged);
    }

    [Fact]
    public void Compose_WithinCap_LogsNothing()
    {
        var enforcer = Enforcer(1000, out var logger);

        enforcer.Compose(new FollowUpContextComponents(
            Values: "values", PlanSummary: "plan", PriorQuestion: "question"));

        Assert.Empty(logger.Entries);
    }

    [Fact]
    public void Compose_NeverSplitsAUtf8CodePoint()
    {
        // The prior question is multi-byte UTF-8 (each emoji is 4 bytes). A cap that does
        // not admit the whole composition drops it whole; the result is either the exact
        // full text or null — never a byte-truncated fragment.
        var question = "😀😀😀"; // 12 UTF-8 bytes
        var components = new FollowUpContextComponents(
            Values: null, PlanSummary: null, PriorQuestion: question);

        Assert.Null(Enforcer(11).Compose(components));

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

    private sealed class CapturingLogger : ILogger<FollowUpContextEnforcer>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception)));
    }
}
