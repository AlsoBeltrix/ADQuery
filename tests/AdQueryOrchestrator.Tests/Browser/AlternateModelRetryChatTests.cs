using Microsoft.Playwright;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Browser;

/// <summary>
/// Guard for finding slice3-or-1: "Try again with another model" replaces the answer to the
/// question the last chat turn already asked, so the conversation must move to the replacement
/// job's answer. Before the fix the chat kept presenting the rejected primary answer while the
/// main window showed the alternate one, and a subsequent follow-up (which references
/// <c>lastCompletedJobId</c>, the alternate job) appeared to refine an answer the user could no
/// longer see.
///
/// Drives the real checked-in page in Chromium with <c>/api</c> stubbed (harness: Slice T1
/// <see cref="StaticSiteFixture"/>), so the real retry path
/// (<c>retryWithAlternateModel</c> → <c>reopenLastChatAnswerForRetry</c> → <c>startPolling</c> →
/// <c>displayJobResults</c> → <c>resolveChatAnswer</c>) executes end to end.
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class AlternateModelRetryChatTests
{
    private const string PrimaryAnswer = "Primary answer: 12 people match.";
    private const string AlternateAnswer = "Alternate answer: 12 people match, 3 of them disabled.";

    private readonly StaticSiteFixture _fixture;

    public AlternateModelRetryChatTests(StaticSiteFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task AlternateModelRetry_MovesTheChatToTheReplacementAnswer()
    {
        var page = await RunPrimaryTurnAsync();
        try
        {
            var bot = page.Locator("#chatLog .exchange .turn.bot");
            await Assertions.Expect(bot).ToHaveTextAsync(PrimaryAnswer);

            await RetryWithAlternateModelAsync(page);

            // Both surfaces move to the alternate job's answer; the rejected primary
            // answer is nowhere in the conversation.
            await Assertions.Expect(bot).ToHaveCountAsync(1);
            await Assertions.Expect(bot).ToHaveTextAsync(AlternateAnswer);
            await Assertions.Expect(page.Locator("#chatLog")).Not.ToContainTextAsync(PrimaryAnswer);
            await Assertions.Expect(page.Locator("#answer .prose")).ToHaveTextAsync(AlternateAnswer);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task AlternateModelRetry_ReusesTheAskedQuestionsTurn()
    {
        var page = await RunPrimaryTurnAsync();
        try
        {
            await RetryWithAlternateModelAsync(page);

            // The retry re-answers a question already asked, so it must not fabricate a
            // second exchange with no user turn to hang off.
            await Assertions.Expect(page.Locator("#chatLog .exchange")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#chatLog .exchange .turn.you"))
                .ToHaveTextAsync("who is in group X");
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// Opens the page with the async-query flow stubbed to two jobs — <c>primary-job</c>
    /// carrying <see cref="PrimaryAnswer"/> and <c>alternate-job</c> carrying
    /// <see cref="AlternateAnswer"/> — then submits one chat question and waits for the
    /// primary turn to settle.
    /// </summary>
    private async Task<IPage> RunPrimaryTurnAsync()
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
            Body = """{"jobId":"primary-job"}""",
        }));
        await page.RouteAsync("**/api/query/feedback", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = """{"success":true}""",
        }));
        await page.RouteAsync("**/api/query/retry-with-alternate-model", route => route.FulfillAsync(
            new RouteFulfillOptions
            {
                ContentType = "application/json",
                Body = """{"success":true,"job_id":"alternate-job"}""",
            }));

        // Broad status route first, then the more specific preview route, so preview
        // (later registration = higher Playwright priority) wins for its own URL.
        await page.RouteAsync("**/api/query/jobs/*", route =>
        {
            var url = route.Request.Url;
            var jobId = url[(url.LastIndexOf('/') + 1)..];
            var answer = jobId == "alternate-job" ? AlternateAnswer : PrimaryAnswer;
            return route.FulfillAsync(new RouteFulfillOptions
            {
                ContentType = "application/json",
                Body = $$$"""
                    {"status":"completed","jobId":"{{{jobId}}}","query":"who is in group X",
                    "result":{"totalRows":12,"headline":{"kind":"count","count":12},
                    "answer":"{{{answer}}}","warnings":[]}}
                    """,
            });
        });
        await page.RouteAsync("**/api/query/jobs/*/preview", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = """{"rows":[],"totalRows":12,"hasMore":false}""",
        }));

        await page.GotoAsync(_fixture.BaseAddress + "/");
        await page.FillAsync("#chatInput", "who is in group X");
        await page.ClickAsync("#chatSend");
        await Assertions.Expect(page.Locator("#chatLog .turn.bot.pending")).ToHaveCountAsync(0);
        return page;
    }

    /// <summary>
    /// Walks the real feedback affordance: 👎 reveals the retry button, which swaps the job.
    /// </summary>
    private static async Task RetryWithAlternateModelAsync(IPage page)
    {
        await page.ClickAsync(".btn-negative");
        await page.ClickAsync(".btn-retry");
        await Assertions.Expect(page.Locator("#chatLog .turn.bot.pending")).ToHaveCountAsync(0);
    }
}
