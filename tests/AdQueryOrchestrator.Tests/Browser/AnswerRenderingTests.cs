using Microsoft.Playwright;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Browser;

/// <summary>
/// F04 Slice 3 guard: the model-authored answer (the Slice 2 Narrate string, carried on the
/// async status DTO as <c>result.answer</c>) is what the user reads — leading the main window
/// above the F01 headline block, and standing in the chat bubble in place of the code-templated
/// "See the result panel" summary. When a job carries no answer (Narrate failed, was skipped, or
/// the job predates F04) both surfaces fall back to the F01 headline exactly as before.
///
/// Drives the real checked-in page in Chromium with the <c>/api</c> async-query flow stubbed
/// (harness: Slice T1 <see cref="StaticSiteFixture"/>), so the real <c>app.js</c> path
/// (<c>displayJobResults</c> → <c>renderAnswer</c>, <c>resolveChatAnswer</c> →
/// <c>summariseJobForChat</c>) executes end to end.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class AnswerRenderingTests
{
    private const string Answer =
        "26,612 of the ~27,000 extensionAttribute1 values occur exactly once, so there is no "
        + "meaningful most-common value.";

    private readonly StaticSiteFixture _fixture;

    public AnswerRenderingTests(StaticSiteFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task ModelAnswer_LeadsTheMainWindowAboveTheHeadline()
    {
        var page = await RunQueryAsync(answerJson: $"\"{Answer}\"");
        try
        {
            var answer = page.Locator("#answer");
            await Assertions.Expect(answer).ToBeVisibleAsync();
            await Assertions.Expect(answer.Locator(".prose")).ToHaveTextAsync(Answer);

            // Answer-first: the answer block precedes the F01 headline in document order,
            // and the headline still renders beneath as the authoritative detail.
            await Assertions.Expect(page.Locator("#results > *:first-child")).ToHaveIdAsync("answer");
            await Assertions.Expect(page.Locator("#headline")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#headline .v")).ToHaveTextAsync("42");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task ModelAnswer_IsWhatTheChatBubbleSays()
    {
        var page = await RunQueryAsync(answerJson: $"\"{Answer}\"");
        try
        {
            var bot = page.Locator("#chatLog .exchange .turn.bot");
            await Assertions.Expect(bot).ToHaveCountAsync(1);
            await Assertions.Expect(bot).ToHaveTextAsync(Answer);

            // Not the F01 code template it replaces.
            await Assertions.Expect(bot).Not.ToContainTextAsync("See the result panel");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task NoAnswer_FallsBackToTheHeadline()
    {
        // Narrate failure / older job: the field is absent from the DTO entirely.
        var page = await RunQueryAsync(answerJson: null);
        try
        {
            await Assertions.Expect(page.Locator("#results")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#answer")).ToBeHiddenAsync();

            // The F01 headline leads the panel and the chat keeps its template.
            await Assertions.Expect(page.Locator("#headline .v")).ToHaveTextAsync("42");
            await Assertions.Expect(page.Locator("#chatLog .exchange .turn.bot"))
                .ToHaveTextAsync("42 matches. See the result panel.");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task BlankAnswer_IsTreatedAsAbsent()
    {
        // A whitespace-only answer must not render an empty lead block or blank the chat
        // bubble; it is the same "no answer" case as an absent field.
        var page = await RunQueryAsync(answerJson: "\"   \"");
        try
        {
            await Assertions.Expect(page.Locator("#answer")).ToBeHiddenAsync();
            await Assertions.Expect(page.Locator("#chatLog .exchange .turn.bot"))
                .ToHaveTextAsync("42 matches. See the result panel.");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// Opens the real page with the async-query flow stubbed to one completed job whose
    /// headline is a count of 42, carrying the given answer JSON literal (or no answer field
    /// at all when <paramref name="answerJson"/> is null), then submits from the chat so both
    /// surfaces render.
    /// </summary>
    private async Task<IPage> RunQueryAsync(string? answerJson)
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

        var answerField = answerJson is null ? string.Empty : $"\"answer\":{answerJson},";
        await page.RouteAsync("**/api/query/jobs/test-job", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = $$$"""
                {"status":"completed","jobId":"test-job","query":"q","result":{"totalRows":42,
                "headline":{"kind":"count","count":42},{{{answerField}}}"warnings":[]}}
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
