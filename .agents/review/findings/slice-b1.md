# slice-b1: F01 Slice B1 — backend headline-shape contract

**Severity**: MEDIUM — a miswired classifier yields a wrong or misleading headline answer (e.g. showing a count where a single record was found, or leaking more grouped categories than DATA-D1 permits). Backend-only; no new AD values leave the server beyond the DATA-D1-bounded grouped payload, and the authoritative download path is unchanged.
**Status**: Verified — reviewer accepted, guard_confirmed (awaiting owner-gated merge)
**Branch**: (none — committed directly to master, consistent with this repo's non-branch policy for F01 slices; reviewed post-hoc over a pinned SHA range because history rewrite is forbidden)
**Commit**: `71cb1b1` (feat(query): add plan-shape headline contract (F01 Slice B1))

## Evidence
Reviewed range `dcc9c96..71cb1b1`. Diff (4 files, +372):
- `csharp/Services/HeadlineClassifier.cs` (new) — pure static `Classify(plan, totalRows, aggregation, firstRow)` with fixed kind precedence; `MaxHeadlineGroups = 10` DATA-D1 ceiling; `ExtractGroups` (bounded, count-desc then key-asc ordinal) and `HasExpansion` helpers.
- `csharp/Models/HeadlineResult.cs` (new) — `HeadlineResult { Kind, Count?, Record?, Groups? }`, `HeadlineKind` constants (`none`/`count`/`record`/`grouped`), `HeadlineGroup { Key, Count }`.
- `csharp/Controllers/QueryController.cs` (+23) — `BuildHeadline(job)` added; wired into the completed-job `result` block of `GetJobStatus` (`headline = BuildHeadline(job)`). Reads the first row from the results cache (same cache as `GetJobPreview`) only when `totalRows == 1` makes the record kind possible.
- `tests/AdQueryOrchestrator.Tests/Unit/HeadlineClassifierTests.cs` (new) — table-driven guard, 9 tests.

## Predicted observable failure
If the classifier regressed, the browser (Slice B2) would lead with a wrong answer: a count where a unique person was found, a "record" for a one-member group expansion (a `SizeLimit=1` seed can fan out), a grouped payload exceeding the DATA-D1 ≤10 ceiling, or a value payload on a zero-row result. Guarded by `HeadlineClassifierTests`: one case per kind (none/grouped/count-distinct/count-purecount/record/count-multirow/count-from-expansion), a DATA-D1 bounding+ordering case, and a precedence-totality case.

## What
F01 Slice B1: a deterministic, plan-shape-derived headline answer computed server-side and exposed on the async job status, so the UI can lead with a direct answer instead of a raw grid. Async path only (the sync `execute` endpoint is retired per SYNC-D1). No raw `job.Plan` is shipped to the browser.

## Approach
A pure `HeadlineClassifier.Classify` implements the plan's fixed precedence (empty → grouped → plan-requested-aggregation count → single non-expansion record → multi-row count) so it is table-testable in isolation. Grouped payloads are bounded to `MaxHeadlineGroups` (10) with deterministic ordering (count descending, then key ascending ordinal) — an independent server ceiling, not the unbounded `PreviewRowCount`. The controller's `BuildHeadline` supplies the classifier with the job's plan, total rows, runtime aggregation, and — only when a single row makes the record kind reachable — the first cached row.

## Files changed
- `csharp/Services/HeadlineClassifier.cs` — new pure classifier + bounding.
- `csharp/Models/HeadlineResult.cs` — new DTO + kind constants.
- `csharp/Controllers/QueryController.cs:1072-1078` (result block), `BuildHeadline` helper next to `BuildAggregationSummary` — wiring.
- `tests/AdQueryOrchestrator.Tests/Unit/HeadlineClassifierTests.cs` — guard.

## Guard proof
- `HeadlineClassifierTests` — 9 tests, green at head `71cb1b1`.
- Non-vacuity (coder, 2026-07-27): forcing `Classify` to a single `count` branch turned the guard red (Failed: 5, Passed: 4 — none/grouped/record cases collapse); restoring returned all 9 green.
- Full `scripts/verify.ps1` at head: 211 passed, 1 skipped, 0 warnings, publish smoke + vuln audit clean.

## Coder dispute (if any)
None.

## Known gaps
- Reviewed post-hoc over a SHA range rather than on a `fix/` branch (work already on master; history rewrite forbidden).
- The grouped payload bound is asserted on the pure classifier; the controller's `BuildAggregationSummary` (separate, pre-existing) still returns the full grouped dictionary for the legacy aggregation UI — that is a Slice B2/C concern (what the UI renders / what follow-up sends), not a B1 defect. B1 only adds the bounded `headline` field.
- `record` payload is the raw projected row (already the on-screen preview slice); DATA-D1 bounding of individual record fields is deferred to the rendering/follow-up slices.

## Reviewer comments

Reviewer: codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh/standard (`--profile review`, danger-full-access, owner-authorized 2026-07-27). Dispatched headless one-shot from the agent's own tool (no owner `!` relay — the review profile carries no bypass flag, so the auto-mode classifier does not block it).

Verdict: **accepted**, `guard_confirmed: true`. Envelope validated fail-closed: exit 0, single schema-valid JSON, `verdict` in enum, `reviewed_sha`==`71cb1b1`, `base_sha`==`dcc9c96`.

Reviewer's own worktree guard proof (corroborated from the JSONL transcript, worktree `adquery-review-71cb1b1-codex`): `git worktree add --detach` at head → forced `Classify` to a single `count` branch → filtered `HeadlineClassifierTests` FAILED (Failed:5, Passed:4) → `git checkout 71cb1b1 -- csharp/Services/HeadlineClassifier.cs` → PASSED (9/9) → `scripts/verify.ps1` in the worktree PASSED (211 passed, 1 skipped, build clean, publish smoke + vuln audit clean). This matches the coder's recorded non-vacuity result exactly.

Substantive comments (all confirming, no defects):
- `HeadlineClassifier.cs:45` — precedence applied exactly: zero→none before grouped/count/record; grouped wins next; aggregation-plan-without-grouped→count; single non-expansion→record; else→count.
- `HeadlineClassifier.cs:73` — `HasExpansion` consulted before record, so a single row from recursive/`expand_members`/`expand_reports` falls through to count.
- `HeadlineClassifier.cs:98` — grouped output capped `Take(MaxHeadlineGroups)` after count-desc, key-asc-ordinal ordering; `MaxHeadlineGroups`==10.
- `QueryController.cs:1072` — status result adds only the `HeadlineResult` DTO; no raw `job.Plan` in the response.
- `QueryController.cs:1800` — `BuildHeadline` reads the cached first row only on `totalRows == 1`.
- `QueryController.cs:1076` — scope confirmed: `git diff dcc9c96..71cb1b1` touches only QueryController + the three new files; no appsettings/config or sync `execute` change.

No `reopened` or `invalid` findings. Merge remains owner-gated (accepted ≠ merge authority).
