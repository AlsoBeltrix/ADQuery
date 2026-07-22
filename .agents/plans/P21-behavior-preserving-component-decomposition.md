# P21 — Behavior-Preserving Component Decomposition

**Status:** Advisory review complete — all three rounds accepted with no required changes. A later independent consistency audit repaired three contradictions; those post-round-3 repairs were not independently re-reviewed because the three-round limit was reached. Implementation remains unauthorized pending owner approval. P21 is last in landing order; all P01–P20 implementation decisions and contracts must settle before its owner decisions can be approved or code work can begin.

## Finding

The current application concentrates unrelated route adaptation, provider workflows, directory execution, browser state/view behavior, and dependency registration in a few files. Their size is evidence, not the defect by itself. The defect is that independent reasons to change share mutable state and concrete dependencies, so a localized behavior change can disturb unrelated API, wire, storage, error, cancellation, or performance behavior and a compile-only check will not detect the drift.

P02–P20 intentionally own the semantic and safety repairs inside these components. Many of those plans also introduce the pure compilers, stores, schedulers, state machines, and gateways required for testing. P21 runs only after those plans land and decomposes the remaining structural concentration around their final contracts. It must not redesign, reinterpret, or duplicate any behavior they own.

Source evidence was verified against commit `7e5a79eec7fc82740d0965616c91f9edb7eab676`. At that point the local clone was 24 commits ahead of and not behind reachable canonical `origin/master`; P19 and P20 were available as in-flight plan contracts and had not changed application code.

### Repository evidence

- `csharp/Controllers/QueryController.cs` is 1,955 lines, has nine constructor dependencies plus configuration-derived state, declares thirteen HTTP actions across synchronous execution, validation, downloads, health, client configuration, jobs, feedback, retry, and CSV enrichment, and also contains exporters, caching, identity handling, pattern detection, logging, aggregation projection, and wire DTOs.
- `QueryController.cs:23-25` derives the base route from `[controller]`. Renaming the class while splitting it would silently change `/api/query/*` unless every replacement uses the explicit fixed route established by the post-P20 endpoint manifest.
- `QueryController.cs:85-235` performs request adaptation, provider invocation, mutable plan preprocessing/validation/execution, storage/log path construction, result publication, caching, logging, and error mapping in one action. P07, P12, P13, and P16 replace those behaviors, but no earlier plan owns the residual route-family partition.
- `QueryController.cs:295-965` implements download authorization plus complete CSV/HTML/text/Excel generation and formatting inside the controller. P07 replaces artifact/export behavior; P21 must not recreate it when separating the resulting thin HTTP adapter.
- `QueryController.cs:986-1358` mixes job admission/status/preview/cancel/download, feedback, and alternate retry. P14, P17, and P19 replace those contracts; P21 may only partition any residual adapters after their final routes and DTOs land.
- `QueryController.cs:1359-1689` mixes CSV request handling, provider compilation, enrichment, output publication, sampling/pattern logic, and plaintext logging. P04/P05/P07/P12/P13/P16/P18 own the replacement workflow; P21 may only move its final route adapter.
- `csharp/Services/DirectoryPlanExecutor.cs` is 1,564 lines. It combines validation, step sequencing, mutable runtime state, four operation dispatch paths, person-search fallback, template parsing/expansion, filter normalization/evaluation, projection, aggregation, identity lookup, warnings, progress, and failure handling.
- `DirectoryPlanExecutor.cs:181-245` owns the execution loop and state lifetime, while `258-1492` also implements operation algorithms and result production. P08/P10/P11/P12 establish authoritative compilers/engines; P21 owns only the residual coordinator/dispatcher boundary after those implementations land.
- `csharp/Services/ClaudeService.cs` is 750 lines. `GenerateExecutionPlanAsync` at `71-201` and `GenerateCsvEnrichmentPlanAsync` at `235-348` independently combine prompt selection, request construction, HTTP transport, provider envelope handling, JSON extraction, deserialization, token/timing capture, and logging. Prompt builders occupy `349-690` and response extraction/log truncation occupy `691-729`.
- The reported deprecated-`temperature` failure is concrete evidence of request-path drift. P02 fixes that field through one typed builder and P20 removes generation-based health; P21 must reuse those settled seams and separate only the residual provider orchestration/prompt/codec responsibilities.
- `csharp/wwwroot/js/app.js` is 1,503 lines in one IIFE. It owns DOM discovery, global mutable state, CSV file parsing/upload, query admission, polling, terminal rendering, downloads, feedback/retry, theme, client configuration, user identity, error mapping, and accessibility state.
- `app.js:105-459` mixes CSV selection/parsing/network/progress/result rendering; `460-606` mixes configuration, identity, form enablement, and theme; `607-817` mixes job creation/polling/result retrieval; `820-1275` mixes aggregation/table/download/error rendering; and `1291-1488` exposes mutable feedback/retry functions on `window`.
- P18 removes client CSV parsing and P19 establishes native `job-api.js`, `job-poller.js`, and `job-view.js` modules. P21 must preserve those modules and split only the remaining feature orchestration and presentation still concentrated in `app.js`.
- `csharp/Program.cs:7-138` directly owns logging bootstrap, MVC serialization, OpenAPI, authentication/authorization, provider client construction, every application service lifetime, jobs, feedback, health, CORS, middleware order, endpoint order, startup diagnostics, host execution, and logger shutdown. P16/P20 own configuration/logging/health semantics; P21 may group registrations but must preserve their owners and exact ordering/lifetimes.
- `ActiveDirectoryService` (753 lines), `PlanValidator` (575), `QueryJobManager` (455), and `CsvEnrichmentService` (339) are also mixed today. P08–P12, P14, and P04/P05/P18 respectively own their semantic decomposition or replacement. P21 does not invent parallel versions of those components.
- The repository currently has no architecture test that prevents controllers from depending directly on provider, LDAP, filesystem, cache, SQLite, or raw configuration; no endpoint inventory that detects duplicate/missing routes after a class split; and no paired allocation protocol for refactor-only commits. P01 supplies the test/verification foundation, but each later plan owns its focused guards.

## Admitted Findings

Each admitted finding is one independently revertible implementation commit. A prerequisite that demonstrably closes one before P21 starts is recorded as the resolving commit and removes that finding from the P21 implementation queue through a reviewed plan update; no no-op commit is created. No P21 commit may address more than one still-open row.

| ID | Severity | Predicted observable failure |
| --- | --- | --- |
| P21-F0 | MEDIUM | Structural moves have no cross-surface baseline; a route, authorization policy, JSON field, P13 problem, DI lifetime, or source-generated serializer registration can disappear or duplicate while the application still builds. |
| P21-F1 | MEDIUM | Provider prompt, request, transport, codec, and use-case orchestration remain coupled; a change for one plan type can alter the other request bytes, classification, cancellation, logging, or allocation behavior. |
| P21-F2 | MEDIUM | Directory coordination and operation algorithms share one runtime object; moving or changing one step path can change execution order, the single P06 context, cancellation, progress, warning order, retained state, or P07 row production. |
| P21-F3 | MEDIUM | Residual `/api/query` route families remain in one controller; class renaming, constructor/lifetime edits, or action movement can change routing/auth/serialization or expose infrastructure dependencies across unrelated endpoints. |
| P21-F4 | MEDIUM | After P18/P19, residual query/CSV/download/feedback actions in `app.js` still share composition state; a feature change can duplicate requests/listeners, bind an action to the wrong immutable job/feedback target, or bypass a bounded API adapter. |
| P21-F5 | MEDIUM | Residual result, error, shell, theme, and accessibility rendering remain interleaved with orchestration; a rendering change can mutate authoritative state, issue network work, duplicate DOM ownership, or regress P19 single-flight/accessibility behavior. |
| P21-F6 | MEDIUM | Final feature registration remains concentrated in `Program.cs`; a mechanical move can change lifetimes/order, construct duplicate singleton/hosted owners, duplicate logging/options registration, or reorder middleware/endpoints without a guard. |

## Desired Outcome

- The final HTTP surface is partitioned by stable route family into thin adapters with explicit `/api/query` routes and the same authorization, request, response, headers, status, cancellation, and P13 mapping as the post-P20 baseline.
- Provider plan generation retains one P02 transport/request authority while pure prompt composers, the typed codec, and use-case orchestration have single reasons to change.
- Directory execution is a small coordinator over the final P08/P10/P11/P12 components, with one explicit dispatcher and one request-owned execution state; it contains no copied filter, traversal, projection, aggregation, LDAP, or validation algorithm.
- The P18/P19 native browser graph has one composition entry point. API modules do no DOM work, view modules do no network/timer work, and feature controllers own only their feature state.
- The composition root expresses feature registration in dependency order without hiding the middleware/endpoint pipeline or creating a second configuration/options/logger authority.
- Public API/wire/storage/error behavior is byte- or field-for-field equivalent where the owning contract requires it. P21 introduces no migration mode, compatibility endpoint, storage conversion, or domain behavior change.
- Each slice is small enough to review, benchmark, revert, and commit independently. There is never a branch-wide rewrite followed by one late parity test.

## Scope

### Included

- A post-P01–P20 responsibility/coupling census and frozen pre-refactor behavior manifest.
- C# namespace/file moves, internal collaborators, thin application ports, and DI registration grouping needed to separate the named residual components.
- Explicit route-family controller partition after P07/P13/P14/P17/P18/P20 endpoints and DTOs settle.
- Residual provider façade/prompt/codec partition using P02/P13/P20 contracts.
- Residual compiled-plan execution coordination using P06/P08/P09/P10/P11/P12 contracts.
- Residual browser feature/view/shell modules after P18/P19, using P19's native-module and deterministic Node harness.
- Source architecture tests, API/DI/module manifests, characterization fixtures, focused mutation proofs, and paired per-component allocation evidence.
- Mechanical documentation updates that point to owning contracts and the final module graph.

### Excluded

- Any change to query, CSV, LDAP, traversal, projection, aggregation, provider, job, feedback, artifact, health, deployment, configuration, logging, security, cancellation, retry, limit, retention, or UX semantics owned by P02–P20.
- Any route rename, route version, endpoint alias, HTTP-method change, serializer-policy change, JSON/property/enum/nullability change, new problem code, or error-message reinterpretation.
- Any filesystem layout, SQLite schema/migration, artifact/spool/feedback format, path root, marker, hash, retention, lease, quota, ACL, or recovery change.
- New provider fallbacks, retries, parallelism, timers, queues, caches, batching, materialization, or feature flags.
- Replacing P09's sole ADSI adapter/scheduler, P12's compiler, P14's state machine, P07/P17/P18 stores, P19's poller, or P20's readiness publisher.
- A generic mediator/event bus, reflection-based handler discovery, service locator, plugin system, microservice split, repository-wide namespace rewrite, UI redesign, CSS redesign, or bundler/framework migration.
- Moving generated/published output under `csharp/publish`; generated artifacts remain outside source work.
- Opportunistic bug fixes discovered during extraction. Stop, record evidence, and route each behavior issue to its owning plan or a separately approved finding.

## Hard Landing Gate and Ownership Boundaries

P21 is the final implementation plan. “Reviewed” plans are not sufficient prerequisites: every applicable P01–P20 implementation slice must be landed, its owner decisions settled, its focused guards green, and its obsolete compatibility path removed. If the owner explicitly declines an earlier feature, that decision must define the final replacement/absence contract before P21 can baseline it. P21 never implements around an unresolved predecessor.

At P21 start, record one exact prerequisite commit and re-read every final plan plus its implementation evidence. The following ownership remains fixed:

| Plan | Contract P21 consumes and must not redefine |
| --- | --- |
| P01 | One solution, test projects, analyzers, JavaScript/PowerShell/Python stages as landed, and the sole canonical `scripts/verify.ps1` entry point. P21 adds tests/benchmarks to those facilities only. |
| P02 | `ClaudeOptions`, one typed messages request/builder/gateway, exact provider wire names, default sampling omission, model selection, and bounded provider-envelope adaptation. |
| P03 | Target framework, SDK pin, package graph, source-generation/OpenAPI compatibility, vulnerability gate, publish shape, and IIS runtime assumptions. P21 adds no dependency unless separately approved and audited. |
| P04 | Canonical directory security policy, CSV operator capability, validation-before-AD, explicit lookup outcomes, and no-publication-on-failure. |
| P05 | Sole CSV limits/options/counters, identifier/batch/correlation/reconstruction semantics, ambiguity, and atomic output overflow. |
| P06 | The single `QueryExecutionContext`, all work/deadline counters, causal exhaustion, accounting order, and no partial success. A decomposition cannot create a context per collaborator. |
| P07 | `IResultRowSource`, schema/cell representation, artifact store/descriptor/lease, prepare/publish/abort/remove ordering, exporters, formula safety, storage layout, and download behavior. |
| P08 | Filter analyzer, template parser/compiler, renderer, exact cost model, branch packer, stable diagnostics, and operation/accounting rules. |
| P09 | Sole process-wide LDAP scheduler, sole blocking ADSI adapter, timeouts/admission/cancellation/retirement, identity boundary, and failure codes. P21 must not split ADSI access across classes. |
| P10 | Directory traversal engine, canonical GUID identity, BFS ordering, ranged membership, duplicate/cycle semantics, budgets, and traversal failure integrity. Moving the engine cannot change them. |
| P11 | Compiled projection/indexes, structured aggregation, finalizable row source, canonical cells, one-pass pipeline, ambiguity rules, and allocation/call-count benchmarks. |
| P12 | Immutable directory/CSV compilers, compiled executable types, phases/provenance/signature order, diagnostics, executable-only boundary, and removal of mutable preprocess/validator paths. |
| P13 | Failure/cancellation descriptors and provenance, fixed registry/codes/arguments, retry disposition, problem-details serialization, job outcomes, redaction, and browser handoff. |
| P14 | Durable job state machine, queue/leases/idempotency/lineage/SID authorization, worker ownership, versioned public snapshots/ETags, private feedback receipts, retention/recovery, drain, and P07 completion ordering. |
| P15 | Deployment package/journal/lock, target IIS adapter, immutable side-by-side release transaction, exact-new-release rollback drain, probes, and rollback. P21 changes are published/deployed only through that mechanism. |
| P16 | One configuration catalog/options owner, `IDataPaths`, outer lease, production projection, secret sources, one logger/event schema, safe metrics, and startup validation. Composition moves call these registrations; they do not copy them. |
| P17 | Job-scoped feedback endpoint, consent/idempotency/event schemas, HMAC/key cohort, store/retention/analyzer, safe exports, and P14 receipt authority. |
| P18 | Multipart transport, `AdQueryCsvV1`, authoritative schema/input row source, P05 incremental limits, protected ingestion spool, cleanup-before-P07-publication, and browser `File`/`FormData` contract. |
| P19 | Native module/test baseline, `job-api.js`, `job-poller.js`, `job-view.js`, single-flight generation/state, ETag/version parsing, cadence, lifecycle, cancellation, terminal action isolation, and accessibility behavior. |
| P20 | Exact live/ready/deployment/diagnostics endpoints and authorization, immutable release/readiness state, fail-closed local-safety invalidation, probe loops, contributor projections, fixed schemas/reasons, endpoint order, and retired `/api/query/health`. P21 never routes health through an MVC query controller or splits/duplicates the singleton readiness publisher/fence wiring. |

P21 may depend on a final predecessor type through a narrower existing interface. It may relocate a class or introduce a thin application-use-case port only when the dependency rule below requires it. It may not copy an option, enum, DTO, failure code, serializer context, schema, state machine, storage abstraction, policy table, metric catalog, or algorithm in order to make a new component look self-contained.

## Owner Decisions Required

### P21-D1 — server adapter partition

**Recommendation:** Keep ASP.NET Core controllers for the existing query API and partition the final actions into explicit route-family classes: query execution/validation, job commands/status, result preview/download, CSV enrichment, feedback, and client configuration. Every class uses the literal post-P20 base route rather than `[controller]`; P20 health remains endpoint-mapped outside these controllers.

The cost is more small files and constructor registrations. The benefit is that action discovery, authorization, and use-case dependencies are local and a class rename cannot change the route. This decision blocks P21-F3.

### P21-D2 — abstraction threshold

**Recommendation:** Introduce interfaces only at an external-effect boundary, an independently owned cross-plan contract, or a use-case seam requiring a deterministic fake. Use internal sealed classes for pure one-consumer helpers. Forbid generic mediators, reflection discovery, service locators, pass-through repository wrappers, and an interface-per-class convention.

This keeps dependency inversion useful without adding dispatch, allocation, or navigation overhead. It blocks P21-F1, P21-F2, and P21-F6.

### P21-D3 — internal compatibility policy

**Recommendation:** Preserve every externally observable HTTP/browser/storage/error contract, but do not retain obsolete internal façades, forwarding overloads, duplicated DTOs, or dual production paths once all in-repository callers migrate in the same slice. Retain an existing interface only when a final predecessor explicitly owns it as a stable port.

Permanent compatibility wrappers would hide reconvergence and double the paths tests must cover. Removing internal types can require coordinated compile fixes inside one slice but has no supported external wire effect. This decision blocks all production slices.

### P21-D4 — enforceable component budgets

**Recommendation:** Commit one architecture manifest as the canonical owner of final component constraints: one route family per controller and no infrastructure dependency; provider façade contains no prompt literals/HTTP/JSON codec logic; directory coordinator contains no domain algorithm; `app.js` contains composition only; API modules contain no DOM; view modules contain no network/timer; `Program.cs` contains host pipeline calls rather than feature registrations. Add conservative file/action/dependency budgets derived from the post-P20 census and fail verification on regression.

The exact numbers are recorded once in the manifest after the prerequisite baseline, not duplicated in this plan. A count alone never justifies an extraction; boundary violations do. This blocks P21-F0 and completion guards.

### P21-D5 — allocation non-regression gate

**Recommendation:** For every production slice, compare its committed parent with an exact pre-commit candidate tree in isolated worktrees using the same pinned benchmark scenario. Investigate any median managed/retained allocation increase that is both greater than 5% and greater than 1 KiB per operation; the slice cannot land without removal or explicit owner acceptance. Deterministic operation-count/side-effect assertions remain hard CI gates; elapsed time is recorded, not gated.

This catches abstraction tax while avoiding false failures from tiny/noisy changes. JavaScript uses explicit-GC retained-heap evidence plus deterministic fetch/timer/listener/render counts. This decision blocks the performance protocol.

### P21-D6 — browser residual partition

**Recommendation:** Build on P19's native modules with no bundler or framework. Extract residual query/CSV/download/feedback feature controllers first, then result/error/page-shell/theme view adapters. Keep `app.js` as explicit construction and one-time listener wiring only; expose nothing on `window`.

This gives deterministic seams without changing the browser/tooling support decision already owned by P19. It blocks P21-F4 and P21-F5.

### P21-D7 — rollout unit

**Recommendation:** Keep every P21 commit buildable, testable, and behavior-equivalent, but promote only the complete P21 candidate through P15. Do not add a decomposition feature flag or operate old/new paths in parallel. Revert one failed slice with a new commit while retaining all earlier green slices.

The application is not in production, so there is no benefit in exposing intermediate source organization as a release. This blocks release guidance, not coding of earlier approved slices.

## Safety and Behavior-Preservation Invariants

1. P21 starts after P01–P20, never between their slices and never as a convenient home for their unresolved work.
2. The baseline is the exact landed prerequisite commit, not the source evidence commit quoted above.
3. The canonical verifier is green before the first P21 edit and after every slice.
4. Every open P21 finding maps to exactly one implementation commit; every implementation commit maps to exactly one open finding.
5. A prerequisite-resolved finding produces no P21 no-op commit. Update the plan mapping before implementation.
6. A newly discovered behavior defect stops the slice. P21 does not repair it under “refactor.”
7. No P21 slice changes an HTTP method, literal route, endpoint order, authorization policy, antiforgery/CORS behavior, consumes/produces metadata, status, content type, cache header, ETag, `Location`, `Retry-After`, or response size ceiling.
8. MVC controller splits use the explicit baseline route. `[controller]` never determines a public query route after P21.
9. Duplicate endpoints and ambiguous action selection are startup/test failures, never accepted compatibility.
10. Request binding, validation, multipart streaming, body limits, and model-state/problem behavior are unchanged.
11. JSON property names, casing, order where golden contracts require it, value kinds, enum spellings, null/omission rules, timestamps, source-generated contexts, and schema versions are unchanged.
12. P13 is the sole public error renderer. No new controller/module catches an exception to invent a string or infer control flow from prose/status alone.
13. Cancellation provenance and token identity pass through without replacement, omission, linked-token expansion, or catch/rethrow reclassification.
14. One request/job/CSV execution receives exactly the same single P06 context and tracker as before decomposition.
15. Directory step order, first-winner/failure order, progress cadence/order, warnings, row order, aggregation, and finalization are unchanged.
16. P08/P09/P10/P11/P12 components remain the only owners of their algorithms. P21 coordinators delegate; they do not fork logic.
17. Provider request bytes, headers, target URI, timeout/deadline, selected model, token limit, prompt bytes, response parsing, token usage, error classification, and safe events are identical for fixed fixtures.
18. P02 remains the only provider message builder/gateway. P21 introduces no second `HttpClient` or anonymous provider request.
19. P09 remains the only physical ADSI access/scheduler. P21 introduces no `DirectoryEntry`, `DirectorySearcher`, raw worker, semaphore, or `Task.Run` path.
20. P07/P14/P17/P18 storage roots, opaque identifiers, schemas, hashes, marker bytes, transactions, leases, cleanup, retention, quotas, and crash recovery are untouched.
21. No P21 component accepts or returns a physical path across HTTP/application boundaries.
22. DI lifetimes, keyed identity, hosted-service count/order, options validation order, singleton ownership, scope creation/disposal, and graceful stop ordering are preserved.
23. No existing singleton or hosted loop is registered or constructed twice during migration.
24. P16 remains the only raw-configuration/options/root/logger composition authority. Feature registration methods call it; they do not bind again.
25. P20 endpoint mapping and SPA fallback order remain exact. Its one readiness publisher/fence and the P07/P09/P14/P16/P17 plus host-stop invalidation connections retain exact singleton identity, ordering, and lifetime. No controller regains `/api/query/health`.
26. Browser modules preserve P19's one timer, one status request, generation ownership, terminal latch, immutable feedback target, action idempotency keys, and document lifecycle.
27. Browser API adapters never query or mutate the DOM. Browser view adapters never call `fetch`, create timers, generate idempotency keys, or own authoritative job/feedback state.
28. `app.js` constructs modules, owns one-time top-level event binding, and translates no protocol/domain state itself.
29. No browser module writes job/query/feedback/model/owner data to storage, globals, diagnostics, DOM attributes, or console.
30. Every asynchronous continuation preserves the same await/order/abort semantics; decomposition adds no fire-and-forget work.
31. Characterization fixtures describe observable contracts and point to predecessor-owned golden fixtures rather than copying their enumerations.
32. Architecture rules test dependency direction and ownership. They do not assert cosmetic namespace or line-count trivia without an owner-approved budget.
33. Every production slice has a structural red/green proof that fails when its old forbidden dependency/responsibility is restored, while behavior fixtures remain green on both sides.
34. Allocation comparison uses the immediately preceding committed slice as baseline, so one regression cannot hide inside a later aggregate improvement.
35. Deterministic call/enumeration/request/listener/render counts must be equal or lower. A performance improvement never permits semantic drift.
36. No benchmark output, temporary worktree, generated publish tree, test result, or heap dump is committed.
37. P21 source moves preserve copyright, analyzers, nullable behavior, serializer visibility, trimming/source-generation roots, and assembly metadata.
38. Each slice deletes its superseded internal path before commit; there is exactly one production implementation for the moved responsibility.
39. Each completed slice is committed before the next begins. No amend, squash, rebase, history rewrite, push, or deployment occurs without its separate authority.
40. Rollback uses new revert commits and never rolls back a predecessor behavior contract merely to make the old component shape compile.

## Post-Prerequisite Rebaseline and Admission

Before approving P21 decisions or writing implementation code:

1. Verify the worktree and record the exact P01–P20 landed commit.
2. Re-read final plans, decisions, accepted reviews, implementation notes, and architecture/golden fixtures. Resolve every naming mismatch in this plan to the landed type without changing semantics.
3. Run the one canonical verifier from the repository root and from any alternate location required by P01; stop on any failure.
4. Run P03's vulnerability/publish/startup checks and the non-credentialed P15 package/probe fixture. No live deployment occurs at this stage.
5. Generate the P21 route/wire/DI/module responsibility census described below into an untracked report. Do not derive public truth from source line count.
6. Classify every admitted finding as `Open`, `ResolvedByPrerequisite(<commit>)`, or `Contested`. A contested boundary returns to the owner. Update this plan before approval so the slice table contains only open findings.
7. Confirm no compatibility adapter scheduled for removal by P02–P20 remains. P21 does not preserve or reorganize a path that should already be deleted.
8. Record parent commit, target files, forbidden dependencies, focused tests, deterministic operation counters, and benchmark scenario for the first open finding.

Current concentration is routed as follows; the rebaseline validates the residual rather than overriding these owners:

| Current component | Predecessor transformation | P21 residual |
| --- | --- | --- |
| `QueryController` | P07 artifacts, P12 compiler, P13 problems, P14 jobs, P17 feedback, P18 CSV, P20 health removal | Partition only remaining thin query route adapters; delete the monolith without changing endpoint metadata. |
| `DirectoryPlanExecutor` / `DirectoryPlanRuntime` | P06 context, P08 filters/templates, P10 traversal, P11 projection/aggregation, P12 executable-only compiler | Small coordinator, explicit operation dispatcher, request-owned execution state, delegation to final engines. |
| `ClaudeService` | P02 options/request/gateway/error seam, P13 failure mapping, P20 health removal | Existing-interface façade over separate query/CSV prompt composers and shared typed codec/transport; no wire change. |
| `ActiveDirectoryService` | P08 renderer/packer, P09 sole blocking adapter/scheduler, P10 traversal gateway | No P21 split of ADSI. Retain the final P09 boundary; move files only if required by the directory-coordinator slice and architecture tests prove one adapter. |
| `PlanValidator` / `PlanPreprocessor` | P04 policy and P12 compiler delete/replace mutable authorities | No P21 compatibility façade. These must already be absent from production execution. |
| `QueryJobManager` / in-memory store/queue | P14 durable state machine and tracked workers | No P21 job algorithm or persistence changes. Consume P14 ports from the route/composition slices. |
| `CsvEnrichmentService` | P04/P05/P12/P18 validation, limits, lookup, spool, row source, publication | No semantic split. Consume the final workflow from the CSV route/browser slices. |
| `app.js` | P18 removes client parsing; P19 establishes job API/poller/view and native tooling | Extract remaining feature controllers, API adapters, result/shell views, and leave composition only. |
| `Program.cs` | P16 owns configuration/logging and P20 health mapping; every domain plan registers its final service | Group feature registrations without changing owner, lifetime, registration order, middleware, endpoint order, or bootstrap/fatal handling. |

A file above may already meet its P21 residual boundary after prerequisites. That is evidence of prerequisite resolution, not permission to manufacture another abstraction.

## Cross-Surface Characterization Baseline

P21-F0 lands before any production move and creates one post-P20 baseline package under the P01 test projects. It is a composition of predecessor-owned fixtures, not a second definition of their domain enumerations.

### HTTP endpoint manifest

Start the final application through `WebApplicationFactory` with the P01/P03 test-authentication and deterministic dependency fakes. Enumerate `EndpointDataSource` after startup and serialize a stable sorted manifest containing only observable routing metadata:

```text
literal route pattern
allowed HTTP methods including HEAD behavior
endpoint order/name where externally relevant
authorization policy names / AllowAnonymous
request content type and body-binding kind
declared response content types/status metadata
cache/ETag/no-store endpoint metadata
antiforgery/CORS metadata that differs from fallback defaults
```

The fixture includes P20's exact health/reservation endpoints, all final `/api/query` routes, `/api/user/info`, Swagger only in its final Development contract, and the SPA fallback. It fails on a missing, duplicate, ambiguous, token-derived, or extra compatibility route. Do not enumerate framework-internal endpoints or compare unstable delegate/type display strings.

For each route family, execute a minimal success, validation failure, authorization failure, P13 failure, cancellation/disconnect path where observable, and not-found/expired path using final predecessor fakes. Reuse their canonical JSON/problem/multipart/ETag fixtures by reference or shared helper. Record exact status, selected headers, content type, bounded body bytes/semantic JSON, and downstream call/side-effect order. Sensitive values are generated test canaries and must appear only where the owning public contract deliberately includes them.

### Application and dependency manifest

Build the service collection through the same production composition entry point and normalize only application-owned descriptors:

```text
service contract
implementation/factory identity category
lifetime
registration ordinal when order is semantic
hosted-service ordinal
options validator ordinal
singleton construction count from recording factories
```

Exclude framework-internal registrations whose versions may change independently. Include every P02/P06–P14/P16–P20 owner interface, serializer context, source-generated JSON context, hosted coordinator/worker, store, and readiness contributor. The fixture asserts exactly one root/logger/options/store/scheduler/state-machine/poller-equivalent owner where each plan requires one and catches captive scoped dependencies by building with scope validation.

### Execution trace fixtures

Use recording fakes to capture immutable event traces rather than private call stacks:

- provider: prompt bytes, typed request JSON bytes/properties, headers, URI, one transport call, model/limit, cancellation token identity, response result/failure and safe event sequence;
- directory: compiled step order/kind, one P06 context identity, physical operation count, state admission order, progress/warnings/failure, projection enumeration, finalization, and P07 row output;
- jobs/CSV/artifacts/feedback/health: consume their owner-provided traces unchanged and assert route adapters add no storage/provider/LDAP calls;
- browser: imported module graph, listener ownership, fetch/timer/abort/render event sequence, immutable interaction IDs/targets, accessible output, and no global/storage/console leakage.

Fixtures assert stable public/event values and causal order, not concrete private class names that P21 exists to change.

### Architecture manifest

Commit one P21-owned manifest under the architecture-test directory. It contains the approved dependency rules, route-family/action budgets, allowed component entry points, browser layer rules, and post-P21 source budgets selected by P21-D4. It points to predecessor fixtures/catalogs for route/error/storage/enum truth. One test reads the manifest, fails on duplicate keys/unknown rules/stale paths, and reports the exact violated edge or budget.

The manifest initially describes the pre-refactor violations as expected open findings so Slice 0 can land green. Each later slice removes exactly its finding from the expected-violation set. Temporarily leaving that finding present after the production extraction must fail because expected and observed structure disagree; temporarily restoring the old dependency must fail after the manifest row is removed. At completion the expected-violation set is empty.

## Target Dependency Direction

```text
Composition root
  ├── HTTP/browser adapters ──> application use-case ports
  ├── infrastructure adapters ──┐
  └── owner registrations       │
                                v
Application coordination ──> immutable domain contracts/engines
                                ^
Infrastructure implementations ─┘
```

- `Contracts` contains only final wire DTOs/source-generation declarations already owned by P13/P14/P17/P20 and request DTOs required by MVC. It contains no service, path, exception, or domain algorithm.
- `Controllers` may reference ASP.NET primitives, final wire contracts, P13's HTTP adapter, owner-subject creation, and one application port per action. They may not reference provider clients, LDAP, stores, SQLite, filesystem, caches, raw `IConfiguration`, domain implementation classes, or exporter/parser implementations.
- `Application` coordinates one use case and depends on immutable compiled/domain ports. It may not reference MVC, DOM, physical paths, ADSI, SQLite connections, `HttpClient`, or raw configuration.
- Pure compiler/policy/domain components remain where P04/P08/P10/P11/P12 place them and depend only inward.
- `Infrastructure` implements final P02/P07/P09/P14/P17/P18 ports. It never depends on controllers/browser code and never calls back into composition.
- `Composition` is the only layer allowed to reference concrete implementations across all layers. It performs explicit, ordered registration; it contains no request behavior or dynamic discovery.

Cycles are forbidden at project namespace and JavaScript module level. The architecture test derives edges from C# syntax/compiled metadata and JavaScript import declarations, normalizes them, and fails with a shortest cycle/forbidden edge. Do not enforce dependency direction with comments alone.

## Server Component Boundaries

### Provider generation façade — P21-F1

Preserve the final P02-owned public/application port used by P12/P14/P18. Suggested internal shapes may adapt to landed names:

```text
ClaudePlanGenerationFacade
  GenerateDirectoryPlanAsync(...) -> DirectoryPlanGenerationWorkflow
  GenerateCsvPlanAsync(...)       -> CsvPlanGenerationWorkflow

DirectoryPlanGenerationWorkflow
  DirectoryPlanPromptComposer (pure, sealed)
  P02 IClaudeMessagesGateway
  P02/P13 typed response codec/failure adapter

CsvPlanGenerationWorkflow
  CsvPlanPromptComposer (pure, sealed)
  the same P02 IClaudeMessagesGateway
  the same typed codec/failure adapter
```

If P02 already names or separates the gateway/codec, reuse it verbatim; do not wrap it again. Pure prompt composers receive immutable validated input and return the exact existing system/user content without reading options, files, clock, logger, or network. Use separate concrete composers because directory and CSV prompts change for different domain reasons; they need no interface unless an existing final contract requires one.

The façade may retain an existing stable interface for consumers, but each method delegates one workflow and contains no prompt text, anonymous request, JSON traversal, `HttpClient`, configuration access, log truncation, retry, or error classification. P20's probe calls the P02 gateway directly as its contract specifies; it never routes through plan generation again.

The extraction preserves exact request bytes and the timing/token/failure observation points. It must not parse response JSON twice, copy complete prompt/response buffers, or introduce a generic `Generate<T>` abstraction that erases the distinct P12/P18 compiled result types.

### Compiled directory execution coordinator — P21-F2

Preserve P12's final executable-only `IDirectoryPlanExecutor` (or landed equivalent). The target collaboration is:

```text
DirectoryPlanExecutor facade
  -> CompiledPlanExecutionCoordinator
       -> request-owned ExecutionState
       -> explicit CompiledStepDispatcher
            search          -> P08/P09 final search executor/gateway
            lookup          -> P09-backed final lookup executor
            expand members  -> P10 traversal engine
            expand reports  -> P10 traversal engine
       -> P11 finalizable projected row source / aggregation
```

The dispatcher is an explicit exhaustive switch over P12's closed compiled operation kind. No dictionary keyed by strings, reflection, DI enumeration, plugin registry, or service locator is allowed. Unsupported/unmapped values retain P12/P13's fixed fail-closed outcome.

`ExecutionState` is created once per execution, never registered in DI, and is the sole owner of step results/index handles, counters delegated to the one P06 context, progress phase, warnings, and finalization state. It exposes narrow methods needed by the coordinator/landed engines and never leaks a mutable dictionary/list to controllers or infrastructure. P21 moves existing state ownership; it does not change admission/deduplication/order.

The coordinator sequence is explicit: cancellation/provenance check, execute compiled steps in canonical order, stop/fail under the existing contract, create P11's finalizable row source, hand it to the final caller/P07 path, and dispose state. P21 adds no validation pass, result clone, catch-all, parallel step execution, or compatibility overload accepting raw plans.

### HTTP route adapters — P21-F3

Partition only the final actions that remain after predecessors. Suggested names adapt to landed route names, but the literal endpoint manifest is immutable:

```text
QueryExecutionController       query submission and explicit validation
QueryJobsController            P14 create/status/cancel/retry commands
QueryResultsController         P07-authorized preview/download adapters
CsvEnrichmentController        P18 multipart endpoint
QueryFeedbackController        P17 job-scoped feedback endpoint
QueryClientConfigurationController  bounded public client snapshot
```

Every replacement uses `[Route("api/query")]` or the exact literal final prefix, never `[controller]`. Keep P16's final named authorization policy at the same class/action scope; do not copy configured role names. An action performs only framework binding/validation, owner/cancellation/correlation adaptation, one application-port call, and final P13/P07 response adaptation. It creates no path/ID/domain context and performs no provider/LDAP/store/cache/export/parser/compiler work.

P16's no-raw-configuration guard is a prerequisite contract, including for the bounded client-configuration snapshot. The final client-config adapter consumes one narrow application projection assembled from landed immutable owner options: P16 remains the binding/validation authority, while P02 remains the authority for model routing identifiers and display values carried by the snapshot. The adapter neither retains the current `IConfiguration` read nor invents a P21 options type. If those landed sources cannot reproduce the final snapshot, classify the boundary as contested during rebaseline and return it to the owning predecessor/the owner before P21-F3.

Final P14/P17/P18/P20 implementations may already own one or more controller classes. Keep them; P21 does not churn a compliant adapter merely to match suggested filenames. Delete the old `QueryController` only after the endpoint manifest proves every residual action appears exactly once and the SPA/health terminal routes still win in the same order.

Wire DTOs move only when needed for controller independence. Preserve their namespaces if any compiled/public predecessor contract relies on them; otherwise place them under the final API contract namespace and update all source-generated contexts/callers in the same slice. Do not retain duplicate DTO definitions or add custom converters to mask drift.

## Browser Component Boundaries

P19's `job-api.js`, `job-poller.js`, and `job-view.js` remain authoritative and are never copied into P21 modules.

### Residual feature orchestration — P21-F4

After the P18/P19 rebaseline, extract only still-present responsibilities into a DAG such as:

```text
query-feature.js       form admission and composition with P19 job API/session
csv-api.js             P18 multipart request + bounded P13 response adapter
csv-feature.js         selected File/query interaction and one CSV action gate
result-actions.js      P07 preview/download action coordination, no rendering
feedback-feature.js    P17 interaction coordination not already owned by P19
```

An existing P19/P17 module wins over a suggested P21 file; never introduce two feedback, retry, admission, or job authorities. Feature controllers accept injected API/view ports and own only feature-local immutable state/action gates. They use P19's scheduler/session and P13 problem adapter where applicable. They never query arbitrary DOM selectors internally, expose a global, persist data, parse status prose, create an unbounded response, or build a route outside the fixed same-origin families.

### Residual presentation and page shell — P21-F5

Extract still-present presentation into modules such as:

```text
result-view.js         summary/table/aggregation and P07 terminal presentation
error-view.js          P13 fixed safe problem projection only
csv-view.js            selected-file/progress/result controls, no file read/network
page-shell-view.js     mode/form/busy/live-region ownership
theme.js               local non-sensitive theme preference only
client-bootstrap.js    bounded config + user-info initialization through injected API
app.js                 imports, object construction, DOM reference assembly, one-time wiring
```

Each DOM element has one owning view. Views render through `textContent`/safe element construction according to predecessor security tests, accept already validated immutable view models, and return user intents through callbacks. They do not own fetch, timer, abort, job version, ETag, retry, feedback target, storage artifact, or domain interpretation.

`app.js` contains no `fetch`, response parsing, CSV parsing, timer/backoff, result formatting, P13 branching, job state transition, or `window.*` assignment. Module initialization is idempotent under the final navigation/BFCache contract, and imports have no side effects beyond exported definitions until composition calls them.

## Composition Boundary — P21-F6

Keep host creation and the visible middleware/endpoint sequence in `Program.cs`. Group only service registration into explicit feature extensions, using final names where available:

```text
AddAdQueryConfigurationAndLogging   // P16 owner; exactly once
AddAdQueryProvider                  // P02/P13/P20 consumers
AddAdQueryDirectoryExecution        // P04/P06/P08–P12
AddAdQueryArtifactsAndCsv           // P05/P07/P18
AddAdQueryJobs                      // P14
AddAdQueryFeedback                  // P17
AddAdQueryOperationalHealth         // P20
```

An extension returns the same `IServiceCollection`, has no hidden `BuildServiceProvider`, starts no task, resolves no service, reads no raw value outside P16's catalog/bootstrap contract, and registers each owner once with the exact baseline lifetime/order. Feature methods may call narrower owner-supplied registration methods; they may not reimplement those registrations. Options validation remains eager in the same host phase.

`Program.cs` retains, visibly and in baseline order, logger/bootstrap failure handling, MVC/source-generated serialization, authentication/authorization, CORS/static files, middleware, P20 endpoint mapping, controller mapping, SPA fallback, start/run, and bounded logger flush. Do not hide the pipeline in a generic host-builder abstraction or scan assemblies for modules.

## Per-Component Performance and Allocation Protocol

P21-F0 adds a pinned, opt-in comparison runner under P01's existing scripts/benchmark project. It accepts one explicit committed baseline SHA, the slice's declared path allowlist, and one allow-listed P21 scenario. It requires the caller's `HEAD` to equal that baseline and rejects a moving branch name, unrelated tracked/staged/untracked/ignored source changes, paths outside the repository, and undeclared candidate files.

The runner creates two verified disposable worktrees at the baseline outside source output. It leaves one clean and materializes the candidate in the other by applying the baseline-to-caller binary tracked diff and copying only declared untracked files with containment and byte-hash checks. It stages only the disposable candidate worktree, verifies its changed-path set equals the declaration, and records `git write-tree` as the exact candidate tree ID. It creates no commit or ref and never changes the caller's index. Both worktrees build Release with the same SDK/configuration and run identical fixed inputs. Results live only beneath ignored `artifacts/p21/`; `finally` removes only the verified disposable worktrees and temporary patch/copy artifacts.

Every production slice pins its committed parent as baseline and its pre-commit candidate tree ID as candidate. Before the real commit, staging the declared slice in the caller must produce the exact measured tree ID; any difference invalidates the evidence and reruns the comparison. Benchmark output records SDK/runtime, scenario version, baseline commit, candidate tree, iterations, operations, allocated/retained bytes, and descriptive timing; it contains no query/identity/path/secret from real users.

| Finding | Deterministic hard checks | Paired allocation evidence |
| --- | --- | --- |
| P21-F1 provider | Exact prompts/request JSON/headers/URI; one gateway call; same token/cancellation/failure/event trace; no complete-buffer clone. | Directory and CSV prompt + request + response-codec scenarios; managed bytes/op and gen collections. |
| P21-F2 executor | Same compiled-step order, LDAP/traversal calls, P06 charges/context identity, enumerations, progress/warnings, row/aggregation output, disposal. | P11 small and maximum admitted projection scenarios plus mixed-step execution; managed bytes/op and peak retained rows/handles. |
| P21-F3 controllers | Exact endpoint/wire/auth manifest and one application call; zero provider/LDAP/storage/filesystem access from adapters. | Fixed success/problem/auth requests through `WebApplicationFactory`; request allocations and response bytes. |
| P21-F4 browser features | Same bounded fetch/abort/action counts, keys/targets, request bytes, session ownership, and zero duplicate listeners/timers. | Fixed 10,000 interaction cycles under pinned Node with explicit GC; retained heap median and created controller/request/view-model counters. |
| P21-F5 browser views | Same safe DOM/accessibility projection and render count; zero fetch/timer/storage/global writes. | Fixed result/error/theme render cycles with detached fixture DOM; retained heap median and node/listener counts. |
| P21-F6 composition | Exact normalized service descriptors/lifetimes/order, one construction per singleton/hosted owner, same middleware/endpoint trace, scope validation. | Cold host-build/start/stop scenario with fake dependencies; managed allocation and construction counts. |

CI does not gate wall-clock time, CPU scheduling, working-set snapshots, or a single heap reading. It gates deterministic operations and architecture. P21-D5 governs paired allocation. A candidate over threshold is investigated with retained-object/allocation traces; moving the allocation to another layer or suppressing the benchmark is not a fix.

## Target Source Layout

Exact names adapt to final predecessor names while preserving this ownership:

```text
csharp/
  Application/
    QueryExecution/                 # thin use-case coordination only if absent
    ProviderGeneration/             # directory/csv workflows + pure composers
    DirectoryExecution/             # coordinator, dispatcher, request state
  Controllers/
    QueryExecutionController.cs
    QueryJobsController.cs
    QueryResultsController.cs
    CsvEnrichmentController.cs
    QueryFeedbackController.cs
    QueryClientConfigurationController.cs
  Composition/
    AdQueryServiceCollectionExtensions.cs
  Contracts/                        # final predecessor wire contracts only
  ...                               # P04–P20 domain/infrastructure locations remain
  wwwroot/js/
    app.js
    job-api.js                      # P19
    job-poller.js                   # P19
    job-view.js                     # P19
    query-feature.js
    csv-api.js
    csv-feature.js
    result-actions.js
    feedback-feature.js             # only if not already final under P19/P17
    result-view.js
    error-view.js
    csv-view.js
    page-shell-view.js
    theme.js
    client-bootstrap.js
tests/
  AdQueryOrchestrator.Tests/
    Contracts/P21/
    Architecture/P21/
    Characterization/P21/
  browser/
    p21-*.test.js
benchmarks/                         # extend P06/P11 landed project; no duplicate
scripts/                            # one P01-integrated paired comparison adapter
```

Do not move final P07/P09/P14/P16/P17/P18/P20 infrastructure merely for directory symmetry. A smaller diff and stable owner location are more important than a visually uniform tree.

## Implementation Slices and Commits

At implementation start, renumber only if the reviewed rebaseline removed a prerequisite-resolved finding. Preserve finding IDs and one-to-one mapping. Each slice begins from a committed green parent, changes only its declared component/test files, runs focused and canonical verification, records structural mutation proof and paired performance evidence, and commits before the next begins.

### Slice 1 — P21-F0 cross-surface characterization and architecture gate

Commit intent: `test(architecture): freeze decomposition contracts`

- Add the stable endpoint, wire, service-descriptor, execution-trace, and browser-module fixtures over the exact P01–P20 baseline.
- Reuse predecessor golden serializers/problems/events by shared helper/reference; do not copy their enum or failure registries.
- Add the P21 architecture manifest with every still-open structural violation explicitly expected.
- Add syntax/metadata dependency analysis for C# and import/forbidden-global analysis for JavaScript. Fail on zero discovered C# or browser architecture cases.
- Extend the existing P06/P11 benchmark project and P01 script surface with the pinned paired-comparison runner; add no package when landed tooling suffices.
- Record the exact prerequisite SHA, final component census, open/resolved finding map, benchmark scenario versions, and fixture-generation command in the test artifact/readme, not `.agents/state.md`.

Guard proof: point the endpoint fixture theory and architecture theory separately at empty fixture/case sets and prove canonical verification fails on zero discovery. Then mutate one copy of the generated endpoint manifest to use the old `/api/query/health` route and mutate one normalized lifetime; each focused test must fail for the intended mismatch. Restore the exact baseline fixtures and verify green. No production source changes in this slice.

### Slice 2 — P21-F1 provider generation separation

Commit intent: `refactor(provider): separate plan generation responsibilities`

- First remove P21-F1 from the architecture manifest's expected violations and prove the focused architecture test is red against the baseline.
- Extract pure directory and CSV prompt composers from the landed provider façade.
- Extract/reuse the final P02 typed codec/gateway boundary; do not wrap an already separate P02 component.
- Move directory and CSV orchestration into separate internal workflows while retaining the final public/application provider port as a thin delegating façade if still required.
- Delete prompt/HTTP/JSON/config/error logic from the façade and delete every superseded helper/path.
- Preserve P20's direct probe-to-P02 gateway wiring.
- Run exact directory/CSV request, P13 classification/cancellation, P12/P18 result, and safe P16 event fixtures.
- Run the paired provider allocation scenarios against Slice 1.

Guard proof: temporarily place one prompt literal and one direct gateway/codec call back in the façade in separate mutations; the P21 dependency/responsibility rule must fail while wire fixtures remain unchanged. Temporarily make CSV use the directory composer; exact request/prompt fixtures must fail. Restore and run the canonical verifier.

### Slice 3 — P21-F2 compiled directory coordination separation

Commit intent: `refactor(directory): isolate compiled plan coordination`

- Remove P21-F2 from expected violations and prove the architecture test red.
- Extract one request-owned execution state and one explicit compiled-step dispatcher from the final executor.
- Make the executor façade delegate to the coordinator and accept only P12 executable contracts plus the one P06 context/progress/cancellation contract.
- Route search/lookup/traversal/projection exclusively to the landed P08–P11 components; move no algorithm and create no alternative interface when an owner port exists.
- Delete old runtime helpers, raw-plan overloads, duplicate operation switches, mutable exposed state, and compatibility normalization/validation.
- Prove exact step/operation/state/progress/warning/failure/finalization traces on success, empty, cancellation, P06 exhaustion, P09 failure, P10 traversal, P11 ambiguity, and P07 consumer-abort cases.
- Run paired mixed-step and maximum admitted P11 allocation scenarios against Slice 2.
- If the final executor census cannot make this one independently reviewable green commit, stop before production edits and amend the admitted findings/slices through review. Do not hide several coordinator findings beneath P21-F2.

Guard proof: temporarily create a new P06 context in one dispatched handler, enumerate one source twice, and bypass the dispatcher for one operation in separate mutations. Context-identity, operation/enumeration-count, and architecture tests must fail respectively. Restore and run canonical verification.

### Slice 4 — P21-F3 explicit query route-family adapters

Commit intent: `refactor(api): partition query route adapters`

- Remove P21-F3 from expected violations and prove the architecture/endpoint test red.
- Partition only remaining final actions into explicit route-family controllers, retaining already compliant predecessor controllers untouched.
- Replace token-derived query base routes with the exact literal baseline route on every moved action.
- Inject final application/P13/owner ports rather than provider, directory, store, cache, filesystem, configuration, exporter, parser, compiler implementation, or SQLite dependencies.
- Move a wire DTO/source-generated registration only when required, in the same commit, with exact golden parity and no duplicate definition.
- Delete the residual `QueryController` after endpoint startup proves exact one-to-one action coverage, including P20 reserved/fallback routes.
- Run complete route/wire/auth/problem/cancellation/ETag/multipart/download/feedback/config fixtures and downstream zero-infrastructure-call spies.
- Run paired representative MVC request allocation scenarios against Slice 3.
- If the final controller census cannot make this one independently reviewable green commit, stop before production edits and amend the admitted findings/slices through review. Do not hide several controller findings beneath P21-F3.

Guard proof: temporarily restore `[Route("api/[controller]")]` on one replacement, duplicate one action on the old class, and inject a final infrastructure store into one controller in separate mutations. Literal-route, duplicate-endpoint/startup, and dependency tests must fail. Restore and run canonical verification.

### Slice 5 — P21-F4 residual browser feature orchestration

Commit intent: `refactor(browser): separate residual feature orchestration`

- Remove P21-F4 from expected violations and prove the module-architecture test red.
- Extract only residual query/CSV/result-action/feedback orchestration not already owned by P18/P19/P17.
- Preserve P19 job modules, state machine, schedulers, keys, generation/terminal ownership, and immutable feedback target; reuse instead of wrapping them.
- Inject bounded API/view ports, DOM references/callbacks, abort scope, and feature-local immutable state explicitly.
- Remove corresponding request construction/action gates/global functions/state branches from `app.js` and delete every superseded helper.
- Add controlled-fetch/action/abort/response fixtures for P13/P14/P17/P18/P19/P07 paths and seeded privacy sentinels.
- Run paired fixed-cycle Node retained-heap plus deterministic operation-count scenarios against Slice 4.

Guard proof: temporarily let a feature module query the DOM, add a second submit listener, and route one feedback action through mutable current-job state in separate mutations. The dependency, listener-count, and immutable-target tests must fail. Restore and run canonical verification.

### Slice 6 — P21-F5 residual browser views and shell

Commit intent: `refactor(browser): isolate page and result views`

- Remove P21-F5 from expected violations and prove the module-architecture test red.
- Extract residual result/error/CSV/page-shell/theme/client-bootstrap presentation into single-owner view modules.
- Keep P19's `job-view.js` as the sole owner of job-monitor state and accessibility; compose with it rather than duplicate its elements or announcements.
- Make views accept validated view models and emit intents only. Remove network, timer, status/problem classification, job/feedback state, and request construction from views.
- Reduce `app.js` to imports, DOM reference assembly, dependency construction, one-time event wiring, and startup/disposal.
- Remove every `window.*` export and duplicate DOM/listener ownership.
- Run exact DOM/accessibility/safe-text/theme/config/error/result fixtures and module-cycle/side-effect/privacy guards.
- Run paired render-cycle retained-heap/node/listener scenarios against Slice 5.
- If the final view census cannot make this one independently reviewable green commit, stop before production edits and amend the admitted findings/slices through review. Do not hide several presentation findings beneath P21-F5.

Guard proof: temporarily call `fetch` from a view, install a timer from a view, and make one imported module register a listener at import time in separate mutations. Static and controlled-runtime tests must fail. Restore and run canonical verification.

### Slice 7 — P21-F6 explicit feature composition

Commit intent: `refactor(host): make feature composition explicit`

- Remove the final expected violation, P21-F6, and prove the architecture/service-manifest test red.
- Group final application registrations behind explicit feature extension methods without changing owner implementations, descriptors, lifetimes, registration order, validator order, serializer contexts, or hosted-service order.
- Leave the host bootstrap and visible middleware/P20 endpoint/controller/SPA sequence in `Program.cs`.
- Forbid `BuildServiceProvider`, runtime resolution, assembly scanning, reflection registration, hidden task start, raw option rebinding, or duplicate P16/P20 registration inside extensions.
- Delete obsolete registration blocks/helpers after descriptor parity succeeds; do not move deployment/package behavior.
- Run scope validation, descriptor/constructor/start-stop traces, endpoint manifest, P16 startup mutation suite, P14/P20 loop ownership, P20 singleton publisher/fence and every local invalidation connection, P03 publish/startup, and logger flush tests.
- Run paired cold host build/start/stop allocation scenarios against Slice 6.

Guard proof: temporarily change one scoped owner to singleton, register one hosted coordinator twice, and reorder P20 mapping after SPA fallback in separate mutations. Descriptor/scope, construction-count, and endpoint tests must fail. Restore, confirm the expected-violation set is empty, and run the full canonical verifier.

## Deterministic Verification Matrix

The default suite uses only final predecessor fakes, `WebApplicationFactory`, fake time, controlled streams/tasks/fetches, temporary P16 roots where an owner integration fixture requires them, and fixed IDs/seeds. It contacts no IIS, provider, LDAP, domain, deployment target, or production filesystem and uses no wall-clock sleep.

### Baseline and API

1. Every final route appears exactly once with exact method, literal route, endpoint order/name, authorization, binding, content metadata, and cache metadata.
2. P20 exact and terminal reservation routes precede SPA fallback; `/api/query/health` remains bodyless 404 and no MVC health action exists.
3. Query success/validation, job command/status/cancel/retry, result preview/download, CSV multipart, feedback, client config, and user-info fixtures retain exact status/headers/body semantics.
4. P13 problems retain exact schema/code/category/retry/safe detail/correlation/`Retry-After`; seeded exception/provider/LDAP/path/query values are absent.
5. P14 ETag/304/version/`Location`, P07 leases/content lengths, P18 streaming/body/media rules, P17 idempotency, and P20 no-store/size/authorization remain exact.
6. Unauthorized, forbidden, missing/non-owner, invalid, cancelled, expired, busy, capacity, and service-stopping paths invoke no forbidden downstream dependency.
7. MVC/source-generated contexts include every final DTO exactly once and serialize golden fixtures identically before/after controller movement.
8. Route/architecture/JavaScript theories fail canonical verification on zero discovered cases.

### Provider

9. Directory and CSV fixed inputs produce byte-identical system/user prompt content and provider request JSON, including omission of `temperature` by default.
10. Primary/alternate P14 model selection, token limits, endpoint, headers, timeout/deadline, and cancellation identity remain exact.
11. Each operation sends exactly one P02 gateway request and performs one bounded response decode; no second `HttpClient`, buffer clone, or serializer path exists.
12. Every P13 provider code/status/retry result and malformed envelope maps identically without raw body/message leakage.
13. P20 provider probing still uses the P02 gateway directly with its fixed prompt/eight-token cap and never invokes a generation workflow.
14. Pure prompt composers resolve without logger/config/network/filesystem and have no import/dependency on one another.
15. Façade source/IL contains no prompt literal, `HttpClient`, anonymous provider request, raw configuration, or JSON traversal.

### Directory execution

16. One P12 executable and one P06 context enter the façade; the identical context reaches every step component and P11 finalization.
17. Search, lookup, member traversal, and report traversal dispatch exactly once in canonical compiled order; unknown values fail through the exact existing code.
18. Empty/skipped/failed/cancelled steps preserve stop behavior, warnings, progress, failure precedence, P06 charges, and zero-partial result.
19. Step results/index handles enter and leave one request-owned state in the same order and are disposed exactly once.
20. P08 branch packing, P09 physical calls, P10 traversal ordering, and P11 source enumerations/counts match predecessor fixtures; the coordinator contains no copied algorithm.
21. Projection/aggregation/output rows, schema, order, warnings, preview/artifact handoff, and consumer cancellation are exact.
22. Raw plans, mutable preprocessing, validator compatibility overloads, string operation maps, reflection handlers, and alternate ADSI paths are absent.

### Controller partition

23. Each controller owns one approved route family and uses the exact literal prefix; renaming its CLR class in a test fixture cannot change routing.
24. Each action invokes one application port at most once and preserves request/cancellation/owner/correlation values exactly.
25. Controllers contain no provider, directory, artifact/spool/feedback/job store, cache, filesystem, SQLite, raw configuration, exporter, parser, compiler implementation, or physical path dependency.
26. Action filters/authorization/antiforgery/CORS and model validation execute in the same order as the baseline.
27. Moving DTOs does not change JSON metadata/source generation/OpenAPI or accept an old duplicate type.
28. No `QueryController` or `[Route("api/[controller]")]` query adapter remains after complete partition.

### Browser features and views

29. P19 retains one timer/status request/generation/terminal latch and exact lifecycle/backoff/version behavior across module extraction.
30. Query/CSV/result/feedback actions have one listener and one action gate; controlled promise reordering cannot let an obsolete feature update the current view.
31. P18 CSV sends the exact selected `File`/query `FormData`, never reads/parses content, and retains media/size guidance.
32. P17 feedback captures one immutable terminal target/key and never reads mutable current job/query/model/timing data.
33. API modules do no DOM/global/storage work; views do no fetch/timer/abort/protocol/status/error classification.
34. Every owned DOM element/listener has one module owner; initialization/disposal/BFCache behavior creates no duplicates.
35. Result/error/CSV/job/accessibility DOM fixtures retain exact safe text, focus, live-region, busy/control behavior and never use unsafe HTML.
36. `app.js` contains composition/one-time wiring only; no `window` export, network, parser, timer, renderer, or protocol branch remains.
37. Module graph is acyclic, imports are side-effect-free before explicit initialization, and seeded sensitive values enter no console/storage/DOM attribute/global.

### Composition and architecture

38. Normalized application service descriptors, lifetimes, order, serializer contexts, options validators, and hosted services match the Slice 1 baseline plus only declared P21 internal collaborators.
39. Scope validation succeeds; each singleton/hosted owner constructs once, scoped collaborators dispose once, and host stop joins the same loops in the same order.
40. Feature extensions do not build a provider, resolve a service, start a task, scan assemblies, bind raw configuration, or register an owner twice.
41. Middleware and endpoint trace remains HTTPS/static/routing/CORS/authentication/authorization/P20/controllers/fallback in the final predecessor order.
42. P16 logger/bootstrap/fatal/flush behavior and P20 readiness startup/stop publication remain exact; construction and transition traces prove one publisher/fence and one correctly ordered invalidation connection for each P07/P09/P14/P16/P17/host-stop source.
43. Dependency graph has no forbidden edge or cycle and the architecture manifest has zero expected violations.
44. Every target source budget is satisfied and every moved responsibility has one production implementation.
45. Paired benchmarks cover all six production findings, meet the approved allocation rule or carry explicit owner acceptance, and show equal/lower deterministic operation counts.

## Red-Green and Guard-Proof Protocol

For Slice 1, prove fixture discovery and manifest comparison can fail as described; characterization tests are expected to pass on the baseline because they freeze behavior rather than fix it.

For every production slice:

1. Record the committed parent SHA, exact finding, declared production/test files, and paired scenario.
2. Remove only that finding from the architecture manifest's expected-violation set before changing production source.
3. Run the focused architecture test and record the intended red result against the old structure.
4. Perform the smallest complete extraction, delete its old production path, and run focused behavior plus architecture tests green.
5. Apply each slice's mandatory temporary mutation without committing or rewriting history; confirm the named guard fails for the intended reason rather than compilation or unrelated fixture drift.
6. Restore the candidate, confirm a clean scoped diff, run focused tests and the complete canonical verifier, then use the runner to materialize the exact pre-commit candidate tree and run paired allocation evidence against the pinned parent.
7. If behavior fixtures differ, stop. A reviewer/owner determines whether the baseline was wrong or a separate domain change is needed; P21 does not approve the difference.
8. Stage exactly the declared slice, require the caller's `git write-tree` to equal the measured candidate tree ID, and commit exactly that finding. Verify no temporary mutation, disposable worktree, patch/copy artifact, benchmark output, publish tree, or test result remains before starting the next.

An architecture test that passes with the old responsibility restored is vacuous and must be fixed. A characterization test need not fail on a structure-only revert; its role is to prove behavior stayed equal while the architecture guard distinguishes the extraction.

## Verification

After P01 lands, the only required repository command is:

```powershell
pwsh -NoLogo -NoProfile -File scripts/verify.ps1
```

Run it before Slice 1, after every red mutation is restored, and after every completed slice. It must include the landed C# tests/analyzers/format/vulnerability gate, P15 Pester stage, P17 Python stage, and P19/P21 Node stage rather than invoking a partial replacement command.

For each production slice also run the P21 paired scenario through the P01-integrated adapter with the explicit parent SHA and measured pre-commit candidate tree ID. Record summarized counts/allocation verdict in implementation evidence; do not commit raw output.

After Slice 7:

1. Run P03's framework-dependent Release publish, startup/DI smoke, generated runtime/dependency inspection, vulnerability audit, and Development Swagger fixture.
2. Run P15's non-elevated fake deployment/package/manifest/probe/rollback suite, including P20 exact release response and P14 rollback-drain contracts.
3. Run architecture analysis over the published assembly/static files as well as source so stale duplicated controllers/modules cannot survive copy/publish rules.
4. Run the opt-in supported-browser smoke from P18/P19 against a local fake-backed host: query admission, polling/cancel, CSV upload, result/preview/download, feedback/retry, theme, accessibility, navigation/BFCache, and typed failures.
5. When separately authorized credentials and a production-matched staging host exist, publish/deploy the exact candidate through P15 and run P03 Windows Authentication, minimal provider, minimal LDAP, P20 live/ready/deployment/diagnostics, P14 drain, P07 download, P17 feedback, and P18 CSV smokes. Record any unrun credentialed step explicitly; never claim it from deterministic fakes.

P21 changes no dependency, but the full vulnerability audit remains mandatory because P03 makes zero vulnerable packages a repository invariant.

## Acceptance Criteria

- Every applicable P01–P20 behavior-owning implementation is landed before the recorded P21 baseline. An explicitly declined predecessor is admissible only when its final replacement/absence contract is settled, landed where code or guards are required, and included in that baseline; approval without implementation is not sufficient.
- The reviewed rebaseline classifies every P21 finding; each open finding has exactly one committed slice and no commit combines findings.
- Endpoint/wire/auth/error/storage/event/browser golden fixtures are unchanged except for explicitly versioned characterization metadata that records the prerequisite baseline commit and scenario version. No tracked fixture embeds the not-yet-created candidate commit or tree ID.
- No public route depends on a controller class token; no missing, duplicate, ambiguous, alias, or SPA-shadowed API/health endpoint exists.
- Controllers are thin route-family adapters and contain no forbidden infrastructure dependency.
- Provider generation uses one P02 gateway/codec and separate pure directory/CSV prompt workflows with exact request parity.
- Directory execution is executable-only coordination over P08–P12 components with one request state and one P06 context; no copied algorithm/ADSI path remains.
- P19 modules remain authoritative; residual browser features/views obey API/view/composition direction, `app.js` is composition-only, module graph is acyclic, and no globals are exposed.
- `Program.cs` retains visible host/pipeline order and final feature extensions preserve exact service descriptors, ownership, and lifecycle.
- The architecture manifest contains zero expected violations, target budgets pass, and every responsibility has one production path.
- Deterministic side-effect/operation counts are equal or lower for every slice; paired allocation evidence satisfies P21-D5 or carries explicit owner acceptance.
- Canonical verification, Release publish/startup, full predecessor contract suite, P15 fake deployment suite, and supported-browser smoke pass.
- Credentialed Windows/IIS/provider/LDAP staging checks pass for the exact candidate or are explicitly recorded as not run; production promotion remains separately authorized.
- No P21 implementation commit changes configuration keys/defaults, public schemas/codes, storage layouts, limits, timeouts, retries, retention, algorithms, or UX policy.
- Superseded monolith paths/helpers are deleted, no compatibility façade/dual route remains, and no generated/temp/benchmark output is committed.

## Rollback

Use new revert commits; do not rewrite history.

- Revert P21 slices in reverse dependency order when a later slice consumes an earlier extracted component: composition, browser views, browser features, controllers, directory coordinator, provider. Slice 1 characterization/architecture infrastructure may remain because it describes preserved behavior and will expose the restored violations as expected only after its manifest is deliberately reverted.
- Reverting P21-F6 restores explicit registrations and `Program.cs` wiring from its parent; it must not change a lifetime, disable a P16 validator, duplicate a logger, reorder P20 endpoints, or roll back a domain registration.
- Reverting P21-F5 restores residual rendering to the prior P19-native `app.js` shape before removing view modules. Never revert P19 to a classic IIFE or globals.
- Reverting P21-F4 restores feature orchestration to the prior native composition while retaining P18 server-owned CSV and P19 job/session behavior.
- Reverting P21-F3 restores the one residual controller atomically. Remove replacement actions in the same revert so no duplicate/ambiguous route interval exists; keep P20 health retired and every P07/P13/P14/P17/P18 contract.
- Reverting P21-F2 restores the prior compiled coordinator only. It cannot restore raw-plan execution, duplicate validation, old template/traversal/projection algorithms, or direct ADSI.
- Reverting P21-F1 restores the prior post-P02/post-P20 provider organization only. It cannot restore anonymous requests, duplicate `temperature`, generation-based health, raw provider errors, or old prompt/config paths.
- If rollback would require violating a predecessor invariant, stop with the application disabled/fail-closed at that route rather than resurrect an obsolete path.
- Run focused/canonical verification and the relevant paired scenario after every revert. Promote a rollback artifact only through P15 and its exact-release/drain transaction.

## Risks and Mitigations

- **Prerequisite plans may already change file/type names or close a finding.** The mandatory rebaseline classifies exact landed evidence and updates P21 before approval; it never guesses or adds wrappers around stale names.
- **A controller class split can alter tokenized routes, action discovery, filters, or serializer metadata.** Use literal routes, runtime endpoint manifests, full HTTP golden fixtures, source-generated-context checks, and duplicate/startup guards.
- **Moving a DTO can change reflection/source-generation accessibility or JSON/OpenAPI shape.** Prefer leaving final DTOs in their owning contract location; move only with byte/metadata parity in the same slice.
- **More interfaces can increase navigation and allocations without reducing coupling.** Apply P21-D2, use sealed pure helpers, ban generic dispatch/service location, and gate allocations per slice.
- **Splitting a stateful executor can accidentally create multiple contexts/state stores or reorder disposal.** Construct one non-DI request state, pass the same P06 context explicitly, and assert exact trace/identity/disposal.
- **Provider extraction can copy prompts/responses or drift request bytes.** Use exact byte fixtures and one shared P02 gateway/codec; benchmark allocation and forbid full-buffer clones.
- **DI grouping can hide duplicate singleton/hosted registration or captive scopes.** Normalize descriptors, count construction, validate scopes, exercise start/stop, and leave endpoint pipeline visible.
- **Logger categories may change when classes move.** P16 event IDs/templates/arguments are the contract. Route through its canonical safe event methods and treat any category/source-context change as explicit reviewed operational drift, not an incidental rename.
- **JavaScript modules can introduce cycles, eager import side effects, or duplicate listeners.** Enforce a DAG, no side effects before initialization, one element/listener owner, controlled lifecycle tests, and exact counters.
- **Heap/allocation measurements can be noisy.** Gate deterministic counts in CI, compare the same scenario in isolated worktrees, require both percentage and absolute thresholds, and record timing rather than gating it.
- **Golden fixtures can ossify private implementation.** Snapshot only observable metadata/causal traces and P21 dependency rules; never snapshot private class names/call stacks or duplicate predecessor registries.
- **A large residual controller/executor may still be too large for one safe commit after prerequisites.** Stop and amend P21 with separately admitted one-commit findings before implementation. Do not split it ad hoc or perform a big-bang commit under one broad finding.
- **Mechanical moves can produce large diffs that hide edits.** Use rename-aware review, compare method bodies/IL or syntax-normalized members, require zero semantic fixture drift, and keep one component per commit.
- **Intermediate commits may be green but are not operationally valuable.** Do not deploy them; P21-D7 promotes only the complete candidate while preserving per-slice revertability.

## Advisory Review

Use no more than three substantive headless Claude Code rounds with the currently configured model, maximum effort, structured JSON, `Read`/`Grep`/`Glob` only, strict empty MCP configuration, no session persistence, and no `--model` override. Each round records Claude Code version, transcript-reported exact model, effort/tool envelope, verdict, material findings, revisions or retained disagreements, and optional comments.

Round 1 reviews the complete plan and current/predecessor evidence. Later rounds review the repair delta plus the complete plan for adjacent regression. A crash, timeout, or non-parseable response does not count as a substantive round; identify and terminate only its P21-specific orphan before retrying.

If Round 3 requires changes, apply them, record every final revision as not re-reviewed, and do not run a fourth round. If Round 3 accepts, freeze the reviewed snapshot; retain optional comments only when applying them would create post-review churn without material benefit.

### Round 1 — 2026-07-22T00:33:59Z

**Reviewer:** Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort / structured JSON / `Read,Grep,Glob` / strict empty MCP / no persistence

**Verdict:** Accepted; no required changes

- Confirmed from repository evidence that P21 is genuinely last, delegates P02's deprecated-`temperature` repair and P20's health separation, and does not duplicate P01–P20 behavior authorities.
- Confirmed P21-F0–F6 map one-to-one to seven independently guarded/revertible slices, with a test-only baseline first and no hidden big-bang implementation.
- Confirmed the provider, compiled-directory, literal-route controller, browser feature/view, and composition seams are concrete; dependency inversion is limited by P21-D2 rather than applied per class.
- Confirmed the characterization, mutation, deterministic-count, paired-allocation, rollout, and rollback protocols preserve public/wire/auth/storage/error/cancellation/browser behavior and are implementable after the mandatory rebaseline.
- Offered three optional precision improvements. Corrected the source census from eleven to thirteen HTTP actions; placed the stop-and-replan rule directly in the two largest slices; and made the client-config adapter's dependency on P16's landed immutable options/projection explicit. These refinements proceed to Round 2 for adjacent-regression review.

### Round 2 — 2026-07-22T00:38:17Z

**Reviewer:** Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort / structured JSON / `Read,Grep,Glob` / strict empty MCP / no persistence

**Verdict:** Accepted; no required changes

- Confirmed all three Round 1 refinements against source: thirteen HTTP actions is exact, the slice-local stop rules return through reviewed re-admission rather than allowing dynamic scope, and the client-config boundary removes the current raw `IConfiguration` read without creating a P21 options authority.
- Confirmed P20 remains a landed hard prerequisite rather than a merely reviewed dependency and that the changes preserve one finding per independently green/revertible commit.
- Found no adjacent regression in F0–F6/slice/decision mapping, seams, invariants, performance protocol, mutation guards, rollout, or rollback.
- Offered two optional precision improvements. Clarified that the client snapshot carries P02-owned model routing values through P16-bound immutable options, and placed the same stop-and-reviewed-re-admission rule inside the residual executor slice. These refinements proceed to final Round 3.

### Round 3 — 2026-07-22T00:41:23Z

**Reviewer:** Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort / structured JSON / `Read,Grep,Glob` / strict empty MCP / no persistence

**Verdict:** Accepted; no required changes

- Re-verified the client-config source field by field: P16 owns binding/validation and query-default values, P02 owns model routing identifiers/display values, and the P21 adapter adds neither raw configuration nor a competing options authority.
- Confirmed the executor's new stop/re-admit rule matches the large controller/view guards, permits no arbitrary mid-implementation scope change, and preserves the F0–F6 to Slice 1–7 one-commit mapping.
- Reconfirmed the P21-last landing/rebaseline gate, all D1–D7 gates, P02/P18/P19/P20 ownership, exact behavior invariants, component seams, deterministic guard proofs, allocation protocol, rollout, and reverse rollback.
- Noted without requesting edits that smaller slices already inherit the global one-finding rule, and that F0 plus the exact-wire invariant already pins the anonymous client-config response shape. Applying either note would create post-review churn without a material gain.
- At the close of Round 3, no reviewer-requested repair was applied.

### Post-review independent consistency audit — 2026-07-22

This is not a fourth advisory-review round. An independent read-only audit after Round 3, followed by the main-agent consistency check, found and repaired three internal contradictions:

- Replaced the impossible parent-SHA/candidate-SHA-before-commit benchmark sequence with an exact disposable-worktree candidate tree. The runner creates no temporary commit/ref, leaves the caller index untouched, and the real staged tree must equal the measured tree before the one-finding commit lands.
- Restored the hard predecessor gate in acceptance criteria: an owner-approved but unimplemented plan is insufficient; every applicable predecessor implementation must land, while a declined predecessor requires a settled and baselined replacement/absence contract.
- Removed a self-referential fixture allowance that could be read as embedding the candidate commit before that commit exists. Characterization metadata may name only the already-committed prerequisite baseline and scenario version; candidate identity remains external measured evidence.

The audit also confirmed that P20's later local-readiness invalidation repair, landed as plan commit `68e224c`, creates no P21-owned behavior. P21's final exact-commit rebaseline and composition guards must freeze the implemented singleton publisher/fence and cross-owner invalidation wiring before any decomposition. These repairs were not independently re-reviewed because the three-round limit was already reached; no fourth round was run.
