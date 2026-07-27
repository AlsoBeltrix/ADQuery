# slice-a: F01 Slice A — park CSV enrichment in the UI

**Severity**: LOW — removes browser-facing CSV surfaces only; the server `/api/query/csv-enrich` endpoint and its P04/P05 hardening are untouched, so the blast radius is the front-end, not production behavior or the endpoint contract.
**Status**: In progress (pending reviewer dispatch)
**Branch**: (none — committed directly to master, consistent with this repo's non-branch policy for F01 slices; reviewed post-hoc over a pinned SHA range because history rewrite is forbidden)
**Commit**: `3091932` (feat(ui): park CSV enrichment in the UI (F01 Slice A))

## Evidence
Reviewed range `1c68dda..3091932` (base = the F01-approval bookkeeping commit, head = Slice A). Diff:
- `csharp/wwwroot/index.html` (-46/+…) — removed the mode toggle (`queryMode` radios), the `#csvForm` upload form, `#csvStats`, and the `data-mode`/`query-mode-section` scaffolding; simplified `<form id="queryForm">`.
- `csharp/wwwroot/js/app.js` (-362) — removed CSV state fields, CSV element refs + listeners, the entire CSV enrichment function block (mode change, file select, `runCsvEnrichment`, `csv-enrich` fetch), and the `csvStats` reset in `hideResults`. Kept `case 'csv'` in `getExtension` (download format, unrelated).
- `csharp/wwwroot/css/styles.css` (-157) — removed the CSV enrichment style block (mode-toggle, file-upload, attribute-grid, etc.) and its mobile media query. Kept `.input-group label`.
- `tests/AdQueryOrchestrator.Tests/Unit/CsvUiParkingGuardTests.cs` (+81) — the guard.

## Predicted observable failure
If the parking regressed, the failure modes that matter are: (1) a CSV UI surface (mode toggle, upload form, file handler, or enrich fetch) survives in the shipped front end, re-exposing the parked feature to users; or (2) the server endpoint is accidentally removed along with the UI, breaking the parked-but-callable contract and its P04/P05 hardening. Guarded by `CsvUiParkingGuardTests`: two tests assert the browser surfaces are absent from `index.html`/`app.js`; one reflection test asserts `QueryController.CsvEnrich` stays mapped with `[HttpPost("csv-enrich")]`.

## What
F01 Slice A: remove the CSV enrichment mode from the browser UI (toggle, upload form, file handling, enrich fetch, and associated CSS) while leaving the server endpoint and its hardening fully intact. The feature is parked, not deleted.

## Approach
Deleted the CSV-specific HTML/JS/CSS surfaces so the UI presents only the natural-language query path. The server `QueryController.CsvEnrich` action and the P04 authorization / P05 scale hardening are deliberately untouched, so the endpoint remains callable. The guard test pins both halves: UI tokens gone, endpoint still mapped.

## Files changed
- `csharp/wwwroot/index.html` — removed mode toggle, `#csvForm`, `#csvStats`, `data-mode` scaffolding.
- `csharp/wwwroot/js/app.js` — removed CSV state, refs, listeners, enrichment function block, `csvStats` reset.
- `csharp/wwwroot/css/styles.css` — removed the CSV enrichment style block and its media query.
- `tests/AdQueryOrchestrator.Tests/Unit/CsvUiParkingGuardTests.cs` — new guard (3 tests).

## Guard proof
- `CsvUiParkingGuardTests` — 3 tests, green at head `3091932`.
- Non-vacuity (coder, 2026-07-27, ASHBIAMWEB1): reintroducing a `data-mode` token into `csharp/wwwroot/index.html` turned `IndexHtml_HasNoCsvModeToggleOrUploadForm` red (Failed: 1, Passed: 2); restoring the file returned all 3 green. Confirms the guard is not vacuous.
- Full `scripts/verify.ps1` at Slice A landing (recorded pre-commit): 202 passed, 1 skipped, 0 warnings.

## Coder dispute (if any)
None.

## Known gaps
- Reviewed post-hoc over a SHA range rather than on a `fix/` branch, because the work is already committed to master and history rewrite is forbidden by repo Git Safety invariants (same posture as `slice0`).
- The guard asserts on literal source tokens (string absence + reflection), not rendered DOM. A JS/DOM test harness does not exist in this repo; behavioral coverage of the rendered page is manual smoke only. This is an F01 open item, not a Slice A defect.

## Reviewer comments

### Dispatch 1 — FAILED (environment, not a code judgment) — 2026-07-27
`Reviewer: codex / (default configured model+effort, inline session-only) / workspace-write`
- Harness: codex-cli 0.144.6 on ASHBIAMWEB1. Intended range `1c68dda..3091932`.
- Bounded capability smoke test (playbook step 3, `-s workspace-write`) failed terminally on two independent causes before any review could run:
  1. **Auth expired.** Every turn logged `codex_login::auth::manager: Failed to refresh token: Your access token could not be refreshed. Please log out and sign in again.` codex is not authenticated on this host.
  2. **Write-sandbox denial.** Creating a scratch file returned `Access to the path 'D:\source\adquery\probe-scratch.txt' is denied` — the same broken Windows `workspace-write` path that failed slice0's Dispatch 1 (CLI) and Dispatch 2 (MCP). Read-only codex works; write mode does not.
- Both are terminal per the playbook (permission/tool denial is terminal; do not chase variants). The worktree guard proof requires write access, so no headless dispatch can set `guard_confirmed`.
- **Status:** In progress, pending a working reviewer path — owner decision required (re-auth codex + resolve the write sandbox, or run the review owner-interactive as slice0 was ultimately reviewed).
