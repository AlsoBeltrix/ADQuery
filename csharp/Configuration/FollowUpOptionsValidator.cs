using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Configuration;

/// <summary>
/// Startup validation for <see cref="FollowUpOptions"/> (F01 Slice C1).
///
/// Rejects zero, negative, and any byte cap above the transport's UTF-16 code-unit
/// guard (reconciliation: an in-bounds byte input must never be pre-empted by
/// binding-time <c>[StringLength]</c> rejection). Zero never restores unlimited context.
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

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }
}
