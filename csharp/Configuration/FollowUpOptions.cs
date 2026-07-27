namespace AdQuery.Orchestrator.Configuration;

/// <summary>
/// Finite, startup-validated bound on follow-up context (F01 Slice C1, FOLLOWUP-D1).
///
/// Follow-up context is the last-turn material (bounded AD values, prior executed-plan
/// summary, prior question) sent back to the model to refine an answer. This is the
/// authoritative minimal-leakage ceiling on that material, measured in UTF-8 bytes and
/// separate from the preview-row cap. Zero never means unlimited.
/// </summary>
public sealed class FollowUpOptions
{
    public const string SectionName = "FollowUp";

    /// <summary>
    /// UTF-16 code-unit ceiling the request transport already imposes on
    /// <c>QueryRequest.Context</c> via its <c>[StringLength(2000)]</c> attribute. The
    /// byte cap must not exceed this, or binding-time rejection could pre-empt this
    /// byte handler for an input that is within the byte cap (each UTF-16 code unit
    /// encodes to at least one UTF-8 byte, so byte count &gt;= code-unit count).
    /// <c>FollowUpOptionsReconciliationTests</c> asserts this mirrors the attribute.
    /// </summary>
    public const int ContextTransportCodeUnitLimit = 2000;

    /// <summary>
    /// Maximum UTF-8 bytes of follow-up context that may be persisted, logged, or sent
    /// to the model. Defaults to the transport-permitted maximum
    /// (<see cref="ContextTransportCodeUnitLimit"/>), the loosest in-bounds value — the
    /// hard mechanism and knob, not a sized-for-typical-usage figure. F01's open item
    /// is to tighten this from a measured last-turn payload once Slice C2 constructs one.
    /// </summary>
    public int MaxContextBytes { get; set; } = ContextTransportCodeUnitLimit;
}
