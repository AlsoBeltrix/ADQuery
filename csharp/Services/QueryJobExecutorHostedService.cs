using System;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Security;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Background service that processes queued query jobs with controlled concurrency.
/// </summary>
public class QueryJobExecutorHostedService : BackgroundService
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly ILogger<QueryJobExecutorHostedService> _logger;
    private readonly SemaphoreSlim _concurrencySemaphore;
    private readonly TimeSpan _jobRetention;

    public QueryJobExecutorHostedService(
        IServiceScopeFactory serviceScopeFactory,
        ILogger<QueryJobExecutorHostedService> logger,
        IConfiguration configuration)
    {
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;
        var maxConcurrent = Math.Max(1, configuration.GetValue<int>("Jobs:MaxConcurrentJobs", 3));
        var retentionHours = Math.Max(1, configuration.GetValue<int>("Jobs:CompletedJobRetentionHours", 24));
        _jobRetention = TimeSpan.FromHours(retentionHours);
        _concurrencySemaphore = new SemaphoreSlim(maxConcurrent);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Query job executor started");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _serviceScopeFactory.CreateScope();
                var jobManager = scope.ServiceProvider.GetRequiredService<IQueryJobManager>();
                var jobQueue = scope.ServiceProvider.GetRequiredService<IQueryJobQueue>();

                // Process queue entries. We use the larger of store-queued count and channel depth
                // so stale queue items (e.g., cancelled before execution) still get drained.
                var queuedCount = jobManager.GetQueuedJobs().Count;
                var queueDepth = jobQueue.Count;
                var workItemsToProcess = Math.Max(queuedCount, queueDepth);

                for (var i = 0; i < workItemsToProcess; i++)
                {
                    if (stoppingToken.IsCancellationRequested) break;

                    // Dequeue job ID
                    var jobId = await jobQueue.DequeueAsync(stoppingToken);
                    if (jobId == null) continue;

                    // Acquire concurrency slot
                    await _concurrencySemaphore.WaitAsync(stoppingToken);

                    // Execute in background (don't await - fire and forget)
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            using var execScope = _serviceScopeFactory.CreateScope();
                            var execJobManager = execScope.ServiceProvider.GetRequiredService<IQueryJobManager>();
                            var claude = execScope.ServiceProvider.GetRequiredService<IClaudeService>();
                            var validator = execScope.ServiceProvider.GetRequiredService<IPlanValidator>();
                            var executor = execScope.ServiceProvider.GetRequiredService<IDirectoryPlanExecutor>();
                            var cache = execScope.ServiceProvider.GetRequiredService<IMemoryCache>();

                            await execJobManager.ExecuteJobWithServicesAsync(
                                jobId,
                                claude,
                                validator,
                                executor,
                                cache,
                                stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error executing job {JobId}", jobId);
                        }
                        finally
                        {
                            _concurrencySemaphore.Release();
                        }
                    }, stoppingToken);
                }

                // Cleanup old jobs based on retention setting
                jobManager.CleanupCompletedJobs(_jobRetention);

                // Poll interval - check for new jobs every second
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error in job executor loop");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }

        _logger.LogInformation("Query job executor stopped");
    }
}
