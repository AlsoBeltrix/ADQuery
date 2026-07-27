using Microsoft.Playwright;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Browser;

/// <summary>
/// Slice B2 (F01) guard: proves the main-window headline block renders each B1
/// headline kind correctly and that the design-contract palette applies per
/// <c>html[data-theme]</c> in both themes. It drives the real checked-in page in
/// Chromium and stubs the <c>/api</c> async-query flow (execute-async → poll
/// status → preview) via Playwright route interception, so the real
/// <c>app.js</c> render path (<c>displayJobResults</c> → <c>renderHeadline</c>)
/// executes end to end. The harness itself is Slice T1
/// (<see cref="StaticSiteFixture"/>).
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class HeadlineRenderingTests
{
    private readonly StaticSiteFixture _fixture;

    public HeadlineRenderingTests(StaticSiteFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Count_RendersValueHero()
    {
        var page = await RunQueryWithHeadlineAsync(
            headlineJson: """{"kind":"count","count":42}""",
            totalRows: 42,
            previewRowsJson: "[]");
        try
        {
            var headline = page.Locator("#headline");
            await Assertions.Expect(headline).ToBeVisibleAsync();

            var value = headline.Locator(".headline-value");
            await Assertions.Expect(value).ToBeVisibleAsync();
            await Assertions.Expect(value).ToHaveTextAsync("42");

            // The value hero, not a record grid or grouped list.
            await Assertions.Expect(headline.Locator(".kv")).ToHaveCountAsync(0);
            await Assertions.Expect(headline.Locator(".headline-groups")).ToHaveCountAsync(0);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task Record_RendersNameAndKvGrid()
    {
        var page = await RunQueryWithHeadlineAsync(
            headlineJson: """{"kind":"record","record":{"displayName":"Jane Doe","department":"IT","title":"Engineer"}}""",
            totalRows: 1,
            previewRowsJson: """[{"displayName":"Jane Doe","department":"IT","title":"Engineer"}]""");
        try
        {
            var headline = page.Locator("#headline");
            await Assertions.Expect(headline).ToBeVisibleAsync();

            await Assertions.Expect(headline.Locator(".headline-name")).ToHaveTextAsync("Jane Doe");

            // The name is not repeated as a kv row; the other two fields are.
            var keys = headline.Locator(".kv dt");
            await Assertions.Expect(keys).ToHaveCountAsync(2);
            var values = headline.Locator(".kv dd");
            await Assertions.Expect(values).ToContainTextAsync(new[] { "IT", "Engineer" });
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task Grouped_RendersBoundedList()
    {
        var page = await RunQueryWithHeadlineAsync(
            headlineJson: """{"kind":"grouped","count":30,"groups":[{"key":"IT","count":12},{"key":"HR","count":10},{"key":"Sales","count":8}]}""",
            totalRows: 30,
            previewRowsJson: "[]");
        try
        {
            var headline = page.Locator("#headline");
            await Assertions.Expect(headline).ToBeVisibleAsync();

            var items = headline.Locator(".headline-groups li");
            await Assertions.Expect(items).ToHaveCountAsync(3);
            await Assertions.Expect(headline.Locator(".headline-groups .group-key"))
                .ToContainTextAsync(new[] { "IT", "HR", "Sales" });
            await Assertions.Expect(headline.Locator(".headline-groups .group-count"))
                .ToContainTextAsync(new[] { "12", "10", "8" });
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task None_LeavesHeadlineHidden()
    {
        var page = await RunQueryWithHeadlineAsync(
            headlineJson: """{"kind":"none"}""",
            totalRows: 0,
            previewRowsJson: "[]");
        try
        {
            // The results panel appears, but the headline block does not.
            await Assertions.Expect(page.Locator("#results")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#headline")).ToBeHiddenAsync();
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task ThemeToggle_AppliesContractPaletteInBothThemes()
    {
        var page = await RunQueryWithHeadlineAsync(
            headlineJson: """{"kind":"count","count":7}""",
            totalRows: 7,
            previewRowsJson: "[]");
        try
        {
            await Assertions.Expect(page.Locator("#headline")).ToBeVisibleAsync();

            // Dark is the default; contract --bg is #000000. ToHaveCSSAsync
            // retries, so it settles past the 0.25s background transition.
            await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "dark");
            await Assertions.Expect(page.Locator("body")).ToHaveCSSAsync("background-color", "rgb(0, 0, 0)");

            // Toggle to light; contract --bg is #d4d2ca. Proves the palette is
            // driven by html[data-theme], not just an attribute flip.
            await page.ClickAsync("#themeToggle");
            await Assertions.Expect(page.Locator("html")).ToHaveAttributeAsync("data-theme", "light");
            await Assertions.Expect(page.Locator("body")).ToHaveCSSAsync("background-color", "rgb(212, 210, 202)");

            // The headline survives the theme change.
            await Assertions.Expect(page.Locator("#headline .headline-value")).ToHaveTextAsync("7");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// Opens the real page with the <c>/api</c> async-query flow stubbed to a
    /// single completed job carrying the given headline, then submits the form so
    /// the real client render path runs.
    /// </summary>
    private async Task<IPage> RunQueryWithHeadlineAsync(
        string headlineJson, int totalRows, string previewRowsJson)
    {
        // Pin the emulated OS preference to dark so initTheme resolves to the
        // contract default deterministically (app.js honours prefers-color-scheme
        // when no stored preference exists).
        var page = await _fixture.Browser.NewPageAsync(new BrowserNewPageOptions
        {
            ColorScheme = ColorScheme.Dark,
        });

        await page.RouteAsync("**/api/user/info", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = """{"isAuthenticated":true,"username":"tester"}""",
        }));
        await page.RouteAsync("**/api/query/config", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = """{"summaryRowCount":20}""",
        }));
        await page.RouteAsync("**/api/query/execute-async", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = """{"jobId":"test-job"}""",
        }));
        await page.RouteAsync("**/api/query/jobs/test-job/preview", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = $$$"""{"rows":{{{previewRowsJson}}},"totalRows":{{{totalRows}}},"hasMore":false}""",
        }));
        await page.RouteAsync("**/api/query/jobs/test-job", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = $$$"""{"status":"completed","jobId":"test-job","query":"q","result":{"totalRows":{{{totalRows}}},"headline":{{{headlineJson}}},"warnings":[]}}""",
        }));

        await page.GotoAsync(_fixture.BaseAddress + "/");
        await page.FillAsync("#queryText", "q");
        await page.ClickAsync("#searchBtn");
        return page;
    }
}
