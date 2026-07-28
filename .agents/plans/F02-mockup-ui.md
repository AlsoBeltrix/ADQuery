# F02 — Main window matches the approved mockup

**Status:** Approved (owner, 2026-07-28). Owner confirmed keep the retry-with-alternate-model / downloads / feedback controls, restyled into the mockup blocks (not deleted).

## Problem

The shipped page (`csharp/wwwroot/index.html`) has **two** text inputs: the main-window
search form (`#queryForm` / `#queryText` / `#searchBtn`) and the floating chat
(`#chatInput`). Slice C3 added the chat *additively* over the pre-existing main form
instead of replacing it. The approved mockup (`artifacts/mockups/qa-ui.html`, git-ignored,
disposable) has exactly one input — the chat — and a main window that is answers-only,
rendered as `.block` cards (value / person / count+table).

Owner intent (2026-07-28): "chat window is for chatting, main window is for results";
"make it look like the approved mockup."

## Non-goals / constraints

- **Preserve all working behavior.** The retry-with-alternate-model control
  (`retryWithAlternateModel()`), the download formats, the feedback thumbs, warnings, and
  aggregation table are functional features and stay. They are restyled to fit the mockup's
  visual language (pill `.op` buttons, `.block` cards); they are not deleted. Rationale:
  retry-with-alternate-model is the exact path that produced the owner's preferred CFO
  answer — removing it would regress the feature the owner just validated.
- The floating chat surface (markup, resize, exchange log, FOLLOWUP-D2 transmit contract)
  is already mockup-aligned and stays as-is except where wiring changes below require it.
- Design tokens (`--bg`, `--panel`, `--accent`, `--sans`, `--mono`, …) already live in
  `csharp/wwwroot/css/styles.css` under `html[data-theme=...]` (landed in B2). Reuse them;
  do not redefine.
- Governance: one concern per commit; each behavior-changing slice adds a focused guard
  proven red→green; run `pwsh -NoLogo -NoProfile -File scripts/verify.ps1` before every
  commit.

## Current wiring that must change

- `runQuery()` (`app.js:259`) reads its text from `#queryText`. `submitChatQuery()`
  (`app.js:1078`) mirrors the chat text into `#queryText` then calls `runQuery()`. Removing
  `#queryText` breaks this bridge.
- The T1 browser guards drive queries through the main form:
  `HeadlineRenderingTests.RunQueryWithHeadlineAsync` (`tests/.../Browser/HeadlineRenderingTests.cs:219-222`)
  calls `page.FillAsync("#queryText", ...)` + `page.ClickAsync("#searchBtn")`. These must be
  rewired to drive via `#chatInput` + `#chatSend` (the sole remaining input). The guards
  still prove the same render behavior; only the driving selector changes.

## Slices

Each slice is one commit with its own guard and a green `verify.ps1`.

### Slice 1 — Sole input: remove the main form, rewire the query path

**Commit:** `refactor(ui): make the chat the sole query input`

- Delete the `#queryForm` / `#queryText` / `#searchBtn` block and the enclosing
  `.search-card` from `index.html` (lines 26-49).
- Rewire `app.js`: `runQuery()` takes the query text as a parameter instead of reading
  `#queryText`. The early `return` guarding a missing `#queryForm` element (`app.js:2-5`)
  is removed or repointed at `#chat` so the module still initializes. `submitChatQuery()`
  passes its trimmed text straight to `runQuery(query)`; drop the `queryInput.value = query`
  mirror and the `queryInput` lookups that only existed for the main form.
- `retryWithAlternateModel()` and any other caller of `runQuery()` are updated to pass the
  remembered last query text (persist the in-flight query text in `state` so retry has it).
- Update `HeadlineRenderingTests` + any other T1 test that drives `#queryText`/`#searchBtn`
  to drive `#chatInput`/`#chatSend`.

Guard: the T1 headline tests already assert the full render path
(`execute-async` → poll → preview → `renderHeadline`). After rewiring them to the chat
input, they *are* the guard that the sole-input path works end to end. Prove non-vacuous:
temporarily leave `runQuery` still reading the (now-deleted) `#queryText` → the tests fail
(no input found / query empty); restore → green.

### Slice 2 — Masthead matches the mockup

**Commit:** `feat(ui): adopt the mockup masthead`

- Replace the `<header>` (`index.html:11-16`) with the mockup masthead: `<h1>Directory
  <span class="slash">/</span> Search</h1>`, the theme button, and the `.subline`
  ("→ ask in the panel; answers resolve here in the form that fits."). Keep the existing
  `#themeToggle` id and the `#userInfo`/`#welcomeMessage` element (auth banner) — reposition,
  don't remove.
- Add the `.masthead`, `.slash`, `.subline`, `.theme-btn` rules to `styles.css` from the
  mockup, using the existing tokens.

Guard: extend a T1 test to assert `.masthead h1 .slash` is present and the accent color
token resolves (mirrors the B2 theme-palette assertion style). Prove red→green by
temporarily reverting the masthead markup.

### Slice 3 — Result panel renders as mockup `.block` cards

**Commit:** `feat(ui): render answers as mockup blocks`

- Restyle `#results` so the headline renders as the mockup blocks:
  - headline `count` → `.block.value` (`.v` big number + `.ctx` context line + `.ops`).
  - headline `record` → `.block.person` (`.who` name + `.kv` grid + `.ops`).
  - headline `grouped` / table → `.block` count+table (`.count` + `table.data`).
- Fold the existing download buttons into an `.ops` "Download ▾" affordance and the
  "See all / Full record" primary `.op.key` button per block; keep every download format
  reachable. Feedback thumbs + retry stay, restyled as `.op`/pill controls beneath the block.
- Add the `.block`, `.block-label`, `.value`, `.person`, `.kv`, `.count`, `table.data`,
  `.ops`, `.op` rules from the mockup to `styles.css` (tokens already defined).

Guard: extend `HeadlineRenderingTests` — count answer produces `.block.value .v`; record
answer produces `.block.person .who` + `.kv`; grouped/table produces `.count` + `table.data`.
Prove red→green by temporarily pointing a render branch at the wrong block class.

## Verification

`pwsh -NoLogo -NoProfile -File scripts/verify.ps1` (green, all guards) before each commit.
Front-end behavior is guarded by the T1 Playwright harness (TEST-D1), not manual notes.
