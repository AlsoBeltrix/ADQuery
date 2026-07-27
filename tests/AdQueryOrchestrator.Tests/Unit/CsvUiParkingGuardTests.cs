using System.Reflection;
using AdQuery.Orchestrator.Controllers;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// Guards F01 Slice A: the CSV enrichment feature is parked in the UI only.
/// The browser-facing surfaces (mode toggle, upload form, file handlers,
/// enrich fetch) must be absent, while the server endpoint stays mapped so the
/// parked feature remains callable and its P04/P05 hardening is untouched.
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

    // --- Part 2: the server CSV endpoint stays mapped (feature parked, not removed). ---

    [Fact]
    public void CsvEnrichEndpoint_IsStillMappedOnController()
    {
        var method = typeof(QueryController).GetMethod(
            nameof(QueryController.CsvEnrich),
            BindingFlags.Public | BindingFlags.Instance);

        Assert.NotNull(method);

        var httpPost = method!.GetCustomAttribute<HttpPostAttribute>();
        Assert.NotNull(httpPost);
        Assert.Equal("csv-enrich", httpPost!.Template);
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
