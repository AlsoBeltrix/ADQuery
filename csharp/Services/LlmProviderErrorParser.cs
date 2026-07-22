using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AdQuery.Orchestrator.Services;

internal sealed record LlmProviderErrorDetails(
    string Message,
    string? Provider,
    string? Type,
    string? Code,
    string? CorrelationId)
{
    public string ToPublicDescription() => Message;
}

internal sealed record LlmProviderErrorBody(string Content, bool IsTruncated);

internal static class LlmProviderErrorParser
{
    internal const int MaxBodyBytes = 32 * 1024;
    internal const int MaxMessageCharacters = 512;
    internal const int MaxSensitiveInputCharacters = 128 * 1024;
    internal const int MaxRedactionCandidates = 256;
    private const int MaxMetadataCharacters = 128;
    private const int MaxSensitiveValues = 16;
    private const string GenericMessage = "Provider returned an error response.";

    private static readonly string[] CorrelationHeaderNames =
    [
        "x-portkey-trace-id",
        "request-id",
        "x-request-id",
        "x-amzn-requestid"
    ];

    public static async Task<LlmProviderErrorBody> ReadBoundedBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = ArrayPool<byte>.Shared.Rent(MaxBodyBytes + 1);

        try
        {
            var totalRead = 0;

            while (totalRead < MaxBodyBytes + 1)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(totalRead, MaxBodyBytes + 1 - totalRead),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                totalRead += read;
            }

            var bodyLength = Math.Min(totalRead, MaxBodyBytes);
            return new LlmProviderErrorBody(
                Encoding.UTF8.GetString(buffer, 0, bodyLength),
                IsTruncated: totalRead > MaxBodyBytes);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        }
    }

    public static string? GetCorrelationId(HttpResponseMessage response)
    {
        foreach (var headerName in CorrelationHeaderNames)
        {
            if (response.Headers.TryGetValues(headerName, out var values))
            {
                return values.FirstOrDefault();
            }
        }

        return null;
    }

    public static LlmProviderErrorDetails Parse(
        LlmProviderErrorBody body,
        string? responseCorrelationId,
        IEnumerable<string?> sensitiveValues)
    {
        var redactions = BuildRedactions(sensitiveValues);
        if (redactions is null)
        {
            return new LlmProviderErrorDetails(GenericMessage, null, null, null, null);
        }

        string? message = null;
        string? provider = null;
        string? type = null;
        string? code = null;
        string? bodyCorrelationId = null;

        if (!body.IsTruncated && !string.IsNullOrWhiteSpace(body.Content))
        {
            try
            {
                using var document = JsonDocument.Parse(
                    body.Content,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = false,
                        CommentHandling = JsonCommentHandling.Disallow,
                        MaxDepth = 16
                    });
                var root = document.RootElement;

                if (root.ValueKind == JsonValueKind.Object)
                {
                    provider = ReadScalar(root, "provider");
                    bodyCorrelationId =
                        ReadScalar(root, "request_id") ??
                        ReadScalar(root, "requestId");

                    if (TryGetProperty(root, "error", out var error))
                    {
                        if (error.ValueKind == JsonValueKind.String)
                        {
                            message = error.GetString();
                        }
                        else if (error.ValueKind == JsonValueKind.Object)
                        {
                            var details = error;

                            for (var depth = 0; depth < 4; depth++)
                            {
                                message = ReadScalar(details, "message") ?? message;
                                type =
                                    ReadScalar(details, "type") ??
                                    ReadScalar(details, "status") ??
                                    type;
                                code = ReadScalar(details, "code") ?? code;
                                provider ??= ReadScalar(details, "provider");
                                bodyCorrelationId ??=
                                    ReadScalar(details, "request_id") ??
                                    ReadScalar(details, "requestId");

                                if (!TryGetProperty(details, "error", out var nested) ||
                                    nested.ValueKind != JsonValueKind.Object)
                                {
                                    break;
                                }

                                details = nested;
                            }
                        }
                    }

                    message ??= ReadScalar(root, "message");
                    type ??= ReadScalar(root, "error_type");
                    code ??= ReadScalar(root, "error_code");
                }
            }
            catch (JsonException)
            {
                // Unknown response bodies use the fixed generic fallback.
            }
        }

        return new LlmProviderErrorDetails(
            Sanitize(message, MaxMessageCharacters, redactions) ?? GenericMessage,
            Sanitize(provider, MaxMetadataCharacters, redactions),
            Sanitize(type, MaxMetadataCharacters, redactions),
            Sanitize(code, MaxMetadataCharacters, redactions),
            Sanitize(
                responseCorrelationId ?? bodyCorrelationId,
                MaxMetadataCharacters,
                redactions));
    }

    private static string? ReadScalar(JsonElement element, string propertyName)
    {
        if (!TryGetProperty(element, propertyName, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => property.GetRawText(),
            _ => null
        };
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        out JsonElement property)
    {
        foreach (var candidate in element.EnumerateObject())
        {
            if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                property = candidate.Value;
                return true;
            }
        }

        property = default;
        return false;
    }

    private static IReadOnlyList<string>? BuildRedactions(IEnumerable<string?> sensitiveValues)
    {
        var materializedValues = new List<string>();
        var totalCharacters = 0;
        var examinedValues = 0;

        foreach (var sensitiveValue in sensitiveValues)
        {
            examinedValues++;
            if (examinedValues > MaxSensitiveValues)
            {
                return null;
            }

            if (string.IsNullOrEmpty(sensitiveValue))
            {
                continue;
            }

            if (sensitiveValue.Length > MaxSensitiveInputCharacters - totalCharacters)
            {
                return null;
            }

            materializedValues.Add(sensitiveValue);
            totalCharacters += sensitiveValue.Length;
        }

        var redactions = new HashSet<string>(StringComparer.Ordinal);

        foreach (var sensitiveValue in materializedValues)
        {
            if (!TryAddRedaction(redactions, sensitiveValue))
            {
                return null;
            }

            using var reader = new StringReader(sensitiveValue);
            while (reader.ReadLine() is { } line)
            {
                var candidate = line.Trim();
                if (candidate.Length >= 8 && !TryAddRedaction(redactions, candidate))
                {
                    return null;
                }
            }
        }

        return redactions
            .OrderByDescending(value => value.Length)
            .ToArray();
    }

    private static bool TryAddRedaction(HashSet<string> redactions, string value)
    {
        return !redactions.Add(value) || redactions.Count <= MaxRedactionCandidates;
    }

    private static string? Sanitize(
        string? value,
        int maxCharacters,
        IReadOnlyList<string> redactions)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        foreach (var redaction in redactions)
        {
            value = value.Replace(redaction, "[redacted]", StringComparison.Ordinal);
        }

        var sanitized = new StringBuilder(Math.Min(value.Length, maxCharacters));
        var previousWasWhitespace = false;

        foreach (var character in value)
        {
            if (char.IsControl(character) ||
                char.IsWhiteSpace(character) ||
                char.GetUnicodeCategory(character) == UnicodeCategory.Format)
            {
                if (!previousWasWhitespace && sanitized.Length > 0)
                {
                    sanitized.Append(' ');
                }

                previousWasWhitespace = true;
                continue;
            }

            sanitized.Append(character);
            previousWasWhitespace = false;
        }

        var result = sanitized.ToString().Trim();
        if (result.Length == 0)
        {
            return null;
        }

        return result.Length <= maxCharacters
            ? result
            : string.Concat(result.AsSpan(0, maxCharacters - 1), "…");
    }
}
