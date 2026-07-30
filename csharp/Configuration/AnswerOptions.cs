namespace AdQuery.Orchestrator.Configuration;

/// <summary>
/// Finite, startup-validated bound on the Narrate reduction (F04 Slice 2, F04-D1).
///
/// The reduction is the only material the second model call sees: the user's question,
/// the plan description, the B1 headline, and a handful of distribution scalars. It never
/// carries result rows or the full set (DATA-D1). This is the authoritative ceiling on
/// that material, measured in UTF-8 bytes. Zero never means unlimited.
///
/// The default is derived from the component maxima the builder itself enforces
/// (<see cref="ReductionCeilingBytes"/>), not from a sized-for-typical-usage figure.
/// </summary>
public sealed class AnswerOptions
{
    public const string SectionName = "Answer";

    /// <summary>
    /// UTF-16 code-unit ceiling the request transport imposes on <c>QueryRequest.Query</c>
    /// via its <c>[StringLength(1000)]</c> attribute. <c>AnswerOptionsTests</c> asserts this
    /// mirrors the attribute.
    /// </summary>
    public const int QuestionTransportCodeUnitLimit = 1000;

    /// <summary>Model-authored plan descriptions are unbounded; the builder clips them here.</summary>
    public const int MaxDescriptionChars = 500;

    /// <summary>Per-value clip for a headline group key, a record field name, or a record value.</summary>
    public const int MaxValueChars = 120;

    /// <summary>Fields carried from a single-record headline.</summary>
    public const int MaxRecordFields = 12;

    /// <summary>Grouped buckets carried; mirrors <c>HeadlineClassifier.MaxHeadlineGroups</c>.</summary>
    public const int MaxGroupBuckets = 10;

    /// <summary>
    /// Ceiling on the completeness line an incomplete result carries (ci-or-1). The line is a
    /// server-written constant rather than the executor's free-text warnings — which are
    /// unbounded in number and would make the derivation below impossible —
    /// so this is a bound on a fixed string. <c>AnswerReductionTests</c> asserts it holds.
    /// </summary>
    public const int MaxCompletenessChars = 256;

    /// <summary>
    /// Worst-case UTF-8 bytes per UTF-16 code unit. A BMP code unit encodes to at most three
    /// bytes; a surrogate pair is two code units encoding to four bytes, so three is the
    /// per-unit maximum.
    /// </summary>
    private const int Utf8BytesPerCodeUnit = 3;

    /// <summary>Slack for the fixed section labels and line separators the builder emits.</summary>
    private const int LabelOverheadBytes = 512;

    /// <summary>
    /// The sum of every component maximum the builder enforces, in worst-case UTF-8 bytes.
    /// A reduction cannot exceed this by construction, which is why it is the default and
    /// the validator's ceiling rather than an estimate of what a reduction usually costs.
    /// The headline contributes the larger of its two value-bearing shapes (a single record
    /// or the grouped buckets); the distribution summary contributes only integers, covered
    /// by <see cref="LabelOverheadBytes"/>. The completeness line (ci-or-1) is a fixed string
    /// and contributes its own bound, so an incomplete result cannot overflow the cap that a
    /// complete one fits.
    /// </summary>
    public const int ReductionCeilingBytes =
        (QuestionTransportCodeUnitLimit * Utf8BytesPerCodeUnit) +
        (MaxDescriptionChars * Utf8BytesPerCodeUnit) +
        (MaxRecordFields * 2 * MaxValueChars * Utf8BytesPerCodeUnit) +
        (MaxCompletenessChars * Utf8BytesPerCodeUnit) +
        LabelOverheadBytes;

    /// <summary>
    /// Maximum UTF-8 bytes of Narrate reduction that may be sent to the model. Whole
    /// components are dropped in a fixed order when the composition overflows; a fragment
    /// is never sent.
    /// </summary>
    public int MaxReductionBytes { get; set; } = ReductionCeilingBytes;
}
