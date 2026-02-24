using System.Text.Json.Serialization;

namespace AdQuery.Orchestrator.Models;

/// <summary>
/// Instructions from the LLM for how to enrich CSV data with AD lookups.
/// The LLM acts as operator, this tells the code what to do.
/// </summary>
public class CsvEnrichmentPlan
{
    /// <summary>
    /// The CSV column containing user identifiers to look up.
    /// </summary>
    [JsonPropertyName("match_column")]
    public string MatchColumn { get; set; } = string.Empty;

    /// <summary>
    /// The AD attribute to match against (sAMAccountName, userPrincipalName, mail, displayName, employeeID).
    /// </summary>
    [JsonPropertyName("match_attribute")]
    public string MatchAttribute { get; set; } = "sAMAccountName";

    /// <summary>
    /// AD attributes to retrieve for each matched user.
    /// </summary>
    [JsonPropertyName("retrieve_attributes")]
    public List<string> RetrieveAttributes { get; set; } = new();

    /// <summary>
    /// Optional filter to apply to results (e.g., only return contractors).
    /// </summary>
    [JsonPropertyName("filter")]
    public CsvEnrichmentFilter? Filter { get; set; }

    /// <summary>
    /// Output mode: "all" returns all CSV rows with AD data merged,
    /// "filtered" returns only rows matching the filter criteria.
    /// </summary>
    [JsonPropertyName("output_mode")]
    public string OutputMode { get; set; } = "all";

    /// <summary>
    /// Human-readable description of what this plan does.
    /// </summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Filter criteria for CSV enrichment results.
/// </summary>
public class CsvEnrichmentFilter
{
    [JsonPropertyName("attribute")]
    public string Attribute { get; set; } = string.Empty;

    [JsonPropertyName("operator")]
    public string Operator { get; set; } = "equals";

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}
