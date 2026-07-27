# slice-c3: F01 Slice C3 — floating conversational chat surface

**Severity**: MEDIUM — a chat surface that either (a) transmitted its display-only exchange log would defeat FOLLOWUP-D2's no-accumulated-transcript rule and the C1 byte cap, or (b) rendered without the current/past delineation or resize clamp would misrepresent which turn is live and break the Design contract. Client-side UI + request-shaping change; no auth, crypto, schema, migration, or server data-path touched.
**Status**: In progress
**Branch**: (none — committed directly to master, this repo's non-branch policy for F01 slices; reviewed post-hoc over a pinned SHA range because history rewrite is forbidden)
**Commit**: `06776ff` (feat(ui): floating conversational chat surface (F01 Slice C3))

## Evidence
Reviewed range `1b54bd1..06776ff`. Diff (4 files, +718):
- `csharp/wwwroot/index.html` — floating chat markup (`.chat > .rz + .chat-head + .chat-log + .chat-composer`) added before the app.js script; the `#queryForm` bootstrap contract (app.js early-exit on missing `#queryForm`) is preserved unchanged.
- `csharp/wwwroot/css/styles.css` — F01 Slice C3 block: `.chat` geometry (380×480 default, min 300×260, `max-width:50vw`/`max-height:100vh`, no box-shadow), `.rz` top-left resize handle, `.exchange.current`/`.past` delineation (past dimmed 0.62), `.qa-rule` hairline, `.turn.you`/`.turn.bot` bubble radii, minimized collapse.
- `csharp/wwwroot/js/app.js` — chat DOM refs; `initChat`, `submitChatQuery` (mirrors the question into `#queryText` and drives the existing `runQuery`), `appendChatExchange` (demotes prior `.current` → `.past`, appends a pending answer bubble), `resolveChatAnswer`/`failChatAnswer` (settle the pending bubble on completion/error), `summariseJobForChat` (per-kind plain-language echo from the B1 headline), `resetChatConversation` ("start over": clears `state.lastCompletedJobId` + the DOM log), `updateChatRefineVisibility`, `initChatResize` (pointer-drag clamped to `min(50vw, …)`/`min(100vh, …)`). Wired into `displayJobResults` (resolve) and `showError` (fail). The follow-up payload still sends only `previousJobId` (C2) — the display-only log is never added to the request body.
- `tests/AdQueryOrchestrator.Tests/Browser/ChatSurfaceTests.cs` (new) — three T1-harness guards.

## Predicted observable failure
- **Resize clamp.** Without the 50vw × 100vh clamp (JS `Math.min` and the CSS `max-width`/`max-height` together), a drag would grow the panel past half the viewport width / full height. Guarded by `Resize_ClampsToHalfViewportWidthAndFullViewportHeight` (loosening both the CSS caps and the JS clamp yields 1690px width vs the 600px cap → red).
- **Exchange delineation.** If prior exchanges were not demoted, two exchanges would both read as `.current`, misrepresenting which turn is live. Guarded by `Exchanges_RenderWithCurrentPastDelineationAndHairline` (skipping the demotion leaves 2 `.current` → red).
- **Display history transmitted.** If the chat added its prior-turn material (question/answer/transcript) to the outgoing body, FOLLOWUP-D2 would be defeated. Guarded by `DisplayHistory_IsNeverTransmitted` (adding a `context` with the prior question to the payload → the `Assert.False(TryGetProperty("context"))` / `Assert.DoesNotContain(priorQuestion)` red).

## What
F01 Slice C3: a floating, resizable chat panel that drives initial queries and follow-ups through the SAME request path as the main form (`runQuery` → `execute-async`); results resolve in the main result panel, which stays authoritative. The chat keeps a display-only exchange log that lives only in the DOM, is cleared on reload, and is never transmitted — a follow-up sends only C2's `previousJobId`. "Start over" ends the follow-up chain and clears the log. The `#queryForm` bootstrap contract is preserved; the chat is an additional surface over the same query path (FOLLOWUP-D2 honored: no accumulated transcript reaches the model or server).

## Approach
The plan's C3 "preserve or deliberately replace the `#queryForm` bootstrap" fork is settled toward **preserve** — lowest risk, keeps the landed B2/C2 browser guards passing unchanged — and the chat is layered as an additional conversational surface that reuses `runQuery`. `submitChatQuery` mirrors the question into `#queryText` so the single existing request path (and its C2 `previousJobId` wiring) is the ONLY transmission path; the chat never constructs its own request body, so the display-only log is structurally incapable of being sent. Geometry, palette, and delineation come verbatim from the plan's binding Design contract. The resize clamp is enforced redundantly in JS (`Math.min`) and CSS (`max-width:50vw`/`max-height:100vh`); non-vacuity required loosening both layers together, since either alone masks the other.

## Files changed
- `csharp/wwwroot/index.html` — floating chat markup; `#queryForm` bootstrap preserved.
- `csharp/wwwroot/css/styles.css` — F01 Slice C3 chat block (geometry, resize handle, delineation, bubbles).
- `csharp/wwwroot/js/app.js` — chat wiring, resize clamp, exchange render, ephemeral display-only history; `displayJobResults`/`showError` hooks.
- `tests/AdQueryOrchestrator.Tests/Browser/ChatSurfaceTests.cs` (new) — resize-clamp, delineation, display-history-not-transmitted guards.

## Guard proof
- `ChatSurfaceTests.Resize_ClampsToHalfViewportWidthAndFullViewportHeight`, `.Exchanges_RenderWithCurrentPastDelineationAndHairline`, `.DisplayHistory_IsNeverTransmitted` — green at head `06776ff`.
- Non-vacuity (coder, 2026-07-27), each temporary break reverted after:
  - Loosening both the CSS caps (`max-width/height:5000px`) and the JS clamp → `Resize_…` red (1690px width vs 600px cap, tolerance 1).
  - Skipping the `.current` → `.past` demotion → `Exchanges_…` red (2 `.current`, expected 1).
  - Adding `payload.context = <prior question>` to the follow-up body → `DisplayHistory_…` red (`context` present / prior question found in body).
- Full `scripts/verify.ps1` at head: 254 passed, 1 skipped, 0 warnings; publish smoke (401 + Swagger hidden in Production; Swagger JSON/UI in Development) + vuln audit clean (up from 251 — the three new C3 browser guards).

## Coder dispute (if any)
None.

## Known gaps
- The resize clamp is enforced by both a JS `Math.min` and CSS `max-width`/`max-height`; the guard treats "the clamp" as the observable rendered outcome, so proving non-vacuity required breaking both layers together (either alone leaves the outcome capped).
- `summariseJobForChat` echoes a short plain-language line derived from the B1 headline; the main result panel remains the authoritative rendering. The chat echo is intentionally minimal, not a second full renderer.
- The chat drives queries by mirroring the question into `#queryText` and calling the shared `runQuery`; this is the deliberate single-transmission-path design that makes the display-only log structurally non-transmittable, not an incidental coupling.

## Reviewer comments
(pending reviewer dispatch)
