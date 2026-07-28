using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Browser;

/// <summary>
/// Slice C2 (F01) client guard: proves the browser transmits only a server-resolvable
/// reference to the prior completed turn (<c>previousJobId</c>) on a follow-up, and never
/// a client-built context payload (FOLLOWUP-D2). It drives the real checked-in page in
/// Chromium, stubs the async-query flow so the first query completes, then submits a
/// second query and inspects the outgoing request body. The harness is Slice T1
/// (<see cref="StaticSiteFixture"/>).
/// </summary>
[Collection(BrowserCollection.Name)]
public sealed class FollowUpContextTransmissionTests
{
    private readonly StaticSiteFixture _fixture;

    public FollowUpContextTransmissionTests(StaticSiteFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task FollowUp_SendsPreviousJobId_AndNoClientContext()
    {
        var page = await _fixture.Browser.NewPageAsync(new BrowserNewPageOptions
        {
            ColorScheme = ColorScheme.Dark,
        });
        try
        {
            var bodies = await StubFlowAndCaptureBodiesAsync(page);

            // First query: no prior turn yet, so no previousJobId. The floating
            // chat is the sole query input (F02).
            await page.GotoAsync(_fixture.BaseAddress + "/");
            await page.FillAsync("#chatInput", "who is in group X");
            await page.ClickAsync("#chatSend");
            await Assertions.Expect(page.Locator("#headline")).ToBeVisibleAsync();
            // The chat ignores a submit while a query is in flight, so wait for the
            // first turn to settle before following up.
            await Assertions.Expect(page.Locator("#chatLog .turn.bot.pending")).ToHaveCountAsync(0);

            // Follow-up query: must reference the completed first job.
            await page.FillAsync("#chatInput", "and in Dublin?");
            await page.ClickAsync("#chatSend");
            await Assertions.Expect(page.Locator("#headline")).ToBeVisibleAsync();
            await Assertions.Expect(page.Locator("#chatLog .turn.bot.pending")).ToHaveCountAsync(0);

            Assert.Equal(2, bodies.Count);

            var first = JsonDocument.Parse(bodies[0]).RootElement;
            Assert.False(first.TryGetProperty("previousJobId", out _));

            var second = JsonDocument.Parse(bodies[1]).RootElement;
            Assert.True(second.TryGetProperty("previousJobId", out var prev));
            Assert.Equal("job-1", prev.GetString());

            // No client-built context material is ever transmitted (FOLLOWUP-D2).
            Assert.False(second.TryGetProperty("context", out _));
        }
        finally
        {
            await page.CloseAsync();
        }
    }

    /// <summary>
    /// Stubs the async-query flow so each submitted query completes, assigning a fresh job
    /// id per submission, and records every <c>execute-async</c> request body.
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
