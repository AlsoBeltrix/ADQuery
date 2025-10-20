using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// In-memory implementation of job store using ConcurrentDictionary.
/// </summary>
public class InMemoryQueryJobStore : IQueryJobStore
{
    private readonly ConcurrentDictionary<string, QueryJob> _jobs = new(StringComparer.OrdinalIgnoreCase);

    public QueryJob? GetJob(string jobId)
    {
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    public void StoreJob(QueryJob job)
    {
        _jobs[job.JobId] = job;
    }

    public void UpdateProgress(string jobId, PlanProgressUpdate progress)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.NodesProcessed = progress.NodesProcessed;
            job.CurrentDepth = progress.CurrentDepth;
            job.EstimatedTotal = progress.EstimatedRemainingNodes ?? job.EstimatedTotal;
        }
    }

    public void UpdateStatus(string jobId, JobStatus status, string? errorMessage = null)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.Status = status;

            if (status == JobStatus.Running && job.StartedAt == null)
            {
                job.StartedAt = DateTime.UtcNow;
            }
            else if (status == JobStatus.Completed || status == JobStatus.Failed || status == JobStatus.Cancelled)
            {
                job.CompletedAt = DateTime.UtcNow;
            }

            if (!string.IsNullOrWhiteSpace(errorMessage))
            {
                job.ErrorMessage = errorMessage;
            }
        }
    }

    public void SetCompleted(string jobId, int totalRows, Dictionary<string, object>? aggregation, List<string> warnings, string resultsCacheKey)
    {
        if (_jobs.TryGetValue(jobId, out var job))
        {
            job.Status = JobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
            job.TotalRows = totalRows;
            job.Aggregation = aggregation;
            job.Warnings = warnings ?? new List<string>();
            job.ResultsCacheKey = resultsCacheKey;
        }
    }

    public List<QueryJob> GetUserJobs(string userName)
    {
        return _jobs.Values
            .Where(j => j.UserName.Equals(userName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(j => j.CreatedAt)
            .ToList();
    }

    public List<QueryJob> GetJobsByStatus(JobStatus status)
    {
        return _jobs.Values
            .Where(j => j.Status == status)
            .OrderBy(j => j.CreatedAt)
            .ToList();
    }

    public bool RemoveJob(string jobId)
    {
        return _jobs.TryRemove(jobId, out _);
    }
}
