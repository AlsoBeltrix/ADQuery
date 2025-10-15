using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AdQuery.Orchestrator.Models;

/// <summary>
/// Represents a structured, C#-native plan for querying directory data.
/// </summary>
public class DirectoryQueryPlan
{
    [Required]
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("steps")]
    public List<DirectoryPlanStep> Steps { get; set; } = new();

    [JsonPropertyName("result_limit")]
    public int? ResultLimit { get; set; }

    [Required]
    [JsonPropertyName("projection")]
    public ProjectionDefinition Projection { get; set; } = new();
}

/// <summary>
/// Represents a single unit of work within a directory query plan.
/// </summary>
public class DirectoryPlanStep
{
    [JsonPropertyName("step")]
    public int Step { get; set; }

    [Required]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Operation to perform. Supported values: search, expand_members, lookup.
    /// </summary>
    [Required]
    [JsonPropertyName("operation")]
    public string Operation { get; set; } = string.Empty;

    [JsonPropertyName("target_type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DirectoryObjectType TargetType { get; set; }

    [JsonPropertyName("filters")]
    public List<DirectoryFilter> Filters { get; set; } = new();

    [JsonPropertyName("attributes")]
    public List<string> Attributes { get; set; } = new();

    [JsonPropertyName("size_limit")]
    public int? SizeLimit { get; set; }

    /// <summary>
    /// Name of the step whose results feed this step.
    /// </summary>
    [JsonPropertyName("source")]
    public string? Source { get; set; }

    /// <summary>
    /// Attribute on the source record containing the lookup value (distinguished name, manager, etc.).
    /// </summary>
    [JsonPropertyName("source_attribute")]
    public string? SourceAttribute { get; set; }

    /// <summary>
    /// Whether membership expansion should be recursive.
    /// </summary>
    [JsonPropertyName("recursive")]
    public bool Recursive { get; set; }
}

/// <summary>
/// Supported directory object types.
/// </summary>
public enum DirectoryObjectType
{
    User,
    Group,
    Computer,
    OrganizationalUnit
}

/// <summary>
/// Represents a simple attribute-based filter.
/// </summary>
public class DirectoryFilter
{
    [Required]
    [JsonPropertyName("attribute")]
    public string Attribute { get; set; } = string.Empty;

    /// <summary>
    /// Supported operators: equals, contains, starts_with, ends_with.
    /// </summary>
    [Required]
    [JsonPropertyName("operator")]
    public string Operator { get; set; } = string.Empty;

    [Required]
    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Defines how results should be projected into the final response.
/// </summary>
public class ProjectionDefinition
{
    [Required]
    [JsonPropertyName("row_step")]
    public string RowStep { get; set; } = string.Empty;

    [JsonPropertyName("columns")]
    public List<ProjectionColumn> Columns { get; set; } = new();

    /// <summary>
    /// Optional filter applied to the row step records before projection.
    /// </summary>
    [JsonPropertyName("filter")]
    public DirectoryFilter? Filter { get; set; }
}

/// <summary>
/// Maps attributes from one or more steps to the final response.
/// </summary>
public class ProjectionColumn
{
    [Required]
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Attribute to read from the selected record.
    /// </summary>
    [Required]
    [JsonPropertyName("attribute")]
    public string Attribute { get; set; } = string.Empty;

    /// <summary>
    /// Step providing the data. Defaults to the projection's row step.
    /// </summary>
    [JsonPropertyName("source_step")]
    public string? SourceStep { get; set; }

    /// <summary>
    /// Attribute name used to correlate records between steps (e.g., DistinguishedName).
    /// </summary>
    [JsonPropertyName("match_on")]
    public string? MatchOn { get; set; }

    /// <summary>
    /// Attribute on the row record that contains the value to match.
    /// </summary>
    [JsonPropertyName("match_value_from")]
    public string? MatchValueFrom { get; set; }

    /// <summary>
    /// Value to use when the attribute is missing.
    /// </summary>
    [JsonPropertyName("default")]
    public string? DefaultValue { get; set; }
}
