using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Channel-based implementation of job queue with unbounded capacity.
/// </summary>
public class InMemoryQueryJobQueue : IQueryJobQueue
{
    private readonly Channel<string> _channel;

    public InMemoryQueryJobQueue()
    {
        _channel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    }

    public ValueTask EnqueueAsync(string jobId, CancellationToken cancellationToken = default)
    {
        return _channel.Writer.WriteAsync(jobId, cancellationToken);
    }

    public async ValueTask<string?> DequeueAsync(CancellationToken cancellationToken = default)
    {
        if (await _channel.Reader.WaitToReadAsync(cancellationToken))
        {
            if (_channel.Reader.TryRead(out var jobId))
            {
                return jobId;
            }
        }

        return null;
    }

    public int Count => _channel.Reader.Count;
}
