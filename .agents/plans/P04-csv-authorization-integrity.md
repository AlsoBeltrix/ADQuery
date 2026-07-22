# P04 — CSV Enrichment Authorization and Failure Integrity

**Status:** Complete — shared authorization, fail-closed CSV validation, explicit lookup outcomes, atomic operational failure, and the no-publication controller gate are implemented and verified. Advisory review was accepted after 2 rounds.

**Finding:** CSV enrichment executes model-generated Active Directory attributes without the authorization policy used by normal directory plans. It also converts directory exceptions into “not found,” reports success after partial work, and lets the controller write and cache failed results.

**Depends on:** P01, Verification foundation and CI. P01 must establish the canonical automated test project and command before this plan is implemented.

**Related plans:** P05 owns CSV size and workload limits. P07 owns general artifact publication and caching. P12 owns broader semantic plan validation. P13 owns end-to-end cancellation and public error contracts. P16 owns portable storage paths and logging.

## Outcome

Every model-generated CSV enrichment plan is semantically validated against the same maximum user-attribute and filter-operator security policy as a normal directory plan before any Active Directory call occurs. CSV execution additionally requires that an allowed operator is meaningful and implemented for the flat CSV filter model. Invalid plans fail closed without silent rewriting. An empty directory result remains a legitimate “not found”; cancellation propagates; operational directory failures make the enrichment fail atomically and produce no result artifact or cache entry.

## Current evidence

- `csharp/Controllers/QueryController.cs:1393-1400` asks the model to generate a `CsvEnrichmentPlan`.
- `csharp/Controllers/QueryController.cs:1404-1412` validates only whether model generation succeeded and returned a non-null plan.
- `csharp/Controllers/QueryController.cs:1424-1430` sends that model-generated plan directly to `ICsvEnrichmentService.ExecuteAsync`; it does not invoke `IPlanValidator` or another semantic/security validator.
- `csharp/Services/CsvEnrichmentService.cs:66-76` uses a private match-attribute list. An unrecognized attribute is not rejected: the service mutates the untrusted plan and silently substitutes `sAMAccountName`.
- That private list includes `employeeID` at `CsvEnrichmentService.cs:69`, while the built-in user allow-list at `csharp/Security/PlanValidator.cs:391-413` does not. Custom allow-list files can further change the authoritative policy, so the two lists cannot remain independent.
- `csharp/Services/CsvEnrichmentService.cs:78-84` copies every model-generated `RetrieveAttributes` value into the directory request without consulting the configured user allow-list.
- `csharp/Services/CsvEnrichmentService.cs:86-93` similarly adds a model-generated filter attribute to the directory retrieval set without authorization.
- `csharp/Services/CsvEnrichmentService.cs:172-186` constructs an Active Directory request using the model-generated match attribute, filter, and retrieval attributes.
- Normal plan validation keeps allowed filter operators in a private set at `csharp/Security/PlanValidator.cs:26-38` and validates filter attributes/operators at `271-320`; CSV enrichment does not share that policy.
- Normal plan validation loads configured per-object-type allow-lists at `csharp/Security/PlanValidator.cs:322-385`, including file overrides. CSV enrichment cannot currently consume the resolved policy.
- `csharp/Services/CsvEnrichmentService.cs:188-194` correctly treats a successful search with no first result as “not found.”
- `csharp/Services/CsvEnrichmentService.cs:196-200` catches every exception, including cancellation and directory operational failures, logs it, and returns `null`. The caller therefore misclassifies dependency failures as ordinary missing users.
- `csharp/Services/CsvEnrichmentService.cs:149-150` unconditionally assigns accumulated output and marks the result successful after the row loop.
- `csharp/Controllers/QueryController.cs:1432-1451` allocates a request ID, renders bytes, writes the result file, and caches the result without checking `enrichmentResult.Success`.
- `csharp/Controllers/QueryController.cs:1458-1481` logs completion and returns HTTP `200`, even though the response includes the unchecked `Success` and `Errors` values.
- `csharp/Controllers/QueryController.cs:1483-1487` already has a cancellation boundary, but cancellation thrown inside `LookupUserAsync` is currently swallowed before it can reach that boundary.

## Security and integrity invariants

1. A model response is untrusted input. No attribute or operator reaches Active Directory solely because the model emitted it.
2. CSV and normal directory plans resolve user attributes from one canonical configured allow-list.
3. Match, retrieval, and filter attributes all require authorization, even when an attribute is used only in an LDAP predicate.
4. A CSV filter operator must be allowed by the canonical security policy and implemented by the CSV evaluator. The evaluator capability declaration is the single source used by both validation and execution and can only narrow the canonical policy.
5. Validation finishes before the first Active Directory call.
6. Invalid plans fail closed and are never silently rewritten to a different query.
7. An empty successful search is “not found.” A thrown directory operation is a failed enrichment. These states never share one sentinel.
8. Cancellation is never translated into “not found” or an operational failure. P13 may later refine its outward HTTP representation, but this plan must preserve propagation.
9. Under the recommended atomic policy, any operational lookup failure invalidates all accumulated rows.
10. A failed enrichment produces no downloadable result file, request/job identifier, or cache entry. A diagnostic failure log is allowed and is not a result artifact.
11. Error logs do not include the CSV match value or other row data.

## Owner decision

### P04-D1 — operational failure policy

**Recommendation:** Fail the entire enrichment on the first Active Directory operational error, discard accumulated rows, and publish no result file or cache entry. This avoids presenting a silently incomplete dataset as authoritative. Users may retry; an explicit partial-results mode can be designed later if there is a real need.

Alternative: continue after operational failures, mark affected rows, and publish partial results. This improves availability but requires a durable per-row error schema, unmistakable incomplete-result UI, download annotations, retry semantics, and acceptance of potentially misleading output. That larger product contract is not defined.

**Decision:** Approved on 2026-07-22. Fail atomically and publish nothing after an operational directory error. The canonical record is `.agents/decisions.md` under `P04-D1 — Fail CSV enrichment atomically`.

## Target design

### One canonical directory security policy

Extract the policy data currently private to `PlanValidator` into a read-only service, for example `IDirectorySecurityPolicy`. It must own:

- resolved allowed attributes by `DirectoryObjectType`;
- allowed filter operators;
- case-insensitive membership checks;
- loading configured attribute files and applying the existing fallback behavior.

`PlanValidator` must consume this service instead of retaining its own copied sets. The CSV validator must consume the same service for `DirectoryObjectType.User`. Do not introduce a second CSV-specific copy of either allow-list.

Preserve existing normal-plan behavior during extraction. In particular, retain current configuration file resolution, fallback defaults, case-insensitive comparisons, and logging. A characterization test must prove that normal plan validation still accepts and rejects representative attributes and operators identically after extraction.

The CSV filter grammar is a separate execution-capability constraint, not a broader security allow-list. Add one typed parser/evaluator contract shared by the CSV validator and `EvaluateFilter`. Initially it supports exactly `equals`, `not_equals`, `contains`, `not_contains`, `starts_with`, and `ends_with`. Canonically allowed but unsupported leaf operators such as `not_starts_with` and `not_ends_with` are rejected, and compound `and`/`or` operators are rejected because `CsvEnrichmentFilter` has no child-condition model. Remove the evaluator's unknown-operator fallback to equals; direct use with an unrecognized value must fail closed.

A startup or focused invariant test asserts that every evaluator capability is a member of the canonical operator policy. The per-plan validator still checks both sets, so either kind of configuration or code drift fails closed.

P12 may later consolidate validator architecture, but it must retain this shared policy and P04’s guards.

### Dedicated CSV semantic validation

Add `ICsvEnrichmentPlanValidator` with a side-effect-free validation method that accepts the plan and the validated CSV header collection. It must return a structured result containing `IsValid` and a bounded collection of actionable errors; it must not mutate the plan.

Validate, before directory access:

- `plan` exists;
- `MatchColumn` is non-empty and matches one supplied CSV header case-insensitively;
- `MatchAttribute` is non-empty and is allowed for `DirectoryObjectType.User`;
- every `RetrieveAttributes` entry is non-empty and allowed for `User`;
- duplicate retrieval attributes are normalized only in an execution copy, or rejected; never mutate the model response;
- when present, `Filter.Attribute` is non-empty and allowed for `User`;
- when present, `Filter.Operator` is in the canonical operator policy and the shared CSV evaluator capability set;
- when present, the filter has the value required by the supported operator semantics;
- `OutputMode` is exactly one supported value, currently `all` or `filtered`, using case-insensitive comparison;
- all attributes the execution service adds internally are also policy-authorized.

P05 owns numeric caps on row count, header count, retrieval count, and value lengths. P04 must not invent competing limits.

The validator must not “repair” an invalid match attribute by substituting `sAMAccountName`. Validation errors should identify the invalid field without reflecting CSV row values.

Preserve the current empty-identifier behavior: it performs no directory lookup and remains an output row only in `all` mode. This plan changes failure classification, not ordinary empty-input semantics.

### Validation boundary

`CsvEnrichmentService.ExecuteAsync` must invoke the CSV validator as its first operation, before constructing or sending any `DirectorySearchRequest`. This makes the security guarantee hold for all callers, not only the controller.

For a validation failure:

- return `Success = false`;
- return no data;
- identify the result as a validation failure through a small internal failure discriminator;
- include bounded validation messages;
- make zero calls to `IActiveDirectoryService`.

The controller maps this result to a client error before result publication. The exact eventual public error schema belongs to P13; P04 may use the current anonymous error response with HTTP `400` so long as invalid details are bounded and no raw row data is included.

### Attribute request construction

After successful validation, build a separate normalized execution representation. Do not mutate `CsvEnrichmentPlan`.

The attribute collection sent to Active Directory must be derived from the validated retrieval and filter attributes. If `matchAttribute` or `distinguishedName` remains an internal loaded property, document why it is required and ensure it passed the same user-attribute policy. Prefer removing implicitly loaded attributes that are not used in correlation or output.

Every attribute appearing in either `DirectorySearchRequest.Filters` or `DirectorySearchRequest.Attributes` must have passed the canonical user policy.

### Explicit lookup outcomes

Replace the nullable “found or anything went wrong” contract in `LookupUserAsync` with explicit outcomes or equivalent control flow:

- **Found:** the directory call completed and returned a record.
- **NotFound:** the directory call completed successfully and returned no record.
- **OperationalFailure:** the directory call threw a non-cancellation exception.
- **Canceled:** do not convert to a result; rethrow `OperationCanceledException`.

A simple implementation is to let empty results produce `NotFound`, rethrow cancellation, and convert only non-cancellation exceptions into an internal operational-failure result. Do not catch `Exception` and return `null`.

On an operational failure under the recommended policy:

- stop processing immediately;
- clear or withhold all accumulated output;
- set `Success = false`;
- set the internal failure kind to `DirectoryOperation`;
- return a bounded operator-safe message;
- log exception type, row index, and match attribute structurally;
- do not log `matchValue`, the row, or CSV contents.

P13 may later replace this local discriminator with a cross-application error contract. It must preserve the distinctions guarded here.

### Controller publication gate

Immediately after `ExecuteAsync`, branch on `enrichmentResult.Success` before:

- creating a request ID;
- computing preview rows or headers;
- generating CSV bytes;
- writing the output file;
- calling `CacheQueryResult`;
- logging a successful completion;
- returning HTTP `200`.

For validation failures, return HTTP `400`. For Active Directory operational failures, return a non-success dependency/server response; use HTTP `500` initially unless P13 establishes a canonical dependency status first. Do not expose raw exception messages.

The failure path may call `WriteCsvLog` with sanitized plan/failure metadata. It must pass no output path and must not create a downloadable result artifact.

To make the no-publication guarantee testable without writing to the repository’s hard-coded output location, place result-file writing behind a narrow injectable artifact-writer boundary if P07 has not already introduced one. Keep that abstraction limited to “write this completed result artifact”; P07 will own streaming, retention, and atomic publication later. Cache behavior is verified through the existing injected `IMemoryCache`.

Coordinate the temporary writer interface with P07's descriptor/store direction before implementation and preserve P04's no-publication guard when P07 replaces it. The controller's residual outer catch currently returns an unexpected exception message; P13 owns removing that raw-message response. P04 must not expose raw messages in its new validation or directory-failure branches.

## Implementation slices

Each slice addresses one issue, receives its own guard proof, and is committed before the next begins.

### Slice 1 — extract the canonical directory security policy

Suggested commit: `refactor(security): share directory attribute policy`

Changes:

- extract the canonical attribute/operator security policy from `PlanValidator`;
- route normal plan validation through it without changing accepted or rejected plans;
- add characterization tests for configured and fallback attributes and operators.

Do not add CSV behavior in this commit. Temporarily restoring the private normal-plan policy must make the shared-policy characterization/integration guard fail after the consumer is routed through the service.

### Slice 2 — authorize CSV plans before directory access

Suggested commit: `fix(csv): authorize enrichment plans before directory access`

Changes:

- add the side-effect-free CSV plan validator;
- add the one shared typed CSV operator parser/evaluator capability contract and remove default-to-equals behavior;
- call it at the entry to `CsvEnrichmentService.ExecuteAsync`;
- remove silent match-attribute substitution and plan mutation;
- construct directory requests only from validated attributes;
- add policy-parity and zero-directory-call tests.

Do not include operational failure or controller publication changes.

Guard proof:

1. Keep the new security tests in place and temporarily reverse only the CSV validator integration.
2. Run the focused tests and record that disallowed retrieval, match, or filter attributes reach the fake directory service, or that invalid plans no longer fail as expected.
3. Restore the validation integration.
4. Rerun the focused tests and record that they pass.
5. Run the canonical test suite and Release build.
6. Commit only the restored implementation.

### Slice 3 — distinguish lookup absence from failure

Suggested commit: `fix(csv): fail atomically on directory lookup errors`

Changes:

- replace the nullable lookup outcome;
- rethrow cancellation;
- stop and discard accumulated data on non-cancellation directory failure;
- add the minimal internal failure discriminator;
- remove match values from operational-failure logs;
- add found/not-found/failure/cancellation tests.

Do not change the application-wide cancellation or public error contract.

Guard proof:

1. Keep the new lookup-integrity tests and temporarily restore the broad catch-to-null behavior.
2. Run the focused tests and record that an exception is misreported as not found, partial output survives, or cancellation fails to propagate.
3. Restore the explicit outcome behavior.
4. Rerun the focused tests and record that they pass.
5. Run canonical verification.
6. Commit only the restored implementation.

### Slice 4 — block publication of failed enrichments

Suggested commit: `fix(csv): publish only successful enrichment results`

Changes:

- add the controller success gate immediately after execution;
- map validation and directory failures to non-success responses;
- ensure failure branches allocate no job/request ID;
- ensure result-file writer and cache are not invoked;
- retain only sanitized diagnostic logging;
- add controller-level no-publication tests.

Guard proof:

1. Keep the no-publication tests and temporarily remove the controller success gate.
2. Run the focused tests and record that the fake artifact writer or cache receives a failed result.
3. Restore the gate.
4. Rerun the focused tests and record that both remain untouched.
5. Run canonical verification.
6. Commit only the restored implementation.

## Regression tests

Use the deterministic test project established by P01. Use hand-written fakes or the project’s approved mocking library; no test may contact Active Directory, the model gateway, or the production output directory.

### Policy and validation

1. A configured allowed user attribute is accepted as `MatchAttribute`.
2. A match attribute absent from the canonical user allow-list is rejected with zero directory calls.
3. A disallowed `RetrieveAttributes` entry is rejected with zero directory calls.
4. A disallowed filter attribute is rejected with zero directory calls.
5. An unsupported filter operator is rejected with zero directory calls.
6. A canonically allowed but CSV-unsupported operator such as `not_ends_with` is rejected and never defaults to equals.
7. Compound `and` and `or` values are rejected because the CSV filter model has no conditions.
8. Direct evaluator use with an unknown operator fails closed rather than evaluating equality.
9. An unknown CSV match column is rejected with zero directory calls.
10. An unsupported output mode is rejected with zero directory calls.
11. Attribute comparisons are case-insensitive without changing the original plan.
12. Validation does not mutate `MatchAttribute`, retrieval attributes, filter, or output mode.
13. A custom configured user allow-list affects normal-plan and CSV-plan validation identically.
14. The policy extraction preserves representative existing normal-plan accept/reject behavior.
15. Every attribute captured in the fake `DirectorySearchRequest` is present in the canonical user allow-list.

Include a regression case for the existing mismatch: `employeeID` must be rejected when the resolved user policy does not contain it and accepted when a custom resolved policy explicitly contains it.

### Lookup integrity

16. A successful search returning one record produces a matched row.
17. A successful empty search produces the expected “Not found” row/warning in `all` mode and does not fail the operation.
18. An empty identifier performs no directory call and retains the existing `all`/`filtered` behavior.
19. A non-cancellation directory exception marks the overall result failed and returns no accumulated data.
20. If one prior row matched before a later directory exception, the final failed result contains no partial data.
21. Cancellation from the directory service propagates as `OperationCanceledException`; it is not returned as not found or operational failure.
22. Operational-failure logs do not contain the match value.

### Controller publication integrity

23. A validation failure returns a non-success response, generates no request ID, writes no result artifact, and adds no cache entry.
24. A directory operational failure returns a non-success response, generates no request ID, writes no result artifact, and adds no cache entry.
25. A cancellation reaches the existing controller cancellation branch and does not publish.
26. A successful result still writes and caches exactly once.

The fake artifact writer must throw if called in a failure test. The fake or instrumented cache must record all `Set` calls. Diagnostic log writing may be asserted separately; it is not a downloadable result artifact.

## Verification

After each slice, run:

```powershell
dotnet test <canonical test entry point established by P01> -c Release --no-restore
dotnet build csharp/AdQueryOrchestrator.csproj -c Release --nologo
```

If P01 records a different command in `.agents/repo-guidance.md`, use that canonical command rather than duplicating a new entry point.

No credentialed Active Directory test is required for deterministic completion. Before deployment, manually submit one valid small enrichment and one deliberately unauthorized attribute plan in a controlled environment. Confirm the latter makes no directory request and creates no result artifact.

## Acceptance criteria

- CSV match, retrieval, and filter attributes use the same resolved user allow-list as normal plans.
- CSV filter operators must satisfy the canonical security policy and the shared CSV evaluator capability contract; unsupported values never default to equality.
- Invalid model plans fail before the first Active Directory call.
- The service never silently changes an invalid match attribute to `sAMAccountName`.
- Plan validation does not mutate the model-generated plan.
- A successful empty search remains “not found.”
- A non-cancellation Active Directory exception cannot become “not found.”
- Cancellation propagates to the controller boundary; broader cancellation behavior remains owned by P13.
- Under the approved atomic policy, any operational lookup failure discards accumulated output.
- Failed enrichments create no request/job ID, result artifact, preview, or cache entry.
- Failed enrichments never return HTTP `200`.
- Diagnostic logs do not contain CSV match values.
- Each slice has recorded red/green guard proof.
- The canonical automated tests and Release build pass.
- P04-D1 was recorded and the plan status became `Approved` before implementation began.

## Implementation evidence

Completed on 2026-07-22 in four independently committed slices:

- `f784beb` — extracted the resolved directory attribute/operator policy and routed normal plan validation through the shared read-only service without changing its accepted policy.
- `9e82c8c` — added immutable CSV plan validation and typed filter capabilities, rejected unauthorized or unsupported plans before directory access, removed silent plan rewriting, and corrected the model prompt to `all|filtered`.
- `6c8357d` — separated found, not-found, cancellation, and directory failure; operational failure now stops immediately, withholds partial rows and counters, and logs no row identifiers or exception text.
- `5edc985` — placed the controller publication gate before result-ID allocation, preview generation, file writing, caching, completion logging, and HTTP success; validation maps to `400`, directory failure to fixed-text `500`, and cancellation remains `408`.

The canonical verifier passed at `5edc985` with SDK `10.0.302`, 147 tests, zero build warnings, successful Production and Development publish/startup checks, and zero direct or transitive vulnerability findings. Focused guards proved their behavior by temporarily removing the shared-policy route, CSV authorization, unknown-operator rejection, prompt contract, explicit lookup outcomes, cancellation checks, controller publication gate, single writer call, and cache write; each targeted test failed, every mutation was restored, and the complete verifier then passed.

No credentialed Active Directory request or production deployment was performed. The plan requires neither for deterministic completion; its controlled-environment checks remain pre-deployment verification work.

## Risks and controls

- **Extracting policy could alter normal-plan behavior.** Preserve existing resolution and fallback semantics and protect them with characterization tests before routing CSV validation through the service.
- **Custom allow-lists may omit internally added attributes.** Do not bypass policy. Remove unused implicit attributes or reject the plan with a clear configuration error.
- **Fail-fast can discard useful earlier matches.** This is intentional under the recommended integrity policy; partial output requires a separate explicit product contract.
- **Controller tests could touch the hard-coded production path.** Use an injected fake artifact writer. Never point tests at `E:\WWWOutput` or another machine-global location.
- **P07 may replace the temporary writer boundary.** It must preserve the no-publication-on-failure guard.
- **P12 may consolidate validation.** It must retain P04’s shared policy, fail-closed behavior, and before-AD ordering.
- **P13 may change response statuses or error models.** It must preserve the not-found, operational-failure, and cancellation distinctions.

## Non-goals

- CSV row, column, attribute-count, or value-length limits; P05 owns them.
- Batching or optimizing per-row directory lookups; P05 and P09 own that work.
- Streaming, retention, atomic file replacement, or cache redesign; P07 owns them.
- General plan-graph validation; P12 owns it.
- Application-wide cancellation and error-response standardization; P13 owns it.
- Portable output roots and general logging modernization; P16 owns them.
- A partial-results product mode.

## Advisory review

### Round 1 — 2026-07-21T20:16:34Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Revisions required

- Replaced the unsafe full-policy assumption with a canonical-security-policy intersection and one shared typed CSV evaluator capability contract; removed unknown-operator fallback semantics and added regression guards.
- Split behavior-preserving policy extraction from CSV authorization, preserved empty-identifier behavior, coordinated the temporary artifact boundary with P07, and assigned the residual outer raw-error path to P13.

### Round 2 — 2026-07-21T20:19:22Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Accepted

- Confirmed the operator-capability repair, atomic slices, ordinary-empty semantics, and cross-plan handoffs have no remaining implementation blocker.
- Made the validator's header input explicit and added a subset invariant guard between evaluator capabilities and canonical policy.

Run no more than three review rounds. Record each round's actionable findings and corresponding revisions before changing this status.
