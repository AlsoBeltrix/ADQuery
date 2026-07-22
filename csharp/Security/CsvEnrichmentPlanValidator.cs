using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Services;

namespace AdQuery.Orchestrator.Security;

public sealed class CsvEnrichmentPlanValidator : ICsvEnrichmentPlanValidator
{
    private const string DistinguishedName = "distinguishedName";
    private const string Enabled = "Enabled";
    private const string UserAccountControl = "userAccountControl";
    private const string AccountExpirationDate = "AccountExpirationDate";
    private const string AccountExpires = "accountExpires";

    private readonly IDirectorySecurityPolicy _securityPolicy;
    private readonly ICsvEnrichmentFilterEvaluator _filterEvaluator;

    public CsvEnrichmentPlanValidator(
        IDirectorySecurityPolicy securityPolicy,
        ICsvEnrichmentFilterEvaluator filterEvaluator)
    {
        _securityPolicy = securityPolicy;
        _filterEvaluator = filterEvaluator;
    }

    public CsvEnrichmentPlanValidationResult Validate(
        CsvEnrichmentPlan? plan,
        IReadOnlyList<string>? csvHeaders)
    {
        var errors = new List<string>();
        if (plan is null)
        {
            errors.Add("CSV enrichment plan is required.");
            return Invalid(errors);
        }

        var hasUserPolicy = _securityPolicy.HasAllowedAttributes(DirectoryObjectType.User);
        if (!hasUserPolicy)
        {
            errors.Add("No allow-listed user attributes are configured for CSV enrichment.");
        }

        var matchColumnIndex = ValidateMatchColumn(plan.MatchColumn, csvHeaders, errors);
        ValidateMatchAttribute(plan.MatchAttribute, hasUserPolicy, errors);
        var retrieveAttributes = ValidateRetrieveAttributes(
            plan.RetrieveAttributes,
            hasUserPolicy,
            errors);
        var filter = ValidateFilter(plan.Filter, hasUserPolicy, errors);
        var outputMode = ValidateOutputMode(plan.OutputMode, errors);

        var attributesToFetch = new List<string>(retrieveAttributes);
        if (filter is not null)
        {
            AddDistinct(attributesToFetch, filter.Attribute);
        }

        RequireInternalAttribute(
            DistinguishedName,
            "CSV enrichment requires the allow-listed internal attribute 'distinguishedName'.",
            hasUserPolicy,
            attributesToFetch,
            errors);
        AddBackingAttributes(plan.MatchAttribute, hasUserPolicy, attributesToFetch, errors, addToFetch: false);
        foreach (var attribute in attributesToFetch.ToArray())
        {
            AddBackingAttributes(attribute, hasUserPolicy, attributesToFetch, errors, addToFetch: true);
        }

        if (errors.Count > 0 ||
            matchColumnIndex < 0 ||
            outputMode is null ||
            (plan.Filter is not null && filter is null))
        {
            return Invalid(errors);
        }

        var executionPlan = new ValidatedCsvEnrichmentPlan(
            matchColumnIndex,
            plan.MatchAttribute,
            retrieveAttributes.AsReadOnly(),
            attributesToFetch.AsReadOnly(),
            filter,
            outputMode.Value);
        return new CsvEnrichmentPlanValidationResult(
            Array.Empty<string>(),
            executionPlan);
    }

    private static int ValidateMatchColumn(
        string? matchColumn,
        IReadOnlyList<string>? csvHeaders,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(matchColumn))
        {
            errors.Add("match_column is required.");
            return -1;
        }

        if (csvHeaders is not null)
        {
            for (var index = 0; index < csvHeaders.Count; index++)
            {
                if (string.Equals(csvHeaders[index], matchColumn, StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }
        }

        errors.Add("match_column must match a CSV header.");
        return -1;
    }

    private void ValidateMatchAttribute(
        string? matchAttribute,
        bool hasUserPolicy,
        List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(matchAttribute))
        {
            errors.Add("match_attribute is required.");
        }
        else if (hasUserPolicy &&
                 !_securityPolicy.IsAttributeAllowed(DirectoryObjectType.User, matchAttribute))
        {
            errors.Add("match_attribute is not allow-listed for User directory queries.");
        }
    }

    private List<string> ValidateRetrieveAttributes(
        IReadOnlyList<string>? retrieveAttributes,
        bool hasUserPolicy,
        List<string> errors)
    {
        var normalized = new List<string>();
        if (retrieveAttributes is null)
        {
            errors.Add("retrieve_attributes is required.");
            return normalized;
        }

        var hasEmpty = false;
        var hasDisallowed = false;
        foreach (var attribute in retrieveAttributes)
        {
            if (string.IsNullOrWhiteSpace(attribute))
            {
                hasEmpty = true;
                continue;
            }

            if (hasUserPolicy &&
                !_securityPolicy.IsAttributeAllowed(DirectoryObjectType.User, attribute))
            {
                hasDisallowed = true;
                continue;
            }

            AddDistinct(normalized, attribute);
        }

        if (hasEmpty)
        {
            errors.Add("retrieve_attributes contains an empty value.");
        }
        if (hasDisallowed)
        {
            errors.Add("retrieve_attributes contains an attribute not allow-listed for User directory queries.");
        }

        return normalized;
    }

    private ValidatedCsvEnrichmentFilter? ValidateFilter(
        CsvEnrichmentFilter? filter,
        bool hasUserPolicy,
        List<string> errors)
    {
        if (filter is null)
        {
            return null;
        }

        var valid = true;
        if (string.IsNullOrWhiteSpace(filter.Attribute))
        {
            errors.Add("filter.attribute is required.");
            valid = false;
        }
        else if (hasUserPolicy &&
                 !_securityPolicy.IsAttributeAllowed(DirectoryObjectType.User, filter.Attribute))
        {
            errors.Add("filter.attribute is not allow-listed for User directory queries.");
            valid = false;
        }

        var parsedOperator = default(CsvEnrichmentFilterOperator);
        if (string.IsNullOrWhiteSpace(filter.Operator))
        {
            errors.Add("filter.operator is required.");
            valid = false;
        }
        else
        {
            if (!_securityPolicy.IsFilterOperatorAllowed(filter.Operator))
            {
                errors.Add("filter.operator is not allowed by the directory security policy.");
                valid = false;
            }
            if (!_filterEvaluator.TryParseOperator(filter.Operator, out parsedOperator))
            {
                errors.Add("filter.operator is not supported for CSV enrichment.");
                valid = false;
            }
        }

        if (string.IsNullOrWhiteSpace(filter.Value))
        {
            errors.Add("filter.value is required.");
            valid = false;
        }

        return valid
            ? new ValidatedCsvEnrichmentFilter(
                filter.Attribute,
                parsedOperator,
                filter.Value)
            : null;
    }

    private static CsvEnrichmentOutputMode? ValidateOutputMode(
        string? outputMode,
        List<string> errors)
    {
        if (string.Equals(outputMode, "all", StringComparison.OrdinalIgnoreCase))
        {
            return CsvEnrichmentOutputMode.All;
        }
        if (string.Equals(outputMode, "filtered", StringComparison.OrdinalIgnoreCase))
        {
            return CsvEnrichmentOutputMode.Filtered;
        }

        errors.Add("output_mode must be 'all' or 'filtered'.");
        return null;
    }

    private void AddBackingAttributes(
        string? attribute,
        bool hasUserPolicy,
        List<string> attributesToFetch,
        List<string> errors,
        bool addToFetch)
    {
        if (string.Equals(attribute, Enabled, StringComparison.OrdinalIgnoreCase))
        {
            RequireInternalAttribute(
                UserAccountControl,
                "CSV enrichment requires the allow-listed backing attribute 'userAccountControl' for 'Enabled'.",
                hasUserPolicy,
                attributesToFetch,
                errors,
                addToFetch);
        }
        else if (string.Equals(attribute, AccountExpirationDate, StringComparison.OrdinalIgnoreCase))
        {
            RequireInternalAttribute(
                AccountExpires,
                "CSV enrichment requires the allow-listed backing attribute 'accountExpires' for 'AccountExpirationDate'.",
                hasUserPolicy,
                attributesToFetch,
                errors,
                addToFetch);
        }
    }

    private void RequireInternalAttribute(
        string attribute,
        string error,
        bool hasUserPolicy,
        List<string> attributesToFetch,
        List<string> errors,
        bool addToFetch = true)
    {
        if (hasUserPolicy &&
            !_securityPolicy.IsAttributeAllowed(DirectoryObjectType.User, attribute))
        {
            if (!errors.Contains(error, StringComparer.Ordinal))
            {
                errors.Add(error);
            }
            return;
        }

        if (addToFetch)
        {
            AddDistinct(attributesToFetch, attribute);
        }
    }

    private static void AddDistinct(List<string> attributes, string attribute)
    {
        if (!attributes.Contains(attribute, StringComparer.OrdinalIgnoreCase))
        {
            attributes.Add(attribute);
        }
    }

    private static CsvEnrichmentPlanValidationResult Invalid(List<string> errors)
    {
        return new CsvEnrichmentPlanValidationResult(
            Array.AsReadOnly(errors.ToArray()),
            executionPlan: null);
    }
}

internal sealed record ValidatedCsvEnrichmentPlan(
    int MatchColumnIndex,
    string MatchAttribute,
    IReadOnlyList<string> RetrieveAttributes,
    IReadOnlyList<string> AttributesToFetch,
    ValidatedCsvEnrichmentFilter? Filter,
    CsvEnrichmentOutputMode OutputMode);

internal sealed record ValidatedCsvEnrichmentFilter(
    string Attribute,
    CsvEnrichmentFilterOperator Operator,
    string Value);

internal enum CsvEnrichmentOutputMode
{
    All,
    Filtered
}
