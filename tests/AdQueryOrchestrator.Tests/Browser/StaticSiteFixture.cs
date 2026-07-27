using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Playwright;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Browser;

/// <summary>
/// Slice T1 (TEST-D1) automated browser harness. Serves the real front-end
/// assets from <c>csharp/wwwroot</c> over a throwaway loopback Kestrel port and
/// owns a single shared Playwright Chromium instance for the browser tests.
/// <para>
/// The F01 headline/chat rendering is pure client-side JS over payloads the page
/// fetches from <c>/api/query/*</c>; those calls are stubbed per-test via
/// Playwright route interception (<see cref="BrowserPageFixture"/>), so the
/// harness needs only the static site — no live Active Directory, database, or
/// authentication. This static host deliberately does not run the application's
/// <c>Program</c>; it exists solely to serve the assets a real browser loads.
/// </para>
/// </summary>
public sealed class StaticSiteFixture : IAsyncLifetime
{
    private WebApplication? _app;
    private IPlaywright? _playwright;

    /// <summary>The base address the running static site is listening on.</summary>
    public string BaseAddress { get; private set; } = string.Empty;

    /// <summary>The shared Chromium instance for the browser tests.</summary>
    public IBrowser Browser { get; private set; } = null!;

    public async ValueTask InitializeAsync()
    {
        var webRoot = ResolveWebRoot();

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { WebRootPath = webRoot });
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");

        _app.UseDefaultFiles();
        _app.UseStaticFiles();

        await _app.StartAsync();
        BaseAddress = ResolveBaseAddress(_app);

        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    /// <summary>
    /// Locates <c>csharp/wwwroot</c> by walking up from the test assembly to the
    /// directory containing <c>ADQuery.sln</c>. Deterministic across the local and
    /// CI working directories so the harness serves the real, checked-in assets.
    /// </summary>
    private static string ResolveWebRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ADQuery.sln")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            throw new InvalidOperationException(
                "Could not locate the repository root (ADQuery.sln) above the test assembly.");
        }

        var webRoot = Path.Combine(directory.FullName, "csharp", "wwwroot");
        if (!Directory.Exists(webRoot))
        {
            throw new InvalidOperationException($"The front-end web root was not found: {webRoot}");
        }

        return webRoot;
    }

    private static string ResolveBaseAddress(WebApplication app)
    {
        // After binding to port 0 the concrete address lives in the server address
        // feature, not app.Urls (which still shows the requested :0).
        var feature = app.Services
            .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
            .Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>();
        var address = feature?.Addresses.FirstOrDefault()
            ?? throw new InvalidOperationException("The static site did not bind a listen address.");
        return address.TrimEnd('/');
    }
}
