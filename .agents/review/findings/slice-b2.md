# slice-b2: F01 Slice B2 — main-window headline rendering

**Severity**: MEDIUM — a broken render branch or misapplied theme would ship a wrong or unreadable headline answer to every user, the first thing they see. Front-end only; no product code under `csharp/` outside `wwwroot/`, no server or data-path change. The authoritative download and full data table are untouched.
**Status**: In progress (pending reviewer dispatch)
**Branch**: (none — committed directly to master, this repo's non-branch policy for F01 slices; reviewed post-hoc over a pinned SHA range because history rewrite is forbidden)
**Commit**: `54d7930` (feat(ui): render plain-language headline per kind (F01 Slice B2))

## Evidence
Reviewed range `3554a13..54d7930`. Diff (4 files, +507/-39):
- `csharp/wwwroot/index.html` — `html[data-theme="dark"]` default, drop `body.theme-dark`, add `<div id="headline" class="headline" hidden>` leading the results panel.
- `csharp/wwwroot/css/styles.css` — migrate theme selectors from `body.theme-dark/light` to `html[data-theme="dark"|"light"]`; adopt the F01 design-contract palette (both themes) and Candara fonts (FONT-D1); contract tokens aliased to the pre-existing legacy variable names; `.container` geometry to contract (`max-width:980px; padding:3rem 2.5rem 9rem`); new `.headline` block styles per kind.
- `csharp/wwwroot/js/app.js` — theme mechanism reads/writes `document.documentElement[data-theme]`; new `renderHeadline` + per-kind helpers (`renderHeadlineCount`/`Record`/`Grouped`), hooked into `displayJobResults` before `renderWarnings`.
- `tests/AdQueryOrchestrator.Tests/Browser/HeadlineRenderingTests.cs` (new) — 5 Playwright tests over the T1 harness with the `/api` async flow stubbed.

## Predicted observable failure
If a render branch regressed, the browser would lead with the wrong shape (a value hero where a person record was found, an empty block where groups exist, or a visible block on a zero-row result), or the design-contract palette would fail to apply per theme. Guarded by `HeadlineRenderingTests`: one test per kind (`Count_RendersValueHero`, `Record_RendersNameAndKvGrid`, `Grouped_RendersBoundedList`, `None_LeavesHeadlineHidden`) plus `ThemeToggle_AppliesContractPaletteInBothThemes`, which asserts the contract `--bg` (`#000000` dark, `#d4d2ca` light) resolves on `<body>` via `html[data-theme]` and that the headline survives the toggle.

## What
F01 Slice B2: the UI presentation of the B1 headline contract. The main window leads the result panel with a plain-language answer per kind — value hero (`count`), person record `.kv` grid (`record`), bounded grouped-count list (`grouped`); `none`/absent leaves the block hidden. It never replaces the authoritative download or the full data table beneath. Adopts the tracked F01 design contract for this surface (palette both themes, Candara fonts, `html[data-theme]` theme mechanism).

## Approach
`renderHeadline(job.result.headline)` reads the B1 DTO (`kind`/`count`/`record`/`groups`; camelCase over the wire) and dispatches to a per-kind helper, each building DOM with `textContent` (no innerHTML injection of server data). The theme mechanism moves from `body.theme-*` classes to `html[data-theme]` per the contract; the CSS defines the contract palette on `html[data-theme="dark"|"light"]` and aliases the contract tokens to the legacy variable names so the rest of the stylesheet inherits the new palette without a rewrite. `renderHeadline` is hooked into the existing async `displayJobResults` path (the shipped query path); the sync path is retired under SYNC-D1.

## Files changed
- `csharp/wwwroot/index.html` — theme attribute migration + `#headline` container.
- `csharp/wwwroot/css/styles.css` — `html[data-theme]` migration, contract palette/fonts/geometry, `.headline` block styles.
- `csharp/wwwroot/js/app.js` — `applyTheme`/`handleThemeToggle` on `data-theme`; `renderHeadline` + per-kind helpers; hook in `displayJobResults`.
- `tests/AdQueryOrchestrator.Tests/Browser/HeadlineRenderingTests.cs` — guard (5 tests).

## Guard proof
- `HeadlineRenderingTests` — 5 tests, green at head `54d7930` (focused run: 5 passed).
- Non-vacuity (coder, 2026-07-27): forcing `renderHeadline` to render nothing (early `return` before the kind dispatch) turned the guard red — Failed: 4, Passed: 1 (`count`/`record`/`grouped`/theme-persistence fail; `None_LeavesHeadlineHidden` stays green by design, since it asserts the block stays hidden — which the broken code also produces). Restoring returned all 5 green.
- Full `scripts/verify.ps1` at head: 217 passed, 1 skipped, 0 warnings, publish smoke + vuln audit clean (up from 212 — the 5 new browser tests).

## Coder dispute (if any)
None.

## Known gaps
- The `record` grid renders every projected field of the single row (the on-screen preview slice already visible in the table); DATA-D1 per-field bounding of individual record fields is deferred to the follow-up slices, consistent with the B1 known-gap.
- `ThemeToggle` uses `ToHaveCSSAsync` (retrying) rather than a one-shot `getComputedStyle`, because the `0.25s` background transition means a single read can catch an intermediate colour; this is an assertion-robustness choice, not a product behaviour.
- The test pins the emulated OS colour scheme to dark (`ColorScheme.Dark`) so `initTheme`'s `prefers-color-scheme` fallback resolves to the contract default deterministically; the default-theme resolution logic itself is unchanged from the shipped code.

## Reviewer comments

(pending dispatch)
