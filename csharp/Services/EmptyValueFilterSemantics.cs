using System;
using System.Linq;
using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// The single reading of a filter whose value is empty or whitespace (F04-D3).
///
/// A negation operator with no value means "the attribute is populated" — the model
/// writes <c>not_equals ""</c> for "has a manager", and the other three negations for
/// the same intent. Previously such a filter was rejected outright ("Projection filter
/// value is required"), which faulted the whole turn.
///
/// This type is the single owner of that reading, shared by the validator, both filter
/// normalizers, the in-memory record evaluator, and the LDAP clause builder — a filter
/// the validator accepts must be one every evaluator reads identically. Splitting the
/// reading is how the pre-existing bug arose: <c>MatchesBaseOperator</c> short-circuits
/// <c>contains</c>/<c>starts_with</c>/<c>ends_with</c> to <c>true</c> on an empty value,
/// so under negation those three matched *nothing* — the exact inverse of the intent
/// (f04-or-5). The populated-attribute predicate is therefore evaluated ahead of generic
/// operator dispatch rather than by patching each short-circuit.
/// </summary>
internal static class EmptyValueFilterSemantics
{
    /// <summary>The legacy attribute whose <c>equals ""</c> means "never expires".</summary>
    private const string AccountExpirationDate = "AccountExpirationDate";

    private const string Enabled = "Enabled";

    /// <summary>
    /// Attributes the search layer <em>synthesizes</em> onto every record it returns rather
    /// than reading verbatim from the directory: <c>ActiveDirectoryService.MapToRecord</c>
    /// derives <c>Enabled</c> from <c>userAccountControl</c> and <c>AccountExpirationDate</c>
    /// from <c>accountExpires</c>, defaulting the latter to the literal "Never". Both
    /// therefore always carry a value, so "is this attribute populated" is unconditionally
    /// true for them — see <see cref="IsAlwaysPopulatedAttribute"/>.
    /// </summary>
    private static readonly string[] SynthesizedAttributes = [Enabled, AccountExpirationDate];

    public static bool IsNegationOperator(string? operatorValue)
        => operatorValue is not null &&
           operatorValue.Trim().StartsWith("not_", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when this filter is the populated-attribute form: a negation operator with no
    /// value to compare against.
    /// </summary>
    public static bool IsPopulatedAttributeFilter(string? attribute, string? operatorValue, string? value)
        => !string.IsNullOrWhiteSpace(attribute) &&
           string.IsNullOrWhiteSpace(value) &&
           IsNegationOperator(operatorValue);

    /// <summary>
    /// True for an attribute the search layer synthesizes onto every record, which is
    /// therefore populated on every record (slice5-or-1). Callers must answer the
    /// populated-attribute question for these with an unconditional yes: the special-cased
    /// clause builders would otherwise answer a different question entirely — the
    /// <c>Enabled</c> builder reads a blank value as "disabled" and the
    /// <c>AccountExpirationDate</c> builder reads it as "never expires", so
    /// <c>not_equals ""</c> would return only enabled, or only expired, accounts.
    /// </summary>
    public static bool IsAlwaysPopulatedAttribute(string? attribute)
        => attribute is not null &&
           SynthesizedAttributes.Contains(attribute.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a filter may carry an empty value at all — the gate the validator and both
    /// normalizers share. Positive operators are NOT widened; the one exception is the
    /// pre-existing <c>AccountExpirationDate equals ""</c> ("never expires"), which is a
    /// distinct concept from "populated" and is left exactly as it was.
    /// </summary>
    public static bool AllowsEmptyValue(string? attribute, string? operatorValue)
    {
        if (string.IsNullOrWhiteSpace(attribute))
        {
            return false;
        }

        if (IsNegationOperator(operatorValue))
        {
            return true;
        }

        return attribute.Trim().Equals(AccountExpirationDate, StringComparison.OrdinalIgnoreCase) &&
               (operatorValue?.Trim() ?? string.Empty).Equals("equals", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the record holds at least one non-null, non-whitespace value for the
    /// attribute. Whitespace-only counts as unpopulated, matching the validator's own
    /// <see cref="string.IsNullOrWhiteSpace(string)"/> reading of an empty filter value.
    /// </summary>
    public static bool HasPopulatedValue(DirectoryRecord record, string attribute)
    {
        if (record is null || string.IsNullOrWhiteSpace(attribute))
        {
            return false;
        }

        if (record.GetStrings(attribute).Any(value => !string.IsNullOrWhiteSpace(value)))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(record.GetString(attribute));
    }
}
