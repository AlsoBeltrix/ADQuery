using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace AdQuery.Orchestrator.Models;

/// <summary>
/// Represents a single directory object returned from an LDAP query.
/// </summary>
public class DirectoryRecord
{
    [JsonPropertyName("object_type")]
    public DirectoryObjectType ObjectType { get; set; }

    [JsonPropertyName("distinguished_name")]
    public string DistinguishedName { get; set; } = string.Empty;

    /// <summary>
    /// Arbitrary attribute bag returned from the directory.
    /// </summary>
    [JsonPropertyName("attributes")]
    public Dictionary<string, object?> Attributes { get; } = new(StringComparer.OrdinalIgnoreCase);

    public object? this[string attribute]
    {
        get => Attributes.TryGetValue(attribute, out var value) ? value : null;
        set => Attributes[attribute] = value;
    }

    public string? GetString(string attribute)
    {
        if (!Attributes.TryGetValue(attribute, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string s => s,
            string[] array when array.Length > 0 => array[0],
            IEnumerable<string> enumerable => enumerable.FirstOrDefault(),
            _ => value.ToString()
        };
    }

    public IEnumerable<string> GetStrings(string attribute)
    {
        if (!Attributes.TryGetValue(attribute, out var value) || value is null)
        {
            return Enumerable.Empty<string>();
        }

        return value switch
        {
            string s => new[] { s },
            string[] array => array,
            IEnumerable<string> enumerable => enumerable,
            _ => new[] { value.ToString() ?? string.Empty }
        };
    }
}
