# slice-t1: F01 Slice T1 — automated browser test harness (TEST-D1)

**Severity**: MEDIUM — a vacuous or misconfigured harness would give false green to every downstream front-end slice (B2, C3), which depend on it as their only automated rendering guard. Test/infra only; no product code.
**Status**: Verified — reviewer accepted, guard_confirmed (awaiting owner-gated merge)
**Branch**: (none — committed directly to master, this repo's non-branch policy for F01 slices; reviewed post-hoc over a pinned SHA range because history rewrite is forbidden)
**Commit**: `01714e4` (test(F01): add Playwright headless browser harness (Slice T1, TEST-D1))

## Evidence
Reviewed range `49919db..01714e4`. Diff (4 files: 3 new + 2 modified):
- `tests/AdQueryOrchestrator.Tests/AdQueryOrchestrator.Tests.csproj` — adds `Microsoft.Playwright` 1.61.0.
- `tests/AdQueryOrchestrator.Tests/packages.lock.json` — regenerated lock for the new package.
- `tests/AdQueryOrchestrator.Tests/Browser/StaticSiteFixture.cs` (new) — `IAsyncLifetime` fixture: static Kestrel serving `csharp/wwwroot` on a throwaway loopback port + a shared Chromium `IBrowser`. Repo root resolved by walking up from the test assembly to the `ADQuery.sln` directory.
- `tests/AdQueryOrchestrator.Tests/Browser/BrowserCollection.cs` (new) — xUnit collection sharing the fixture.
- `tests/AdQueryOrchestrator.Tests/Browser/HarnessSmokeTests.cs` (new) — one smoke test: open `/` in Chromium, assert `#queryForm` visible.
- `scripts/verify.ps1` — adds an `Install-PlaywrightBrowser` stage (runs the emitted `playwright.ps1 install chromium`) after the Release build, before `dotnet test`.

## Predicted observable failure
Without this slice the front-end rendering slices (B2/C3) would have no automated guard — a broken rendering branch or theme would ship green (TEST-D1 forbids manual-only coverage). A *vacuous* harness (serving the wrong assets, or a browser that never actually loads the page) is the specific risk here: it would pass regardless of the real page, giving false confidence. Guarded by `HarnessSmokeTests.RealPage_LoadsInChromium_WithBootstrapElement`, which asserts the real `#queryForm` bootstrap element (`app.js:2` depends on it) is present after a real Chromium navigation.

## What
F01 Slice T1: the automated headless-browser test harness mandated by TEST-D1 (owner, 2026-07-27). Playwright for .NET drives real Chromium against the checked-in `csharp/wwwroot` assets served over a throwaway Kestrel port. It is placed in the existing .NET test project so `scripts/verify.ps1` stays the single verification gate (no Node toolchain fork). jsdom was rejected: it does no layout/CSS, so it cannot verify the theme palette or resize-clamp geometry the design contract requires.

## Approach
`StaticSiteFixture` (an `IAsyncLifetime` collection fixture) builds a minimal `WebApplication` with `WebRootPath` set to `csharp/wwwroot` (located by walking up to `ADQuery.sln`), binds `127.0.0.1:0`, serves the static files, and launches one shared headless Chromium. The smoke test navigates a real browser to `/` and asserts `#queryForm` is visible — proof the real page loaded and `app.js` bootstrapped. `verify.ps1` installs Chromium via the Playwright bootstrapper emitted into the test build output. F01 `/api/query/*` calls are client-side JS over injected payloads and will be stubbed per-test via route interception in B2/C3, so the harness needs no live AD/DB/auth.

## Files changed
- `tests/AdQueryOrchestrator.Tests/AdQueryOrchestrator.Tests.csproj` — `Microsoft.Playwright` 1.61.0.
- `tests/AdQueryOrchestrator.Tests/packages.lock.json` — regenerated.
- `tests/AdQueryOrchestrator.Tests/Browser/StaticSiteFixture.cs` — static host + Chromium fixture.
- `tests/AdQueryOrchestrator.Tests/Browser/BrowserCollection.cs` — shared collection.
- `tests/AdQueryOrchestrator.Tests/Browser/HarnessSmokeTests.cs` — smoke guard.
- `scripts/verify.ps1` — `Install-PlaywrightBrowser` stage.

## Guard proof
- `HarnessSmokeTests.RealPage_LoadsInChromium_WithBootstrapElement` — green at head `01714e4`.
- Non-vacuity (coder, 2026-07-27): temporarily pointing the fixture web root at `csharp` (one level above `wwwroot`, no `index.html`) made the smoke test FAIL; restoring `csharp/wwwroot` made it PASS. This proves the test actually depends on the real served page, not on the browser merely launching. (Reviewer refinement: the failure surfaces as an HTTP 404 at navigation — the `response.Ok` assertion — which precedes the `#queryForm` check; see Reviewer comments.)
- Full `scripts/verify.ps1` at head: 212 passed, 1 skipped, 0 warnings, publish smoke + vuln audit clean (up from 211 — the new browser test).

## Coder dispute (if any)
None.

## Known gaps
- The `Install-PlaywrightBrowser` stage is a network operation (downloads Chromium); an offline run fails there. This is intentional per the plan — a real environmental blocker to surface, not to bypass. No offline-cache fallback is provided.
- The harness serves static assets only; it does not run the application `Program`. This is deliberate (F01 rendering is client-side over stubbed payloads) but means the harness would not catch a server-side routing/middleware regression — those stay covered by the existing `WebApplicationFactory`-based tests.
- No per-kind rendering assertions yet; those land in B2 on top of this harness. T1's own guard only proves the harness drives the real page.

## Reviewer comments

Reviewer: codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh/standard (`--profile review`, danger-full-access, owner-authorized 2026-07-27). Dispatched headless one-shot from the agent's own tool (no owner `!` relay — the review profile carries no bypass flag, so the auto-mode classifier does not block it).

Verdict: **accepted**, `guard_confirmed: true`. Envelope validated fail-closed: exit 0, single schema-valid JSON, `verdict` in enum, `reviewed_sha`==`01714e4`, `base_sha`==`49919db`. (Two `codex_login` token-refresh ERROR lines appear in the transcript *after* `turn.completed` — post-run auth expiry, not part of the review run.)

Reviewer's own worktree guard proof (corroborated from the transcript, detached worktree at `01714e4`): changed `ResolveWebRoot` to return `csharp` (one level above `wwwroot`) → build succeeded → focused browser test FAILED; restored `StaticSiteFixture` → focused test PASSED; then `scripts/verify.ps1` in the worktree PASSED (212 passed, 1 skipped, publish smoke clean, dependency audits clean). Non-vacuity dependency confirmed independent of the coder's run.

Substantive comments (all confirming, no defects):
- `StaticSiteFixture.cs:73` — `ResolveWebRoot` walks up to `ADQuery.sln` and returns `csharp/wwwroot`; the host serves the checked-in front-end, not a fabricated fixture.
- `StaticSiteFixture.cs:40` — binds `http://127.0.0.1:0`, then `ResolveBaseAddress` reads `IServerAddressesFeature` for the concrete bound address, not the requested `:0`.
- `StaticSiteFixture.cs:33` — xUnit v3 `IAsyncLifetime` `ValueTask` signatures; `DisposeAsync` disposes browser, Playwright, and the `WebApplication`.
- `HarnessSmokeTests.cs:28` — real Chromium page at the fixture base address; asserts HTTP success before `#queryForm` (present at `index.html:28`, read by `app.js:2`).
- `AdQueryOrchestrator.Tests.csproj:17` — `Microsoft.Playwright` added to the existing test project; `git diff 49919db..01714e4` touches only `scripts/verify.ps1` and test-project files, no product code under `csharp/`.
- `scripts/verify.ps1:450` — `Install-PlaywrightBrowser` runs the emitted `playwright.ps1 install chromium` after the Release build and before `dotnet test`, without weakening restore/format/build/test/publish/audit stages.

**Reviewer correction (folded in):** the finding's "Guard proof" recorded the break-mode failure point as the `#queryForm` assertion; the reviewer observed the actual failure is an HTTP 404 at navigation (the page does not load at all when the web root is wrong), which fails the `response.Ok` assertion before `#queryForm` is reached. The non-vacuity dependency is real either way; the precise failure point is the earlier HTTP-success assertion.

No `reopened` or `invalid` findings. Merge remains owner-gated (accepted ≠ merge authority).
