using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Security;

/// <summary>
/// Provides the resolved directory attribute and filter-operator allow-lists.
/// </summary>
public interface IDirectorySecurityPolicy
{
    bool HasAllowedAttributes(DirectoryObjectType objectType);

    bool IsAttributeAllowed(DirectoryObjectType objectType, string? attribute);

    bool IsFilterOperatorAllowed(string? operatorValue);
}
