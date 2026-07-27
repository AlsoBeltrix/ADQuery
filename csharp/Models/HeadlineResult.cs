using System.Collections.Generic;

namespace AdQuery.Orchestrator.Models;

/// <summary>
/// Deterministic, plan-shape-derived headline answer for a completed query
/// (F01 Slice B1). Computed server-side; exposed on the async job status so the
/// browser can lead with a direct answer instead of a raw grid. Values are
/// bounded per DATA-D1 before they leave the server.
/// </summary>
public sealed class HeadlineResult
{
    /// <summary>The headline kind. See <see cref="HeadlineKind"/> for values.</summary>
    public string Kind { get; init; } = HeadlineKind.None;

    /// <summary>Total matching row count. Present for <c>count</c> and <c>grouped</c>.</summary>
    public int? Count { get; init; }

    /// <summary>The single projected record. Present only for <c>record</c>.</summary>
    public IReadOnlyDictionary<string, object?>? Record { get; init; }

    /// <summary>The bounded grouped counts. Present only for <c>grouped</c>.</summary>
    public IReadOnlyList<HeadlineGroup>? Groups { get; init; }
}

/// <summary>
/// The fixed set of headline kinds. The classifier assigns exactly one per
/// completed job with deterministic precedence.
/// </summary>
public static class HeadlineKind
{
    /// <summary>Zero result rows; no value payload.</summary>
    public const string None = "none";

    /// <summary>A single scalar count is the answer (pure-count, distinct-list, or multi-row).</summary>
    public const string Count = "count";

    /// <summary>Exactly one non-expansion record is the answer.</summary>
    public const string Record = "record";

    /// <summary>A bounded set of grouped counts is the answer.</summary>
    public const string Grouped = "grouped";
}

/// <summary>One (key, count) pair in a <c>grouped</c> headline.</summary>
public sealed class HeadlineGroup
{
    public string Key { get; init; } = string.Empty;

    public int Count { get; init; }
}
