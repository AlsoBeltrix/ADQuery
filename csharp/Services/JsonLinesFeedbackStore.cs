using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Stores feedback in JSONL (JSON Lines) format for offline analysis.
/// Thread-safe append-only implementation.
/// </summary>
public class JsonLinesFeedbackStore : IFeedbackStore
{
    private readonly string _metricsDirectory;
    private readonly ILogger<JsonLinesFeedbackStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    public JsonLinesFeedbackStore(ILogger<JsonLinesFeedbackStore> logger)
    {
        _logger = logger;

        // Store feedback outside static web root.
        // Uses a system-scoped folder under E:\WWWOutput for centralized operational logging.
        _metricsDirectory = Path.Combine(QueryLogHelper.OutputRoot, "_system", "feedback");

        // Ensure directory exists
        Directory.CreateDirectory(_metricsDirectory);
    }

    public async Task SaveFeedbackAsync(QueryFeedback feedback)
    {
        await _writeLock.WaitAsync();

        try
        {
            // Use monthly file rotation: feedback-2025-10.jsonl
            var fileName = $"feedback-{DateTime.UtcNow:yyyy-MM}.jsonl";
            var filePath = Path.Combine(_metricsDirectory, fileName);

            // Serialize to single-line JSON
            var json = JsonSerializer.Serialize(feedback, JsonOptions);

            // Append to file (thread-safe)
            await File.AppendAllTextAsync(filePath, json + "\n");

            _logger.LogInformation(
                "Saved feedback for job {JobId}: {Sentiment} ({Comment})",
                feedback.JobId,
                feedback.Sentiment,
                string.IsNullOrWhiteSpace(feedback.Comment) ? "no comment" : "with comment"
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to save feedback for job {JobId}", feedback.JobId);
            throw;
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
