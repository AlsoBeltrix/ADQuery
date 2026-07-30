using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// The <see cref="IResultArtifactStore"/> of record: one JSON Lines file per completed job
/// (F04 Slice 7, F04-D5).
/// <para>
/// JSON Lines rather than one JSON document because three of the four readers want only the
/// first few rows — preview takes ten, the single-record headline takes one — and a
/// line-oriented file lets those stop reading after that many lines instead of materializing
/// a 40k-row set to answer a question about its head. The first line is a header carrying the
/// row count, warnings, and the serialized plan, so a bounded read still reports the real
/// total and whole-plan reuse can compare plans without reading rows at all.
/// </para>
/// </summary>
public sealed class JsonLinesResultArtifactStore : IResultArtifactStore
{
    /// <summary>Artifacts and their interrupted-write temp files, both swept at startup.</summary>
    public const string ArtifactExtension = ".results.jsonl";
    public const string TempExtension = ".results.tmp";

    /// <summary>
    /// Free space a query must find before it is accepted. A completed 40k-row result is a
    /// few tens of MB; this leaves room for several in flight plus the audit copies the
    /// download path writes alongside them.
    /// </summary>
    public const long DefaultMinimumFreeBytes = 1L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly ILogger<JsonLinesResultArtifactStore> _logger;
    private readonly string _root;
    private readonly long _minimumFreeBytes;

    public JsonLinesResultArtifactStore(
        ILogger<JsonLinesResultArtifactStore> logger,
        IConfiguration configuration)
    {
        _logger = logger;

        var configuredRoot = configuration["Results:ArtifactRoot"];
        _root = string.IsNullOrWhiteSpace(configuredRoot) ? QueryLogHelper.OutputRoot : configuredRoot;

        _minimumFreeBytes = Math.Max(
            0,
            configuration.GetValue("Results:MinimumFreeDiskBytes", DefaultMinimumFreeBytes));
    }

    public async Task<string> WriteAsync(
        QueryJob job,
        PlanExecutionResult result,
        CancellationToken cancellationToken = default)
    {
        var directory = QueryLogHelper.GetUserDirectory(_root, job.UserName);
        var baseName = QueryLogHelper.BuildFileBaseName(
            job.UserName,
            job.CreatedAt == default ? DateTime.UtcNow : job.CreatedAt);
        var stem = Path.Combine(directory, $"{baseName}_{job.JobId}");
        var tempPath = stem + TempExtension;
        var artifactPath = stem + ArtifactExtension;

        // Atomic: a reader never observes a partially written artifact, because the file at
        // the recorded path either does not exist yet or is complete.
        //
        // The writer owns its temp file on both paths (slice7-or-3): an interrupted write —
        // a cancelled query is the everyday case, since the row loop is where a large write
        // spends its time — must not leave a full-size partial behind. The startup sweep
        // still covers what no in-process handler can, a killed or crashed process.
        try
        {
            await using (var stream = new FileStream(
                tempPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 64 * 1024, useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                var header = new ArtifactHeader
                {
                    TotalRows = result.Data.Count,
                    Warnings = result.Warnings,
                    PlanJson = QueryLogHelper.SerializePlan(job.Plan),
                };
                await writer.WriteLineAsync(JsonSerializer.Serialize(header, SerializerOptions));

                for (var i = 0; i < result.Data.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    var line = new ArtifactRow
                    {
                        Row = result.Data[i],
                        GroupValues = i < result.GroupValues.Count ? result.GroupValues[i] : null,
                    };
                    await writer.WriteLineAsync(JsonSerializer.Serialize(line, SerializerOptions));
                }
            }

            File.Move(tempPath, artifactPath, overwrite: true);
        }
        catch
        {
            // Best-effort and silent: a cleanup failure must not replace the exception the
            // caller needs to see. Delete already swallows and logs its own IO failures.
            Delete(tempPath);
            throw;
        }

        _logger.LogInformation(
            "Job {JobId} result artifact written: {Rows} rows at {Path}",
            job.JobId, result.Data.Count, artifactPath);

        return artifactPath;
    }

    public ResultArtifact? Read(string? artifactPath, int? maxRows = null)
    {
        if (string.IsNullOrWhiteSpace(artifactPath) || !File.Exists(artifactPath))
        {
            return null;
        }

        try
        {
            using var reader = new StreamReader(artifactPath, Encoding.UTF8);

            var headerLine = reader.ReadLine();
            if (headerLine == null)
            {
                return null;
            }

            var header = JsonSerializer.Deserialize<ArtifactHeader>(headerLine, SerializerOptions);
            if (header == null)
            {
                return null;
            }

            var rows = new List<Dictionary<string, object?>>();
            var groupValues = new List<IReadOnlyList<string?>>();

            while (maxRows is null || rows.Count < maxRows.Value)
            {
                var line = reader.ReadLine();
                if (line == null)
                {
                    break;
                }

                var parsed = JsonSerializer.Deserialize<ArtifactRow>(line, SerializerOptions);
                if (parsed?.Row == null)
                {
                    continue;
                }

                rows.Add(parsed.Row);
                if (parsed.GroupValues != null)
                {
                    groupValues.Add(parsed.GroupValues);
                }
            }

            return new ResultArtifact
            {
                TotalRows = header.TotalRows,
                Rows = rows,
                GroupValues = groupValues,
                Warnings = header.Warnings ?? [],
                PlanJson = header.PlanJson,
            };
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // A missing or corrupt artifact reads as absent: callers already handle "results
            // not available" and must not fail a completed job over it.
            _logger.LogWarning(ex, "Result artifact {Path} could not be read", artifactPath);
            return null;
        }
    }

    public void Delete(string? artifactPath)
    {
        if (string.IsNullOrWhiteSpace(artifactPath))
        {
            return;
        }

        try
        {
            File.Delete(artifactPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Result artifact {Path} could not be deleted", artifactPath);
        }
    }

    /// <summary>
    /// Deletes every temp file (a temp file at startup is by definition an interrupted write)
    /// and every artifact not in <paramref name="livePaths"/>.
    /// <para>
    /// Job metadata is in-memory only, so after a restart no job is live and every artifact is
    /// an orphan — that is the intended outcome, not an accident of ordering. The sweep matches
    /// only this store's own two extensions, so the CSV and log audit trail written alongside
    /// them is never a candidate.
    /// </para>
    /// </summary>
    public int SweepOrphans(IReadOnlySet<string> livePaths)
    {
        if (!Directory.Exists(_root))
        {
            return 0;
        }

        var removed = 0;

        try
        {
            foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                var isTemp = path.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase);
                var isArtifact = path.EndsWith(ArtifactExtension, StringComparison.OrdinalIgnoreCase);

                if (!isTemp && !isArtifact)
                {
                    continue;
                }

                if (isArtifact && livePaths.Contains(path))
                {
                    continue;
                }

                Delete(path);
                removed++;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Result artifact sweep of {Root} did not complete", _root);
        }

        if (removed > 0)
        {
            _logger.LogInformation("Swept {Count} orphaned result artifacts from {Root}", removed, _root);
        }

        return removed;
    }

    public bool HasRoomForAnotherResult()
    {
        if (_minimumFreeBytes <= 0)
        {
            return true;
        }

        try
        {
            var pathRoot = Path.GetPathRoot(Path.GetFullPath(_root));
            if (string.IsNullOrWhiteSpace(pathRoot))
            {
                return true;
            }

            return new DriveInfo(pathRoot).AvailableFreeSpace >= _minimumFreeBytes;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // An unprobeable volume must not become a blanket refusal: the write itself still
            // fails loudly if the disk is genuinely full.
            _logger.LogWarning(ex, "Free-space check for {Root} failed; admitting the query", _root);
            return true;
        }
    }

    private sealed class ArtifactHeader
    {
        public int TotalRows { get; set; }
        public List<string>? Warnings { get; set; }
        public string? PlanJson { get; set; }
    }

    private sealed class ArtifactRow
    {
        public Dictionary<string, object?>? Row { get; set; }
        public IReadOnlyList<string?>? GroupValues { get; set; }
    }
}
