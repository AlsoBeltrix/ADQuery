using System.Reflection;
using AdQuery.Orchestrator.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// Guards CSV-KILL-D1: the CSV enrichment feature is removed entirely. The
/// browser-facing surfaces (mode toggle, upload form, file handlers, enrich
/// fetch) must be absent, and the server <c>csv-enrich</c> endpoint must no
/// longer be mapped on the controller.
/// </summary>
public sealed class CsvUiParkingGuardTests
{
    // --- Part 1: the CSV UI surfaces are gone from the shipped front end. ---

    [Fact]
    public void IndexHtml_HasNoCsvModeToggleOrUploadForm()
    {
        var html = ReadWebAsset("index.html");

        Assert.DoesNotContain("queryMode", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"csvForm\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"csvFile\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("id=\"csvStats\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("data-mode", html, StringComparison.Ordinal);
    }

    [Fact]
    public void AppJs_HasNoCsvEnrichHandlersOrFetch()
    {
        var js = ReadWebAsset(Path.Combine("js", "app.js"));

        Assert.DoesNotContain("csv-enrich", js, StringComparison.Ordinal);
        Assert.DoesNotContain("runCsvEnrichment", js, StringComparison.Ordinal);
        Assert.DoesNotContain("handleCsvFileSelect", js, StringComparison.Ordinal);
        Assert.DoesNotContain("handleModeChange", js, StringComparison.Ordinal);
    }

    // --- Part 2: the server CSV endpoint is removed from the controller. ---

    [Fact]
    public void CsvEnrichEndpoint_IsNotMappedOnController()
    {
        var templates = typeof(QueryController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(method => method.GetCustomAttribute<HttpPostAttribute>())
            .Where(attribute => attribute?.Template is not null)
            .Select(attribute => attribute!.Template!)
            .ToList();

        Assert.DoesNotContain("csv-enrich", templates);
    }

    private static string ReadWebAsset(string relativePath)
    {
        var repoRoot = FindRepositoryRoot();
        var full = Path.Combine(repoRoot, "csharp", "wwwroot", relativePath);
        Assert.True(File.Exists(full), $"Expected web asset at {full}");
        return File.ReadAllText(full);
    }

    private static string FindRepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ADQuery.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate repository root (ADQuery.sln) from " + AppContext.BaseDirectory);
    }
}
