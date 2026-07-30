using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Startup orphan sweep for result artifacts (F04 Slice 7, f04-or-7).
/// <para>
/// Two kinds of orphan exist. A <c>.results.tmp</c> file is an atomic write the process died
/// partway through, so it is never live. An artifact whose job no longer exists is unreachable:
/// job metadata is in-memory, so after a restart that is every artifact on disk. Both leak
/// disk permanently unless something removes them, and the 2h cache expiry D5 replaced is no
/// longer there to do it.
/// </para>
/// </summary>
public sealed class ResultArtifactSweeper : IHostedService
{
    private readonly IResultArtifactStore _artifacts;
    private readonly IQueryJobStore _jobs;
    private readonly ILogger<ResultArtifactSweeper> _logger;

    public ResultArtifactSweeper(
        IResultArtifactStore artifacts,
        IQueryJobStore jobs,
        ILogger<ResultArtifactSweeper> logger)
    {
        _artifacts = artifacts;
        _jobs = jobs;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            _artifacts.SweepOrphans(LivePaths());
        }
        catch (Exception ex)
        {
            // A failed sweep leaks disk; it must not stop the app from starting.
            _logger.LogError(ex, "Startup result-artifact sweep failed");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private IReadOnlySet<string> LivePaths() =>
        Enum.GetValues<JobStatus>()
            .SelectMany(_jobs.GetJobsByStatus)
            .Select(job => job.ResultArtifactPath)
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
