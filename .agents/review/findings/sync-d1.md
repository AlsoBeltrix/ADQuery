# sync-d1: F01 SYNC-D1 — retire the unused synchronous `execute` endpoint

**Severity**: MEDIUM — the risk is not the removal itself but over-removal: deleting the sync `execute` action must not (a) also strip the shipped async `execute-async` route or a helper the async/download/csv-enrich paths still depend on, nor (b) leave a compile break. A wrong cut breaks the shipped query path. Server-side controller change; no auth, crypto, schema, migration, or serialization surface touched.
**Status**: Verified (awaiting owner-gated merge/push)
**Branch**: (none — committed directly to master, this repo's non-branch policy for F01 slices; reviewed post-hoc over a pinned SHA range because history rewrite is forbidden)
**Commit**: `6836c11` (refactor(api): retire unused synchronous execute endpoint (F01 SYNC-D1))

## Evidence
SYNC-D1 (`.agents/decisions.md:48`, Approved 2026-07-27, owner): the synchronous `POST api/query/execute` endpoint (`QueryController.ExecuteQuery`) is retired. The shipped browser uses only the async path — `csharp/wwwroot/js/app.js:284` posts to `./api/query/execute-async`; there is no `fetch` of `api/query/execute` anywhere in `wwwroot`. No in-repo caller: a repo-wide search for `ExecuteQuery(` / `api/query/execute` / `"execute"` finds only the sync action's own definition, docs, and plan references — every test targets `ExecuteQueryAsync`. The sync action also carried a latent aggregation gap (it computed but never returned aggregation), which the decision resolves by removal rather than repair.

Reviewed range `1448025..6836c11`. Diff (3 files):
- `csharp/Controllers/QueryController.cs` — removed the `ExecuteQuery` action (`[HttpPost("execute")]`, former `:94–243`) and the now-dead `QueryResponse` model (former `:1919–1973`), whose sole consumer was that action.
- `csharp/README.md` — architecture surface list updated from `/api/query/execute` to `/api/query/execute-async`.
- `tests/AdQueryOrchestrator.Tests/Unit/SyncExecuteEndpointRetiredTests.cs` (new) — the retirement + over-removal guard.

## Predicted observable failure
- **Route not retired.** If the sync `execute` action survived (or were reintroduced), `SyncExecuteRoute_IsNotMappedOnController` fails: reflection over `QueryController` finds an `[HttpPost]` whose template equals `execute`.
- **Over-removal of the shipped path.** If the async route were deleted alongside the sync one, `AsyncExecuteRoute_SurvivesRetirement` fails: no `[HttpPost]` template equals `execute-async`. This is the sentinel that the cut stayed scoped to the dead endpoint.
- A shared-helper over-cut (`GetSamAccountName`, `DetermineHeaders`, `GenerateFileContent`, `CloneDictionary`, `CacheQueryResult`, or the `QueryRequest`/`TokenUsage`/preprocessor/claude deps) would surface as a Release build break (warnings-as-errors) in `scripts/verify.ps1`.

## What
F01 SYNC-D1: retire the unused synchronous `POST api/query/execute` endpoint and its now-dead `QueryResponse` response model. The shipped browser drives every query through the async path (`execute-async` + job polling); nothing in-repo or shipped calls the sync endpoint, and it carried a never-returned aggregation gap. Removing it (rather than repairing the gap) closes the former F01 "GATE-2" per SYNC-D1.

## Approach
Deleted only the `ExecuteQuery` action and the `QueryResponse` class, whose sole consumer was that action (verified: `QueryResponse` appears nowhere else in `csharp/`). Every helper the action called is shared with the surviving async/download/csv-enrich paths (`GetSamAccountName`, `DetermineHeaders`, `GenerateFileContent`, `CloneDictionary`, `CacheQueryResult`) or is a constructor dependency still used elsewhere (`_claudeService.GenerateExecutionPlanAsync` via `QueryJobManager`, `_planPreprocessor.PrepareForExecution` via `QueryJobManager`), so none were removed. `TokenUsage`, `QueryRequest`, and the `validate`/`health`/`download`/`csv-enrich` actions are untouched. README's architecture table was corrected to name the async route.

## Files changed
- `csharp/Controllers/QueryController.cs` — removed the `ExecuteQuery` action and the `QueryResponse` model; shared helpers and all other actions untouched.
- `csharp/README.md` — surface list `/api/query/execute` → `/api/query/execute-async`.
- `tests/AdQueryOrchestrator.Tests/Unit/SyncExecuteEndpointRetiredTests.cs` (new) — retirement guard + async-survival over-removal sentinel.

## Guard proof
- `SyncExecuteEndpointRetiredTests.SyncExecuteRoute_IsNotMappedOnController` — asserts no `QueryController` `[HttpPost]` template equals `execute`. `.AsyncExecuteRoute_SurvivesRetirement` — asserts one equals `execute-async`.
- Non-vacuity (coder, 2026-07-27): with the `ExecuteQuery` action still present (pre-removal, at commit tree `1448025` + the new test), `SyncExecuteRoute_IsNotMappedOnController` was **red** — `Assert.DoesNotContain() Failure: Item found`, collection `["execute","validate","execute-async",…]`, found `"execute"` — while `AsyncExecuteRoute_SurvivesRetirement` passed. After removing the action, both pass. The over-removal sentinel's non-vacuity is symmetric by construction: deleting the `execute-async` action would flip `AsyncExecuteRoute_SurvivesRetirement` red.
- Full `scripts/verify.ps1` at head `6836c11`: 256 passed, 1 skipped, 0 warnings; publish smoke (401 + Swagger hidden in Production; Swagger JSON/UI in Development) + vuln audit clean (up from 254 — the two new SYNC-D1 guards).

## Coder dispute (if any)
None.

## Known gaps
- The guard asserts the route at the controller-attribute level (reflection over `[HttpPost]` templates), not by booting Kestrel and observing a 404. This is deterministic and needs no live backend; the attribute is what MVC routing binds, so its absence is the observable that the route is gone. The `AsyncExecuteRoute_SurvivesRetirement` sentinel guards the corresponding over-removal risk.
- `QueryResponse` removal is covered indirectly: it was the sole return type of the deleted action, so its deletion is proven safe by the clean warnings-as-errors Release build in verify (an orphaned reference would break the build), not by a dedicated test.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard (inline, session-only)`
— no escalation (T1 no sensitive-path match — the diff touches `csharp/Controllers/QueryController.cs`,
`csharp/README.md`, and a unit test, none matching the default sensitive-path globs; T2 severity
MEDIUM; T3 guard proof valid; first round so no T4/T5). Dispatch: codex CLI `--profile review`
(owner-authorized unsandboxed, standing 2026-07-27), headless one-shot `codex exec … --json
--output-last-message`. Transcript-sourced from the JSONL stream
(`artifacts/review/sync-d1-stream.jsonl`, `agent_message` item_84 + `turn.completed`) and the
`--output-last-message` file (`artifacts/review/sync-d1-verdict.txt`), confirmed byte-identical. No
owner-confirmed durable tier mapping exists on this machine (`"tiers": {}`), so the model+effort pair
is recorded inline/session-only, mirroring slices a–c3.

- **Reviewer harness + version**: codex-cli 0.145.0 on ASHBIAMWEB1.
- **Reviewed head SHA**: `6836c11abd892a7dc1315a7375686f051d36ca8d` (== dispatched head).
- **Base SHA**: `14480253aa4d77c043899cc245ad5e6641dece5e` (== dispatched base).
- **guard_confirmed**: `true` — reviewer independently reproduced both guard proofs (revert→FAIL,
  restore→PASS) in its own detached `git worktree` at head: reintroducing a temporary
  `[HttpPost("execute")]` action made `SyncExecuteRoute_IsNotMappedOnController` fail, restore passed;
  renaming the `execute-async` template made `AsyncExecuteRoute_SurvivesRetirement` fail, restore
  passed. Canonical `scripts/verify.ps1` passed at head (Release 0 warnings/0 errors, 256 passed/1
  skipped, publish smoke + vuln audit clean).
- **Verdict**: **accepted**.
- **Timestamp**: 2026-07-27T23:22:18Z.

Orchestrator acceptance (computed by the coder, not the reviewer): exit 0; single schema-valid JSON
envelope; `verdict` in enum; `reviewed_sha` == dispatched head; `base_sha` == dispatched base;
`guard_confirmed` literally `true` → **accepted**. No parse miss, no re-prompt needed.

Comments (all confirmations, no defects):
- `csharp/Controllers/QueryController.cs:853` — `[HttpPost("execute-async")]` remains on
  `ExecuteQueryAsync`; no `[HttpPost("execute")]` route remains in `QueryController`.
- `tests/AdQueryOrchestrator.Tests/Unit/SyncExecuteEndpointRetiredTests.cs:22` — reintroducing a
  temporary `[HttpPost("execute")]` action made `SyncExecuteRoute_IsNotMappedOnController` fail;
  restoring `QueryController` made the two-test filter pass.
- `tests/AdQueryOrchestrator.Tests/Unit/SyncExecuteEndpointRetiredTests.cs:30` — renaming the
  `execute-async` template made `AsyncExecuteRoute_SurvivesRetirement` fail; restoring the template
  made the two-test filter pass.
- `csharp/Controllers/QueryController.cs:270` — `CacheQueryResult` remains defined and called by
  csv-enrich; `GetSamAccountName`, `DetermineHeaders`, `GenerateFileContent`, and `CloneDictionary`
  also remain referenced by the surviving download/async/csv paths.
- `csharp/wwwroot/js/app.js:284` — shipped browser posts to `./api/query/execute-async`; repo scan
  found no shipped caller of `api/query/execute` and no remaining `QueryResponse` symbol.
- `scripts/verify.ps1` — canonical verification passed at `6836c11`: Release build 0 warnings/0
  errors, 256 tests passed/1 skipped, publish smoke passed, vulnerability audit passed.

This record is committed as part of the verification history.
