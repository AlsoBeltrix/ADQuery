using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

using AdQuery.Orchestrator.Models;

namespace AdQuery.Orchestrator.Services;

internal static class QueryLogHelper
{
    public const string OutputRoot = @"E:\WWWOutput";

    private static readonly Regex InvalidPathChars = new Regex(@"[^\w\.-]", RegexOptions.Compiled);

    internal static string GetUserDirectory(string? samAccountName)
    {
        var accountSegment = SanitizePathSegment(string.IsNullOrWhiteSpace(samAccountName) ? "unknown" : samAccountName!);
        var directory = Path.Combine(OutputRoot, accountSegment);
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static string BuildFileBaseName(string? samAccountName, DateTime timestampUtc)
    {
        var accountSegment = SanitizePathSegment(string.IsNullOrWhiteSpace(samAccountName) ? "unknown" : samAccountName).ToUpperInvariant();
        return $"adquery_{accountSegment}_{timestampUtc:yyyyMMdd_HHmmssfff}";
    }

    internal static void WriteQueryLog(
        string logPath,
        DateTime timestampUtc,
        string requestId,
        string? samAccountName,
        string query,
        string? context,
        bool success,
        int recordCount,
        IEnumerable<string>? warnings,
        string? errorMessage,
        int? resultLimit,
        string? outputPath,
        string? rawModelResponse = null,
        string? modelPlanJson = null,
        string? executedPlanJson = null,
        string? modelUsed = null)
    {
        var builder = new StringBuilder();
        builder.AppendLine($"TimestampUtc: {timestampUtc:o}");
        builder.AppendLine($"RequestId: {requestId}");
        builder.AppendLine($"User: {samAccountName ?? "unknown"}");
        builder.AppendLine($"Model: {modelUsed ?? "unknown"}");
        builder.AppendLine($"Success: {success}");
        builder.AppendLine($"Records: {recordCount}");

        if (resultLimit.HasValue && resultLimit.Value > 0)
        {
            builder.AppendLine($"ResultLimit: {resultLimit.Value}");
        }

        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            builder.AppendLine($"OutputFile: {outputPath}");
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            builder.AppendLine($"Query: {EscapeForLog(query)}");
        }

        if (!string.IsNullOrWhiteSpace(context))
        {
            builder.AppendLine($"Context: {EscapeForLog(context)}");
        }

        if (warnings != null && warnings.Any())
        {
            builder.AppendLine($"Warnings: {EscapeForLog(string.Join(" | ", warnings))}");
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            builder.AppendLine($"Error: {EscapeForLog(errorMessage)}");
        }

        AppendMultilineSection(builder, "ModelResponseRaw", rawModelResponse);
        AppendMultilineSection(builder, "ModelPlanJson", modelPlanJson);
        AppendMultilineSection(builder, "ExecutedPlanJson", executedPlanJson);

        builder.AppendLine("DownloadHistory:");
        File.WriteAllText(logPath, builder.ToString());
    }

    internal static void AppendDownloadEvent(string logPath, string format)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        var entry = $"  - {DateTime.UtcNow:o} format={format.ToUpperInvariant()}{Environment.NewLine}";
        File.AppendAllText(logPath, entry);
    }

    internal static string? SerializePlan(DirectoryQueryPlan? plan)
    {
        if (plan is null)
        {
            return null;
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
            };
            return JsonSerializer.Serialize(plan, options);
        }
        catch (Exception ex)
        {
            return $"<serialization_error: {ex.Message}>";
        }
    }

    private static string SanitizePathSegment(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var sanitized = InvalidPathChars.Replace(value, "_");
        return string.IsNullOrWhiteSpace(sanitized) ? "unknown" : sanitized;
    }

    private static string EscapeForLog(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Replace("\r", " ").Replace("\n", " ").Replace("\t", " ");
    }

    private static void AppendMultilineSection(StringBuilder builder, string header, string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return;
        }

        builder.AppendLine($"{header}:");
        foreach (var line in content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            builder.AppendLine($"  {line}");
        }
    }
}
