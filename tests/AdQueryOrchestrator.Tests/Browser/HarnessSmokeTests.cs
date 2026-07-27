using Microsoft.Playwright;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Browser;

/// <summary>
/// Slice T1 (TEST-D1) guard: proves the automated browser harness actually drives
/// the real front-end. It launches the static site, opens <c>/</c> in real
/// Chromium, and asserts the <c>#queryForm</c> bootstrap element is present — the
/// element <c>app.js</c> requires to bootstrap (<c>app.js:2</c> early-exits without
/// it). If the harness served the wrong web root or the page failed to load, the
/// element is absent and this test fails. The per-kind headline rendering
/// assertions build on this harness in Slice B2.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class HarnessSmokeTests
{
    private readonly StaticSiteFixture _fixture;

    public HarnessSmokeTests(StaticSiteFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task RealPage_LoadsInChromium_WithBootstrapElement()
    {
        var page = await _fixture.Browser.NewPageAsync();
        try
        {
            var response = await page.GotoAsync(_fixture.BaseAddress + "/");
            Assert.NotNull(response);
            Assert.True(response!.Ok, $"Navigating to the page returned HTTP {response.Status}.");

            // The bootstrap contract app.js depends on (app.js:2).
            var queryForm = page.Locator("#queryForm");
            await Assertions.Expect(queryForm).ToBeVisibleAsync();
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}
