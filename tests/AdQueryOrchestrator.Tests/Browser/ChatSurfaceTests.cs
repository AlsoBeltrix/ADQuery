using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Browser;

/// <summary>
/// Slice C3 (F01) guard for the floating chat surface. Drives the real checked-in
/// page in Chromium (harness: Slice T1 <see cref="StaticSiteFixture"/>) and proves the
/// three behaviours the plan's C3 contract calls out:
/// <list type="number">
///   <item>the resize handle clamps the panel to 50vw × 100vh (Design contract);</item>
///   <item>exchanges render with current/past delineation and a Q/A hairline;</item>
///   <item>the display-only exchange log is never transmitted — a follow-up sends only
///   <c>previousJobId</c> and no client-built context or transcript (FOLLOWUP-D2).</item>
/// </list>
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class ChatSurfaceTests
{
    private const int ViewportWidth = 1200;
    private const int ViewportHeight = 800;

    private readonly StaticSiteFixture _fixture;

    public ChatSurfaceTests(StaticSiteFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Resize_ClampsToHalfViewportWidthAndFullViewportHeight()
    {
        var page = await NewPageAsync();
        try
        {
            await StubFlowAndCaptureBodiesAsync(page);
            await page.GotoAsync(_fixture.BaseAddress + "/");
            await Assertions.Expect(page.Locator("#chat")).ToBeVisibleAsync();

            var handle = page.Locator("#chatResize");
            var box = await handle.BoundingBoxAsync();
            Assert.NotNull(box);

            // Grab the top-left handle and drag far past the top-left corner of the
            // viewport. The panel is anchored bottom-right, so this attempts to grow it
            // well beyond both caps; the clamp must hold it at 50vw × 100vh.
            await page.Mouse.MoveAsync(box!.X + box.Width / 2, box.Y + box.Height / 2);
            await page.Mouse.DownAsync();
            await page.Mouse.MoveAsync(-500, -500, new MouseMoveOptions { Steps = 10 });
            await page.Mouse.UpAsync();

            var width = await page.Locator("#chat").EvaluateAsync<double>(
                "el => el.getBoundingClientRect().width");
            var height = await page.Locator("#chat").EvaluateAsync<double>(
                "el => el.getBoundingClientRect().height");

            Assert.Equal(ViewportWidth * 0.5, width, 1.0);
            Assert.Equal(ViewportHeight, height, 1.0);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task Exchanges_RenderWithCurrentPastDelineationAndHairline()
    {
        var page = await NewPageAsync();
        try
        {
            await StubFlowAndCaptureBodiesAsync(page);
            await page.GotoAsync(_fixture.BaseAddress + "/");
            await Assertions.Expect(page.Locator("#chat")).ToBeVisibleAsync();

            await SubmitChatAsync(page, "who is in group X");
            await Assertions.Expect(page.Locator("#chatLog .exchange")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#chatLog .exchange.current")).ToHaveCountAsync(1);
            // Wait for the first turn to complete before following up: the surface
            // ignores a submit while a query is in flight (state.isLoading).
            await WaitForAnswerSettledAsync(page);

            await SubmitChatAsync(page, "and in Dublin?");
            await Assertions.Expect(page.Locator("#chatLog .exchange")).ToHaveCountAsync(2);

            // Delineation: exactly one current exchange (the latest); the earlier one
            // is demoted to the dimmed "past" state.
            await Assertions.Expect(page.Locator("#chatLog .exchange.current")).ToHaveCountAsync(1);
            await Assertions.Expect(page.Locator("#chatLog .exchange.past")).ToHaveCountAsync(1);

            // The latest question leads the current exchange; each exchange carries the
            // Q/A hairline and a left-aligned model answer bubble.
            var current = page.Locator("#chatLog .exchange.current");
            await Assertions.Expect(current.Locator(".turn.you")).ToHaveTextAsync("and in Dublin?");
            await Assertions.Expect(current.Locator(".qa-rule")).ToHaveCountAsync(1);
            await Assertions.Expect(current.Locator(".turn.bot")).ToHaveCountAsync(1);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task Placeholder_InvitesFirstQuestion_ThenSwitchesToFollowUp()
    {
        // The panel is the sole query input, so its opening prompt must invite a
        // first question — not "Ask a follow-up…", which only makes sense once a
        // prior answer exists. It switches to the follow-up prompt after the first
        // turn settles (same signal as the "refining last question" affordance).
        var page = await NewPageAsync();
        try
        {
            await StubFlowAndCaptureBodiesAsync(page);
            await page.GotoAsync(_fixture.BaseAddress + "/");

            var input = page.Locator("#chatInput");
            await Assertions.Expect(input).ToHaveAttributeAsync("placeholder", "Ask about the directory…");
            // The "refining last question" line stays hidden until a prior turn exists.
            await Assertions.Expect(page.Locator("#chat .refine")).ToHaveClassAsync(
                new System.Text.RegularExpressions.Regex(@"\bhidden\b"));

            await SubmitChatAsync(page, "who is in group X");
            await WaitForAnswerSettledAsync(page);

            await Assertions.Expect(input).ToHaveAttributeAsync("placeholder", "Ask a follow-up…");
            await Assertions.Expect(page.Locator("#chat .refine")).Not.ToHaveClassAsync(
                new System.Text.RegularExpressions.Regex(@"\bhidden\b"));
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    [Fact]
    public async Task DisplayHistory_IsNeverTransmitted()
    {
        var page = await NewPageAsync();
        try
        {
            var bodies = await StubFlowAndCaptureBodiesAsync(page);
            await page.GotoAsync(_fixture.BaseAddress + "/");
            await Assertions.Expect(page.Locator("#chat")).ToBeVisibleAsync();

            await SubmitChatAsync(page, "who is in group X");
            await Assertions.Expect(page.Locator("#chatLog .exchange")).ToHaveCountAsync(1);
            // Wait for the first turn to complete so it becomes the referenced prior
            // turn (previousJobId) before following up.
            await WaitForAnswerSettledAsync(page);

            await SubmitChatAsync(page, "and in Dublin?");
            await Assertions.Expect(page.Locator("#chatLog .exchange")).ToHaveCountAsync(2);
            await WaitForAnswerSettledAsync(page);

            Assert.Equal(2, bodies.Count);

            // First turn: no prior completed turn, so no previousJobId.
            var first = JsonDocument.Parse(bodies[0]).RootElement;
            Assert.Equal("who is in group X", first.GetProperty("query").GetString());
            Assert.False(first.TryGetProperty("previousJobId", out _));

            // Follow-up turn: references the completed first job only. The display-only
            // exchange log (the prior question/answer) is NOT transmitted — no context,
            // no transcript, and the outgoing query is just the new question.
            var second = JsonDocument.Parse(bodies[1]).RootElement;
            Assert.Equal("and in Dublin?", second.GetProperty("query").GetString());
            Assert.True(second.TryGetProperty("previousJobId", out var prev));
            Assert.Equal("job-1", prev.GetString());
            Assert.False(second.TryGetProperty("context", out _));

            // The prior turn's material never appears in any outgoing body.
            Assert.DoesNotContain("who is in group X", bodies[1]);
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    private async Task<IPage> NewPageAsync()
    {
        var page = await _fixture.Browser.NewPageAsync(new BrowserNewPageOptions
        {
            ColorScheme = ColorScheme.Dark,
            ViewportSize = new ViewportSize { Width = ViewportWidth, Height = ViewportHeight },
        });
        return page;
    }

    private static async Task SubmitChatAsync(IPage page, string query)
    {
        await page.FillAsync("#chatInput", query);
        await page.ClickAsync("#chatSend");
    }

    // The answer bubble carries .pending until the job result settles it. The chat
    // ignores a submit while a query is in flight, so follow-ups wait on this.
    private static async Task WaitForAnswerSettledAsync(IPage page)
    {
        await Assertions.Expect(page.Locator("#chatLog .turn.bot.pending")).ToHaveCountAsync(0);
    }

    /// <summary>
    /// Stubs the async-query flow so each submitted query completes, assigning a fresh job
    /// id per submission, and records every <c>execute-async</c> request body. Mirrors the
    /// Slice C2 transmission harness so the two client guards stub identically.
    /// </summary>
    private static async Task<List<string>> StubFlowAndCaptureBodiesAsync(IPage page)
    {
        var bodies = new List<string>();
        var submission = 0;

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
        await page.RouteAsync("**/api/query/execute-async", route =>
        {
            var body = route.Request.PostData ?? "{}";
            bodies.Add(body);
            submission++;
            return route.FulfillAsync(new RouteFulfillOptions
            {
                ContentType = "application/json",
                Body = $$"""{"jobId":"job-{{submission}}"}""",
            });
        });
        // Register the broad status route first, then the more specific preview route, so
        // preview (later registration = higher Playwright priority) wins for its own URL.
        await page.RouteAsync("**/api/query/jobs/*", route =>
        {
            var url = route.Request.Url;
            var jobId = url[(url.LastIndexOf('/') + 1)..];
            return route.FulfillAsync(new RouteFulfillOptions
            {
                ContentType = "application/json",
                Body = $$$"""{"status":"completed","jobId":"{{{jobId}}}","query":"q","result":{"totalRows":5,"headline":{"kind":"count","count":5},"warnings":[]}}""",
            });
        });
        await page.RouteAsync("**/api/query/jobs/*/preview", route => route.FulfillAsync(new RouteFulfillOptions
        {
            ContentType = "application/json",
            Body = """{"rows":[],"totalRows":5,"hasMore":false}""",
        }));

        return bodies;
    }
}
