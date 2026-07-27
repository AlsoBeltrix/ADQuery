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

(pending reviewer dispatch)
