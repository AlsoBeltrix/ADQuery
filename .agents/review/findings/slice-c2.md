# slice-c2: F01 Slice C2 — server-resolved last-turn follow-up context

**Severity**: MEDIUM — a follow-up that trusted client-asserted provenance or context could (a) leak another user's last turn into this query's context by referencing a foreign job id, or (b) let the client inject an unbounded/multi-turn transcript, defeating FOLLOWUP-D2 and the C1 byte cap. Server-side data-path change (resolution, ownership check, context assembly → persistence/logging/transmission input); no auth, crypto, schema, migration, or wire-format path touched.
**Status**: In progress — pending reviewer dispatch
**Branch**: (none — committed directly to master, this repo's non-branch policy for F01 slices; reviewed post-hoc over a pinned SHA range because history rewrite is forbidden)
**Commit**: `6c53a0d` (feat(query): server-resolved last-turn follow-up context (F01 Slice C2))

## Evidence
Reviewed range `051059a..6c53a0d`. Diff (8 files, +607/-21):
- `csharp/Services/FollowUpContextBuilder.cs` (new) — `IFollowUpContextBuilder` / `FollowUpContextBuilder`. `BuildFromPreviousTurn(QueryJob)` sources only the three last-turn components from the resolved prior job (prior question, plan-shape summary, minimal value slice) and composes them through the C1 `IFollowUpContextEnforcer.Compose` byte cap. The value slice reads `Aggregation["grouped_counts"]`, orders desc, and takes `QueryDefaults:SummaryRowCount` (default 20) rows. It never reads the prior job's own `Context`.
- `csharp/Controllers/QueryController.cs:1021` — `ExecuteQueryAsync` context resolution rewritten: `context` starts null; if `request.PreviousJobId` is set, the job is resolved via `_jobManager.GetJob`, ownership-checked (`previousJob.UserName` != caller → `Forbid()`), and only a `Completed` prior job yields context via `_followUpContextBuilder.BuildFromPreviousTurn`. Any client-supplied `request.Context` is ignored. `QueryRequest.PreviousJobId` added; `Context` retained with `[StringLength(2000)]` (so the C1 byte cap can't be pre-empted by binding-time rejection) but documented as ignored by `ExecuteQueryAsync`.
- `csharp/Program.cs` — `AddSingleton<IFollowUpContextBuilder, FollowUpContextBuilder>` between the enforcer and job-manager registrations.
- `csharp/wwwroot/js/app.js:37,267,413` — `state.lastCompletedJobId` (separate from in-flight `currentJobId`); `runQuery` sends `previousJobId` when a prior completed job exists and no client context; `displayJobResults` records the completed job id. `buildContextHint` removed.
- 3 new test files (builder unit, controller provenance unit, browser transmission) + `CsvEnrichmentControllerTests.cs` ctor call sites updated for the new injected dependency.

## Predicted observable failure
- **Foreign-provenance leak.** Without the ownership check, a follow-up citing another user's `previousJobId` would assemble that user's last turn into this query's context. Guarded by `QueryControllerFollowUpProvenanceTests.ExecuteQueryAsync_ForeignPreviousJobId_IsRejected` (removing `Forbid()` returns `AcceptedResult` and creates a job).
- **Client-asserted context / accumulation.** If the controller trusted `request.Context` instead of server-building it, an unbounded/multi-turn client transcript would be persisted/transmitted (FOLLOWUP-D2 breach). Guarded by `ExecuteQueryAsync_OwnCompletedPreviousJob_BuildsServerSideContext` (sourcing context from `request.Context` drops the prior-question material and leaks the client sentinel). The builder's `BuildFromPreviousTurn_DoesNotCarryPriorContext` guards against forwarding the prior job's own `Context`.
- **Unbounded value slice.** If the value slice were not row-capped, a large grouped result would carry more values than the user saw. Guarded by `FollowUpContextBuilderTests.BuildFromPreviousTurn_ValueSlice_BoundedToSummaryRowCount` (raising the take drops the exclusion). Byte-cap fail-closed guarded by `BuildFromPreviousTurn_BoundedByByteCap`.
- **Client transmission.** If the client still built context or omitted `previousJobId`, provenance would not be server-verifiable. Guarded by `FollowUpContextTransmissionTests.FollowUp_SendsPreviousJobId_AndNoClientContext` (disabling the client wiring drops `previousJobId`).

## What
F01 Slice C2 (FOLLOWUP-D2): last-turn follow-up context is assembled server-side from the prior completed job, not accepted from the client. A follow-up asserts only a `previousJobId`; the server resolves it, ownership-checks it (foreign → `Forbid`), and — only for a completed prior job — builds a bounded context of the prior question, a plan-shape summary, and a minimal value slice through the C1 byte cap. Last-turn provenance is server-verified, not client-asserted; context cannot accumulate across turns because the prior job's own `Context` is never forwarded and client-supplied context is ignored.

## Approach
The plan's C2 provenance fork (client-returned summary DTO vs server-resolved `previousJobId`) is settled by the plan's own binding constraint ("Last-turn provenance must be server-verifiable, not client-asserted") and the guard "a forged/foreign previousJobId is rejected" — so the client sends only `previousJobId` and the server owns resolution, ownership, and assembly. `FollowUpContextBuilder` consumes the reviewed C1 `Compose` primitive (fixed keep-priority prior-question → plan → values, whole-component drop, never splits a UTF-8 code point). The value slice is bounded to `QueryDefaults:SummaryRowCount` — the exact row count the aggregation UI already displays — so a follow-up never carries more grouped values than the user saw, avoiding an unmeasured "typical size" guess. A not-found/expired prior job is benign (no context, proceed); a non-completed job carries no summarizable material.

## Files changed
- `csharp/Services/FollowUpContextBuilder.cs:1` — server-side last-turn context builder.
- `csharp/Controllers/QueryController.cs:1021` — server-side `previousJobId` resolution + ownership check; `QueryRequest.PreviousJobId`.
- `csharp/Program.cs` — DI registration.
- `csharp/wwwroot/js/app.js:37,267,413` — client sends `previousJobId`; `buildContextHint` removed.
- `tests/AdQueryOrchestrator.Tests/Unit/FollowUpContextBuilderTests.cs` (new) — builder guards.
- `tests/AdQueryOrchestrator.Tests/Unit/QueryControllerFollowUpProvenanceTests.cs` (new) — controller provenance guards.
- `tests/AdQueryOrchestrator.Tests/Browser/FollowUpContextTransmissionTests.cs` (new) — client transmission guard.
- `tests/AdQueryOrchestrator.Tests/Unit/CsvEnrichmentControllerTests.cs` — ctor call sites updated for the injected builder.

## Guard proof
- `FollowUpContextBuilderTests`, `QueryControllerFollowUpProvenanceTests`, `FollowUpContextTransmissionTests` — green at head `6c53a0d`.
- Non-vacuity (coder, 2026-07-27), each temporary edit reverted after:
  - Disabling `Forbid()` in the ownership check → `ExecuteQueryAsync_ForeignPreviousJobId_IsRejected` red (AcceptedResult, not ForbidResult).
  - Sourcing context from `request.Context` instead of the builder → `ExecuteQueryAsync_OwnCompletedPreviousJob_BuildsServerSideContext` red (prior-question material absent, client sentinel present).
  - Raising the value-slice take past the cap → `BuildFromPreviousTurn_ValueSlice_BoundedToSummaryRowCount` red (excluded rows present).
  - Disabling the client `previousJobId` wiring → `FollowUp_SendsPreviousJobId_AndNoClientContext` red.
- Full `scripts/verify.ps1` at head: 251 passed, 1 skipped, 0 warnings; publish smoke (401 + Swagger hidden in Production; Swagger JSON/UI in Development) + vuln audit clean (up from 240 — the new C2 unit + browser tests).

## Coder dispute (if any)
None.

## Known gaps
- The value slice reads only `Aggregation["grouped_counts"]`; other aggregation shapes (e.g. non-grouped scalar results) carry no value slice and fall back to question + plan summary. This is the minimal slice the plan calls for; other shapes are out of scope for C2.
- `MaxContextBytes` default remains 2000 (the transport maximum, C1's value); the plan's open item to size the operative value from a real assembled payload is still open and not addressed here.
- C3 (floating chat UI) will drive follow-ups through a dedicated surface; C2 wires the existing single-query flow to send `previousJobId`.

## Reviewer comments

_(pending dispatch)_
