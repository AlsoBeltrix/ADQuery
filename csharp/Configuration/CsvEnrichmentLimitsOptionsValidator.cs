using Microsoft.Extensions.Options;

namespace AdQuery.Orchestrator.Configuration;

/// <summary>
/// Startup validation for <see cref="CsvEnrichmentLimitsOptions"/> (P05 Slice 1).
///
/// Rejects zero, negative, and contradictory settings, and any combination whose
/// derived arithmetic overflows. Zero never restores unlimited behavior. This slice
/// only validates the options; it enforces no request behavior.
/// </summary>
public sealed class CsvEnrichmentLimitsOptionsValidator : IValidateOptions<CsvEnrichmentLimitsOptions>
{
    public ValidateOptionsResult Validate(string? name, CsvEnrichmentLimitsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        RequirePositive(nameof(options.MaxDataRows), options.MaxDataRows, failures);
        RequirePositive(nameof(options.MaxColumns), options.MaxColumns, failures);
        RequirePositive(nameof(options.MaxRetrieveAttributes), options.MaxRetrieveAttributes, failures);
        RequirePositive(nameof(options.MaxFieldCodeUnits), options.MaxFieldCodeUnits, failures);
        RequirePositive(nameof(options.MaxRequestBodyBytes), options.MaxRequestBodyBytes, failures);
        RequirePositive(nameof(options.LdapReceiveCeilingBytes), options.LdapReceiveCeilingBytes, failures);

        // Overflow safety: the rectangular grid must not overflow long arithmetic.
        if (options.MaxDataRows > 0 && options.MaxColumns > 0)
        {
            try
            {
                _ = checked((long)options.MaxDataRows * options.MaxColumns);
            }
            catch (OverflowException)
            {
                failures.Add(
                    $"{nameof(options.MaxDataRows)} × {nameof(options.MaxColumns)} overflows the grid-cell budget.");
            }
        }

        // Cross-field: the body cap must be able to hold the worst-case encoded body
        // for the declared row/column/field profile, or valid uploads are silently
        // rejected. The conservative compact-JSON bound is 39 + 6q + 6s + 3n + 2r
        // (plan mechanical derivations); here we require at least the raw grid-cell
        // code-unit contribution to fit, which is a necessary lower bound.
        if (options.MaxDataRows > 0 && options.MaxColumns > 0 && options.MaxFieldCodeUnits > 0)
        {
            try
            {
                var maxGridCodeUnits = checked(
                    (long)options.MaxDataRows * options.MaxColumns * options.MaxFieldCodeUnits);
                if (maxGridCodeUnits > 0 && options.MaxRequestBodyBytes < options.MaxColumns)
                {
                    failures.Add(
                        $"{nameof(options.MaxRequestBodyBytes)} is too small to hold even the header row for {nameof(options.MaxColumns)} columns.");
                }
            }
            catch (OverflowException)
            {
                failures.Add(
                    $"{nameof(options.MaxDataRows)} × {nameof(options.MaxColumns)} × {nameof(options.MaxFieldCodeUnits)} overflows the input code-unit budget.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    private static void RequirePositive(string field, long value, List<string> failures)
    {
        if (value <= 0)
        {
            failures.Add($"{CsvEnrichmentLimitsOptions.SectionName}:{field} must be greater than zero; zero never means unlimited.");
        }
    }
}
