using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace AdQuery.Orchestrator.Models;

/// <summary>
/// Request describing a directory search operation.
/// </summary>
public class DirectorySearchRequest
{
    [JsonPropertyName("target_type")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DirectoryObjectType TargetType { get; set; }

    [JsonPropertyName("filters")]
    public List<DirectoryFilter> Filters { get; set; } = new();

    [JsonPropertyName("attributes")]
    public List<string> Attributes { get; set; } = new();

    [JsonPropertyName("search_base")]
    public string? SearchBase { get; set; }

    [JsonPropertyName("scope")]
    public DirectorySearchScope Scope { get; set; } = DirectorySearchScope.Subtree;

    [JsonPropertyName("size_limit")]
    public int? SizeLimit { get; set; }
}

/// <summary>
/// Supported LDAP search scopes.
/// </summary>
public enum DirectorySearchScope
{
    Base,
    OneLevel,
    Subtree
}
