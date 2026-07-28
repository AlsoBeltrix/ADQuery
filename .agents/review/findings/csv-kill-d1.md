# csv-kill-d1: CSV-KILL-D1 — remove the CSV enrichment feature entirely

**Severity**: MEDIUM — the risk is not the removal itself but over-removal and residue: deleting the enrichment feature must not (a) also strip the regular-query CSV *download* format (`BuildCsv`/`EscapeCsv`/`DetermineHeaders`/`GenerateFileContent` "csv" branch/`CacheQueryResult`), (b) leave a dangling reference/compile break under warnings-as-errors, or (c) silently drop a request-body-size protection without recording it. A wrong cut breaks the shipped download path or the build. Server-side controller/service/config change; no auth, crypto, schema, migration, or serialization surface touched.
**Status**: In progress (pending review)
**Branch**: (none — committed directly to master, this repo's non-branch policy for F01/CSV-KILL slices; reviewed post-hoc over a pinned SHA range because history rewrite is forbidden)
**Commit**: `c777a67` (feat(csv): remove the CSV enrichment feature entirely (CSV-KILL-D1))

## Evidence
CSV-KILL-D1 (`.agents/decisions.md:3`, Approved 2026-07-28, owner: "how much money do you think I want to invest in something that will never get used or seen? kill it."). The CSV enrichment feature (LLM generates an enrichment plan, backend does per-row AD lookups and merges) is removed entirely: the server endpoint `POST api/query/csv-enrich`, the enrichment service/filter-evaluator/plan-validator/request-validator, `CsvEnrichmentLimitsOptions` + its DI extension, the `CsvEnrichmentPlan`/`CsvEnrichmentPlanResponse` models, `GenerateCsvEnrichmentPlanAsync` on the LLM service, the `CsvEnrichment` appsettings section, and the P05 Slice 2 transport body caps (web.config `requestLimits` + Kestrel/IIS `MaxRequestBodySize`, sourced from the deleted CSV options). The entire `tests/AdQueryOrchestrator.Tests/Benchmarks/` capacity-evidence suite (P05 Slice 0) and all CSV-enrichment unit tests are deleted.

Reviewed range `92fb0a2..c777a67`. Diff: 44 files, +57/−6036. Product deletions (11 files) under `csharp/Configuration|Models|Security|Services`; test deletions (6 CSV unit tests + 12-file `Benchmarks/`); shared-file edits: `QueryController.cs` (ctor + CSV action/helpers/model removed), `Program.cs` (config/DI/body-cap removed), `IClaudeService.cs` + `ClaudeService.cs` (`GenerateCsvEnrichmentPlanAsync` + prompt builders removed), `appsettings.json` (CsvEnrichment section), `web.config` (requestLimits block), and 4 test files retargeted off the CSV path. Docs: decisions/F01/P05/state.

## Predicted observable failure
- **Download format collateral removal.** If `BuildCsv`/`EscapeCsv`/`DetermineHeaders`/`CacheQueryResult` or the `GenerateFileContent` "csv" branch were removed with the feature, the regular-query CSV download breaks — a Release build break (a caller referencing a deleted member) surfaces under warnings-as-errors in `scripts/verify.ps1`, or a download test fails.
- **Dangling reference / compile break.** Any remaining reference to a removed CSV type (service, options, model, validator, `GenerateCsvEnrichmentPlanAsync`) breaks the Release build (warnings-as-errors).
- **Guard vacuity / route residue.** If the `csv-enrich` route survived (or were reintroduced), `CsvUiParkingGuardTests.CsvEnrichEndpoint_IsNotMappedOnController` fails: reflection over `QueryController` finds an `[HttpPost]` whose template equals `csv-enrich`.
- **Unused-member break.** A now-orphaned test helper (e.g. the former `GetUserMessageContent`) left in place trips warnings-as-errors.

## What
CSV-KILL-D1: remove the CSV enrichment feature entirely per the owner's explicit kill instruction. The feature was never used and its P05 hardening scope is thereby moot. Keeps the separate regular-query CSV download format. Reverses the F01 "park CSV in UI only" non-goal and supersedes P05.

## Approach
Deleted the 11 enrichment-only product files and the 18 enrichment-only test files (6 unit + the self-contained 12-file `Benchmarks/` suite), then stripped enrichment references from the shared files: the `QueryController` constructor lost its four CSV dependencies and the `CsvEnrich` action + its CSV-only helpers (`CsvEnrichmentRejection`, `DetectColumnPatterns`, `DetectValuePattern`, `WriteCsvLog`, `BuildCsvLogPath`) and the `CsvEnrichmentRequest` model; `Program.cs` lost the CSV config/DI registrations and the body-cap block; `IClaudeService`/`ClaudeService` lost `GenerateCsvEnrichmentPlanAsync` and its two prompt builders; `appsettings.json` lost the `CsvEnrichment` section; `web.config` lost the P05 Slice 2 `requestLimits`. Four test files were retargeted from the CSV path to the surviving `GenerateExecutionPlanAsync` path (or had CSV-only cases/asserts dropped), and one now-orphaned helper (`GetUserMessageContent`) was removed to keep the warnings-as-errors build green. The CSV download format was left untouched.

## Files changed
See `git diff --stat 92fb0a2..c777a67` (44 files). Product: `csharp/{Configuration,Models,Security,Services}/CsvEnrichment*.cs`, `csharp/Services/ICsvEnrichmentFilterEvaluator.cs` (deleted); `csharp/Controllers/QueryController.cs`, `csharp/Program.cs`, `csharp/Services/IClaudeService.cs`, `csharp/Services/ClaudeService.cs`, `csharp/appsettings.json`, `csharp/web.config` (edited). Tests: `tests/.../Benchmarks/**` + `tests/.../Unit/CsvEnrichment*Tests.cs` (deleted); `CsvUiParkingGuardTests.cs`, `DirectorySecurityPolicyTests.cs`, `LlmProviderRequestTests.cs`, `LlmProviderErrorTests.cs`, `QueryControllerFollowUpProvenanceTests.cs` (edited). Docs: `.agents/decisions.md`, `.agents/plans/F01-conversational-query.md`, `.agents/plans/P05-csv-scale-limits.md`, `.agents/state.md`.

## Guard proof
- `CsvUiParkingGuardTests.CsvEnrichEndpoint_IsNotMappedOnController` — asserts no `QueryController` `[HttpPost]` template equals `csv-enrich` (flipped from the former Slice-A assertion that the route was still mapped).
- Non-vacuity (coder, 2026-07-28): with a temporary `[HttpPost("csv-enrich")] public ActionResult<object> TempGuardProbe() => Ok();` re-added to `QueryController`, the test filter ran **red** — `CsvEnrichEndpoint_IsNotMappedOnController [FAIL]` (`Assert.DoesNotContain() Failure`), the other two `CsvUiParkingGuardTests` passing. Removing the probe returned all three to green.
- Full `scripts/verify.ps1` at head `c777a67`: Release build 0 warnings/0 errors, 138 tests passed/0 skipped, publish smoke (401 + Swagger hidden in Production; Swagger JSON/UI in Development) + vulnerability audit clean.

## Coder dispute (if any)
None.

## Known gaps
- The P05 Slice 2 transport request-body cap died with the CSV options it was sourced from; the app now has no explicit request-body-size limit. This is a deliberate consequence of the removal (the cap existed only to bound CSV enrichment bodies), recorded in `.agents/state.md` as an open one-line y/n for the owner: leave the app with no explicit body cap, or restore an independent one. Not a defect in this change; flagged for the owner's ruling.
- The CSV download format's continued correctness is proven indirectly: it is exercised by the surviving download tests and the clean warnings-as-errors Release build (a collateral cut would break the build or those tests), not by a new dedicated test in this change.
- DATA-D1's CSV "never row cell values" clause described the now-removed enrichment path and is historical; no separate doc edit was made to DATA-D1 (it remains accurate about the download path, which never sent cell values to the model).

## Reviewer comments

(pending dispatch)
