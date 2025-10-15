using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Abstraction for querying Active Directory using managed code.
/// </summary>
public interface IActiveDirectoryService
{
    Task<IReadOnlyList<DirectoryRecord>> SearchAsync(DirectorySearchRequest request, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DirectoryRecord>> ExpandGroupMembersAsync(IEnumerable<string> groupDistinguishedNames, bool recursive, IEnumerable<string> attributes, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DirectoryRecord>> LookupAsync(IEnumerable<string> distinguishedNames, DirectoryObjectType targetType, IEnumerable<string> attributes, CancellationToken cancellationToken = default);
}
