using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Configuration;

/// <summary>
/// Startup validation for <see cref="FollowUpOptions"/> (F01 Slice C1, extended by F04
/// Slice 6a).
///
/// Rejects zero, negative, and any byte cap above the transport's UTF-16 code-unit
/// guard (reconciliation: an in-bounds byte input must never be pre-empted by
/// binding-time <c>[StringLength]</c> rejection). Zero never restores unlimited context.
///
/// It also enforces F04-D6's derived floor: the byte cap must clear the worst case the
/// configured <see cref="FollowUpOptions.MaxPriorQuestions"/> can compose, so the cap is a
/// backstop rather than the shaper. Without this, a legitimate maximum-length thread would
/// trip the cap mid-conversation instead of the pair failing at boot.
/// </summary>
public sealed class FollowUpOptionsValidator : IValidateOptions<FollowUpOptions>
{
    public ValidateOptionsResult Validate(string? name, FollowUpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        if (options.MaxContextBytes <= 0)
        {
            failures.Add(
                $"{FollowUpOptions.SectionName}:{nameof(options.MaxContextBytes)} must be greater than zero; zero never means unlimited.");
        }
        else if (options.MaxContextBytes > FollowUpOptions.ContextTransportCodeUnitLimit)
        {
            failures.Add(
                $"{FollowUpOptions.SectionName}:{nameof(options.MaxContextBytes)} ({options.MaxContextBytes}) must not exceed the transport code-unit guard ({FollowUpOptions.ContextTransportCodeUnitLimit}); a larger context needs a deliberate widening of the QueryRequest.Context [StringLength] attribute.");
        }

        if (options.MaxPriorQuestions < 0)
        {
            failures.Add(
                $"{FollowUpOptions.SectionName}:{nameof(options.MaxPriorQuestions)} must not be negative; zero means the turn carries its own question only.");
        }
        else if (options.MaxPriorQuestions >= FollowUpOptions.MaxThreadQuestions)
        {
            // MaxThreadQuestions is what the transport guard is derived from, so a knob at
            // or above it could compose a context the transport itself would reject.
            failures.Add(
                $"{FollowUpOptions.SectionName}:{nameof(options.MaxPriorQuestions)} ({options.MaxPriorQuestions}) must be below the thread ceiling the transport guard is derived from ({FollowUpOptions.MaxThreadQuestions}); the current turn's own question occupies the remaining slot.");
        }
        else if (options.MaxContextBytes > 0)
        {
            // F04-D6 derived floor. Checked only once the cap itself is a sane positive
            // number, so a zero cap reports the clearer failure above rather than both.
            var worstCase = FollowUpOptions.WorstCaseContextBytes(options.MaxPriorQuestions);
            if (options.MaxContextBytes < worstCase)
            {
                failures.Add(
                    $"{FollowUpOptions.SectionName}:{nameof(options.MaxContextBytes)} ({options.MaxContextBytes}) is below the worst case {options.MaxPriorQuestions} prior questions can compose ({worstCase} bytes); the byte cap is a backstop, not a shaper, so lower {nameof(options.MaxPriorQuestions)} or raise the cap.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
