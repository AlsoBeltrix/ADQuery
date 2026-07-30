using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AdQuery.Orchestrator.Configuration;
using AdQuery.Orchestrator.Models;
using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// The four components that compose a Narrate reduction (F04 Slice 2, F04-D1). Ordered
/// here by drop priority: when the composition exceeds the byte cap, whole components are
/// dropped in the fixed order <see cref="Headline"/> → <see cref="Distribution"/>, so the
/// question is retained longest. The headline carries the AD values, so it goes first — the
/// same drop ordering principle as <see cref="FollowUpContextComponents"/>.
/// <para>
/// Dropping stops there (slice7-or-4). <see cref="Headline"/> and <see cref="Distribution"/>
/// are the only components carrying evidence about the <em>result</em>;
/// <see cref="PlanDescription"/> states what was asked of the directory and
/// <see cref="Question"/> what the user asked, so a composition of those two alone would
/// hand Narrate a question and no facts to answer it with. When neither evidence-bearing
/// component fits, the reduction is null and Narrate is skipped.
/// </para>
/// <para>
/// That floor is enforced on the composition's <em>contents</em>, not on its position in
/// the drop order (slice2-or-1): <see cref="Distribution"/> is null for every result that
/// is not a grouped aggregation, so on a count or a single record the headline-dropping
/// step already lands on the factless composition.
/// </para>
/// </summary>
public sealed record AnswerReductionComponents(
    string? Headline,
    string? Distribution,
    string? PlanDescription,
    string? Question);

/// <summary>
/// Builds the bounded reduction the Narrate call sees (F04 Slice 2, F04-D1).
///
/// The reduction is <em>only</em> {question, plan description, B1 headline, distribution
/// scalars}, byte-capped by <c>Answer:MaxReductionBytes</c>. Result rows, the full set,
/// and the on-disk artifact never enter it — the headline's ≤10 buckets or one record is
/// the entire AD-value exposure, which is what DATA-D1 already permits for follow-up
/// context. The bound is authoritative and server-side: whole components are dropped, a
/// fragment is never emitted.
/// </summary>
public interface IAnswerReductionBuilder
{
    /// <summary>
    /// Assembles and byte-bounds the reduction. Returns <c>null</c> when nothing composable
    /// survives the cap, in which case Narrate is skipped and the job completes without an
    /// answer.
    /// </summary>
    string? Build(
        string question,
        DirectoryQueryPlan? plan,
        HeadlineResult headline,
        DistributionSummary? distribution);
}

/// <inheritdoc />
public sealed class AnswerReductionBuilder : IAnswerReductionBuilder
{
    private readonly IOptions<AnswerOptions> _options;

    public AnswerReductionBuilder(IOptions<AnswerOptions> options)
    {
        _options = options;
    }

    private int MaxBytes => _options.Value.MaxReductionBytes;

    public string? Build(
        string question,
        DirectoryQueryPlan? plan,
        HeadlineResult headline,
        DistributionSummary? distribution)
    {
        System.ArgumentNullException.ThrowIfNull(headline);

        var components = new AnswerReductionComponents(
            Headline: BuildHeadline(headline, plan),
            Distribution: BuildDistribution(distribution),
            PlanDescription: BuildPlanDescription(plan),
            Question: BuildQuestion(question));

        // Assembly order is fixed and independent of which components survive: question,
        // plan description, distribution, headline. Drop order is the reverse.
        var normalized = new AnswerReductionComponents(
            Headline: Normalize(components.Headline),
            Distribution: Normalize(components.Distribution),
            PlanDescription: Normalize(components.PlanDescription),
            Question: Normalize(components.Question));

        AnswerReductionComponents[] ladder =
        {
            normalized,
            normalized with { Headline = null },
        };

        // The ladder ends at the last rung still carrying result evidence (slice7-or-4).
        // A composition of {question, plan description} alone is what was asked, twice over,
        // and nothing about what came back — so Narrate would answer from nothing and the
        // invented answer would sit above a correct headline and table. Returning null
        // instead skips Narrate and completes the job with headline, table, and export,
        // which is the documented behaviour for a null reduction.
        //
        // The floor is decided by what a composition contains, not by which rung produced it
        // (slice2-or-1): the distribution is null for every non-grouped result, so the
        // headline-dropping rung is itself factless on the count and single-record shapes.
        foreach (var candidate in ladder)
        {
            if (!CarriesResultEvidence(candidate))
            {
                continue;
            }

            var assembled = Assemble(candidate);
            if (assembled is not null && FitsCap(assembled))
            {
                return assembled;
            }
        }

        return null;
    }

    /// <summary>
    /// True when the composition says something about the <em>result</em>. Only the headline
    /// and the distribution do; the question and the plan description both describe what was
    /// asked.
    /// </summary>
    private static bool CarriesResultEvidence(AnswerReductionComponents components)
        => components.Headline is not null || components.Distribution is not null;

    private bool FitsCap(string value) => Encoding.UTF8.GetByteCount(value) <= MaxBytes;

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string? Assemble(AnswerReductionComponents components)
    {
        string?[] ordered =
        {
            components.Question,
            components.PlanDescription,
            components.Distribution,
            components.Headline,
        };

        var present = ordered.Where(component => component is not null).ToArray();
        return present.Length == 0 ? null : string.Join('\n', present);
    }

    private static string? BuildQuestion(string question)
        => string.IsNullOrWhiteSpace(question) ? null : "QUESTION: " + Clip(question.Trim(), AnswerOptions.QuestionTransportCodeUnitLimit);

    private static string? BuildPlanDescription(DirectoryQueryPlan? plan)
    {
        var description = plan?.Description;
        return string.IsNullOrWhiteSpace(description)
            ? null
            : "QUERY RUN: " + Clip(description.Trim(), AnswerOptions.MaxDescriptionChars);
    }

    private static string? BuildDistribution(DistributionSummary? distribution)
    {
        if (distribution is null)
        {
            return null;
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"DISTRIBUTION: {distribution.TotalRows} rows; {distribution.DistinctBuckets} distinct values; {distribution.SingletonBuckets} values occur exactly once; {distribution.BlankRows} rows blank.");
    }

    private static string? BuildHeadline(HeadlineResult headline, DirectoryQueryPlan? plan)
    {
        switch (headline.Kind)
        {
            case HeadlineKind.Count:
                return string.Create(CultureInfo.InvariantCulture, $"RESULT: count = {headline.Count ?? 0}.");

            case HeadlineKind.Record:
                return BuildRecord(headline.Record);

            case HeadlineKind.Grouped:
                return BuildGrouped(headline, plan);

            case HeadlineKind.None:
            default:
                return "RESULT: no matching records.";
        }
    }

    private static string BuildRecord(IReadOnlyDictionary<string, object?>? record)
    {
        if (record is null || record.Count == 0)
        {
            return "RESULT: one record, no fields.";
        }

        var fields = record
            .Take(AnswerOptions.MaxRecordFields)
            .Select(pair =>
                $"{Clip(pair.Key, AnswerOptions.MaxValueChars)}={Clip(pair.Value?.ToString() ?? string.Empty, AnswerOptions.MaxValueChars)}");

        return "RESULT: one record — " + string.Join("; ", fields) + ".";
    }

    private static string BuildGrouped(HeadlineResult headline, DirectoryQueryPlan? plan)
    {
        var groups = headline.Groups ?? [];
        var groupBy = plan?.Projection?.Aggregation?.GroupBy;
        var by = groupBy is { Count: > 0 } ? " by " + string.Join(", ", groupBy) : string.Empty;

        var builder = new StringBuilder();
        builder.Append(CultureInfo.InvariantCulture, $"RESULT: {headline.Count ?? 0} rows grouped{by}.");

        if (groups.Count > 0)
        {
            // The headline is already bounded to MaxHeadlineGroups; the Take is a second,
            // local statement of the same bound so this builder cannot be widened by a
            // change elsewhere.
            var top = groups
                .Take(AnswerOptions.MaxGroupBuckets)
                .Select(group => $"{Clip(group.Key, AnswerOptions.MaxValueChars)}: {group.Count}");

            builder.Append(" LARGEST VALUES: ").Append(string.Join(", ", top)).Append('.');
        }

        return builder.ToString();
    }

    /// <summary>
    /// Bounds a value's length and flattens it onto one line (slice2-or-3).
    /// <para>
    /// The reduction's only structure is <c>\n</c>-delimited prefixed sections, and directory
    /// values are interpolated into them verbatim — a record field, a group key. A value
    /// carrying its own newline (<c>description</c> and <c>info</c> both accept them) would
    /// forge a line in that format, including one that reads like a second RULES block, which
    /// Narrate cannot distinguish from the template's own. Every control character folds to a
    /// space so each value stays on the line its section allocated for it.
    /// </para>
    /// <para>
    /// This closes the delimiter forgery, not prompt injection in general: a single-line value
    /// can still read like an instruction. Defending that needs an explicit untrusted-content
    /// delimiter in the format and a rule in the template — recorded as a known gap.
    /// </para>
    /// </summary>
    private static string Clip(string value, int maxChars)
    {
        var clipped = value.Length <= maxChars ? value : value[..maxChars];

        return clipped.Any(char.IsControl)
            ? string.Create(
                clipped.Length,
                clipped,
                static (destination, source) =>
                {
                    for (var i = 0; i < source.Length; i++)
                    {
                        destination[i] = char.IsControl(source[i]) ? ' ' : source[i];
                    }
                })
            : clipped;
    }
}
