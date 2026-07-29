using Microsoft.Playwright;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Browser;

/// <summary>
/// F04 Slice 4 UI guard: the export pill row is a permanent, unobtrusive affordance on every
/// response that carries a meaningful exportable artifact, and is absent from every response
/// that does not (owner rule 2026-07-28). The browser never re-derives the rule — it obeys the
/// server's <c>result.exportable</c> flag, which is decided from plan shape.
///
/// Drives the real checked-in page in Chromium with the <c>/api</c> async-query flow stubbed
/// (harness: Slice T1 <see cref="StaticSiteFixture"/>), so the real <c>app.js</c>
/// <c>displayJobResults</c> → <c>showDownloadOptions</c> path executes end to end.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class ExportAffordanceRenderingTests
{
    private readonly StaticSiteFixture _fixture;

    public ExportAffordanceRenderingTests(StaticSiteFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ExportableArtifact_ShowsTheDownloadRow()
    {
        var page = await RunQueryAsync(exportableJson: "true");
        try
        {
            await Assertions.Expect(page.Locator("#downloadSection")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#downloadSection .op")).ToHaveCountAsync(4);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task ExportRow_IsSecondary_NeverThePrimaryAction()
    {
        var page = await RunQueryAsync(exportableJson: "true");
        try
        {
            // Unobtrusive: the row is styled secondary and no pill claims the accented
            // primary treatment (.op.key) that would put export ahead of the answer.
            await Assertions.Expect(page.Locator("#downloadSection")).ToHaveClassAsync("ops secondary");
            await Assertions.Expect(page.Locator("#downloadSection .op.key")).ToHaveCountAsync(0);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task NonExportableAnswer_HasNoDownloadRow()
    {
        // A one-line answer (a scalar count) or a single record: results render, export does not.
        var page = await RunQueryAsync(exportableJson: "false");
        try
        {
            await Assertions.Expect(page.Locator("#results")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#downloadSection")).ToBeHiddenAsync();
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task AbsentFlag_HasNoDownloadRow()
    {
        // Fail closed: a job whose DTO carries no flag at all must not offer a download the
        // server never sanctioned.
        var page = await RunQueryAsync(exportableJson: null);
        try
        {
            await Assertions.Expect(page.Locator("#results")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#downloadSection")).ToBeHiddenAsync();
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// Opens the real page with the async-query flow stubbed to one completed job carrying the
    /// given <c>exportable</c> JSON literal (or no such field when <paramref name="exportableJson"/>
    /// is null), then submits from the chat so the results panel renders.
    /// </summary>
    private async Task<IPage> RunQueryAsync(string? exportableJson)
    {
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

        var exportableField = exportableJson is null ? string.Empty : $"\"exportable\":{exportableJson},";
        await page.RouteAsync("**/api/query/jobs/test-job", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = $$$"""
                {"status":"completed","jobId":"test-job","query":"q","result":{"totalRows":42,
                "headline":{"kind":"count","count":42},{{{exportableField}}}
                "answer":"42 people match.","warnings":[]}}
                """,
        }));
        await page.RouteAsync("**/api/query/jobs/test-job/preview", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = """{"rows":[],"totalRows":42,"hasMore":false}""",
        }));

        await page.GotoAsync(_fixture.BaseAddress + "/");
        await page.FillAsync("#chatInput", "q");
        await page.ClickAsync("#chatSend");
        // The bubble carries .pending until the job settles it.
        await Assertions.Expect(page.Locator("#chatLog .turn.bot.pending")).ToHaveCountAsync(0);
        return page;
    }
}
