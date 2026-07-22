namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Writes a completed CSV enrichment result and returns its output path.
/// Failed enrichments must never call this boundary.
/// </summary>
public interface ICsvEnrichmentResultWriter
{
    string Write(string? ownerName, DateTime timestampUtc, byte[] content);
}

internal sealed class FileSystemCsvEnrichmentResultWriter : ICsvEnrichmentResultWriter
{
    public string Write(string? ownerName, DateTime timestampUtc, byte[] content)
    {
        var userDirectory = QueryLogHelper.GetUserDirectory(ownerName);
        var baseFileName = QueryLogHelper.BuildFileBaseName(ownerName, timestampUtc);
        var outputPath = Path.Combine(userDirectory, $"{baseFileName}_csv.csv");
        File.WriteAllBytes(outputPath, content);
        return outputPath;
    }
}

/// <summary>
/// Allocates the current download identifier only after enrichment succeeds.
/// </summary>
public interface ICsvEnrichmentResultIdGenerator
{
    string CreateId();
}

internal sealed class CsvEnrichmentResultIdGenerator : ICsvEnrichmentResultIdGenerator
{
    public string CreateId()
    {
        return Guid.NewGuid().ToString();
    }
}
