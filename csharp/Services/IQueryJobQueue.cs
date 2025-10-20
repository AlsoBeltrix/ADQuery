using System.Threading;
using System.Threading.Tasks;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// FIFO queue for query job work items with cancellation support.
/// </summary>
public interface IQueryJobQueue
{
    ValueTask EnqueueAsync(string jobId, CancellationToken cancellationToken = default);
    ValueTask<string?> DequeueAsync(CancellationToken cancellationToken = default);
    int Count { get; }
}
