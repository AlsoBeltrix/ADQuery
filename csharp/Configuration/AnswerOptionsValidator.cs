using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Configuration;

/// <summary>
/// Startup validation for <see cref="AnswerOptions"/> (F04 Slice 2).
///
/// Rejects zero, negative, and any cap above the ceiling the builder's own component
/// maxima already impose: a larger number would be unreachable and would misrepresent
/// how much material Narrate can see. Zero never restores an unbounded reduction.
/// </summary>
public sealed class AnswerOptionsValidator : IValidateOptions<AnswerOptions>
{
    public ValidateOptionsResult Validate(string? name, AnswerOptions options)
    {
        var failures = new List<string>();

        if (options.MaxReductionBytes <= 0)
        {
            failures.Add(
                $"{AnswerOptions.SectionName}:{nameof(options.MaxReductionBytes)} must be greater than zero; zero never means unlimited.");
        }
        else if (options.MaxReductionBytes > AnswerOptions.ReductionCeilingBytes)
        {
            failures.Add(
                $"{AnswerOptions.SectionName}:{nameof(options.MaxReductionBytes)} ({options.MaxReductionBytes}) must not exceed the ceiling the builder's component maxima impose ({AnswerOptions.ReductionCeilingBytes}); raising it requires widening those maxima deliberately.");
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
