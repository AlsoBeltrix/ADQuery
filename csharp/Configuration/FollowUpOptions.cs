namespace AdQuery.Orchestrator.Configuration;

/// <summary>
/// Finite, startup-validated bound on follow-up context (F01 Slice C1, FOLLOWUP-D1).
///
/// Follow-up context is the material (bounded AD values, prior executed-plan summary, and
/// — from F04 Slice 6 — the thread's accumulated question text) sent back to the model to
/// refine an answer. This is the authoritative minimal-leakage ceiling on that material,
/// measured in UTF-8 bytes and separate from the preview-row cap. Zero never means unlimited.
/// </summary>
public sealed class FollowUpOptions
{
    public const string SectionName = "FollowUp";

    /// <summary>
    /// Hard ceiling on how many questions one turn's context may carry (F04 Slice 6, F04-D6).
    /// Questions are the only component that grows with the thread, so this is the only input
    /// the derived ceilings below scale with. It is a deliberate thread-depth bound, not an
    /// extrapolation from observed threads (f04-or-6); Slice 6a's configurable
    /// max-prior-questions knob is validated against it.
    /// </summary>
    public const int MaxThreadQuestions = 20;

    /// <summary>Value-slice buckets, at the ceiling <c>QueryDefaults:SummaryRowCount</c> may take.</summary>
    private const int ValueSliceBuckets = 20;

    /// <summary>A clipped bucket key, its separator, and its count.</summary>
    private const int ValueSliceBucketCodeUnits = AnswerOptions.MaxValueChars + 16;

    /// <summary>Slack for the fixed section labels and line separators the builder emits.</summary>
    private const int LabelOverheadCodeUnits = 512;

    /// <summary>
    /// UTF-16 code units reserved for the components that do <em>not</em> grow with the
    /// thread: the executed-plan summary, the DATA-D1 value slice, and the fixed labels
    /// between them. Sized from the same per-component clips the Narrate reduction enforces
    /// (<see cref="AnswerOptions.MaxDescriptionChars"/>, <see cref="AnswerOptions.MaxValueChars"/>)
    /// at the value slice's bucket bound.
    /// <para>
    /// <c>FollowUpContextBuilder</c> bounds the value slice by bucket count but does not yet
    /// clip a model-authored plan description or a bucket key, so this is a reserved budget
    /// rather than an enforced maximum until Slice 6a applies those clips at the builder.
    /// </para>
    /// </summary>
    public const int FixedComponentCodeUnits =
        AnswerOptions.MaxDescriptionChars +
        (ValueSliceBuckets * ValueSliceBucketCodeUnits) +
        LabelOverheadCodeUnits;

    /// <summary>
    /// Worst-case UTF-16 code units a maximum thread composes: <see cref="MaxThreadQuestions"/>
    /// questions at the <c>QueryRequest.Query</c> <c>[StringLength]</c> maximum
    /// (<see cref="AnswerOptions.QuestionTransportCodeUnitLimit"/>), plus the fixed components.
    /// </summary>
    private const int MaxComposedCodeUnits =
        (MaxThreadQuestions * AnswerOptions.QuestionTransportCodeUnitLimit) + FixedComponentCodeUnits;

    /// <summary>
    /// Worst-case UTF-8 bytes per UTF-16 code unit. A BMP code unit encodes to at most three
    /// bytes; a surrogate pair is two code units encoding to four bytes, so three is the
    /// per-unit maximum. Matches <c>AnswerOptions</c>' derivation.
    /// </summary>
    private const int Utf8BytesPerCodeUnit = 3;

    /// <summary>
    /// UTF-16 code-unit ceiling the request transport imposes on <c>QueryRequest.Context</c>
    /// via its <c>[StringLength]</c> attribute. The byte cap must not exceed this, or
    /// binding-time rejection could pre-empt this byte handler for an input that is within
    /// the byte cap (each UTF-16 code unit encodes to at least one UTF-8 byte, so byte count
    /// &gt;= code-unit count). <c>FollowUpOptionsTests</c> asserts this mirrors the attribute.
    /// <para>
    /// F04 Slice 6b widened this from a flat 2000. The widened value is <b>derived from
    /// enforced maxima, never from observed samples</b> (f04-or-6): it is the worst-case
    /// <em>byte</em> size of a maximum thread, so a byte cap set to that worst case is still
    /// at or below this code-unit ceiling and the reconciliation above continues to hold.
    /// Slice 6a's derived-floor validation makes the byte cap a backstop that a legitimate
    /// maximum thread cannot trip, rather than the shaper F04-D6 rejects.
    /// </para>
    /// </summary>
    public const int ContextTransportCodeUnitLimit = MaxComposedCodeUnits * Utf8BytesPerCodeUnit;

    /// <summary>
    /// Maximum UTF-8 bytes of follow-up context that may be persisted, logged, or sent
    /// to the model. Defaults to the transport-permitted maximum
    /// (<see cref="ContextTransportCodeUnitLimit"/>), the loosest in-bounds value — the
    /// hard mechanism and knob, not a sized-for-typical-usage figure.
    /// </summary>
    public int MaxContextBytes { get; set; } = ContextTransportCodeUnitLimit;
}
