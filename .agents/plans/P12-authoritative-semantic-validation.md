# P12 — Authoritative Semantic Plan Compilation

Status: **Reviewed — implementation is not authorized**

Owner approval: Pending

Implementation dependencies: P01, P04, P06, P08, P10, and P11 must supply their verified policy, budget, filter/template, traversal, projection, and aggregation contracts first. P12 must land before P13 and P14 make failures and queued commands authoritative. P09 is consumed indirectly through the executable operation contracts but P12 performs no LDAP work.

Review status: Advisory review accepted in round 2

## Problem

Plan acceptance is currently a sequence of mutable, overlapping checks rather than one semantic boundary. Controllers mutate plans through `PlanPreprocessor`, the executor validates again, the queued path runs only the security validator before passing the same mutable plan to an executor that validates again, and the explicit validation endpoint follows a different HMAC/preprocessing path.

The validators prove individual facts but do not produce an executable object that carries those proofs. Runtime code therefore resolves step names, operation strings, template references, projection joins, attributes, aggregation fields, and limits again. A later mutation or a caller that invokes the wrong service can bypass assumptions that another path checked.

Earlier plans deliberately add focused safety seams before consolidation: P04 validates CSV enrichment, P06 supplies shared budgets, P08 compiles bounded filters/templates, P10 validates traversal, and P11 compiles projection/aggregation. P12 must compose those seams without reimplementing them and make the resulting immutable object the only input execution accepts.

## Repository evidence

- `QueryController.ExecuteQuery` mutates a model-generated `DirectoryQueryPlan` with `PrepareForExecution`, then passes the raw mutable object to `IDirectoryPlanExecutor`.
- `DirectoryPlanExecutor.ExecutePlanAsync` calls its own `ValidatePlanAsync` before runtime execution.
- `DirectoryPlanExecutor.ValidatePlanAsync` performs description, step, numbering, positive-limit, security, and complexity checks.
- `PlanValidator.ValidateSecurityAsync` separately checks operations, allow-listed attributes, filter operators, prior-source names, select operation fields, projection filtering, traversal fields, and aggregation.
- `PlanValidator` mutates filter operators/attributes while validating.
- `PlanPreprocessor` recursively mutates filters, maps license aliases, merges requested result limits, and may clamp one selected step.
- The explicit `POST /api/query/validate` endpoint verifies optional HMAC before preprocessing, applies only custom mappings, and delegates to executor validation.
- The synchronous generated-plan path does not use that HMAC route, which is appropriate for an internally generated plan but is not represented by a typed origin.
- `QueryJobManager` mutates and stores `job.Plan`, calls only `ValidateSecurityAsync`, then passes the raw plan to an executor that validates it again.
- Runtime orders steps and repeatedly resolves string names and unsupported operations despite validation.
- Validation can accept a `group_by` token under the attribute allow-list while current aggregation later looks it up in a dictionary keyed by output display names. P11 owns the corrected compiled rule.
- `IPlanPreprocessor`, `IPlanValidator`, and executor validation expose no immutable validation artifact, policy version, compiled graph, or proof that execution used the checked object.
- P04 already plans a side-effect-free CSV validator and separate normalized CSV execution representation rather than mutating `CsvEnrichmentPlan`.

## Goals

1. Establish one side-effect-free semantic compiler for each executable plan shape.
2. Produce deeply immutable executable plans with no references to mutable input DTOs.
3. Make directory and CSV execution accept only successfully compiled plans.
4. Compose P04/P06/P08/P10/P11 policies and compiled components rather than duplicate them.
5. Normalize aliases, operators, defaults, and effective limits once in an explicit phase.
6. Validate the complete step graph, operation contracts, data dependencies, requested attributes, filters, traversal, projection, aggregation, and budgets before I/O.
7. Make synchronous, queued, retry, validation, and health paths use the same compiler and diagnostics.
8. Preserve signature verification over the unnormalized submitted plan and represent trusted internal origin explicitly.
9. Return bounded, stable, path-addressed diagnostics without raw values, query text, secrets, or exception messages.
10. Remove validation and raw-plan fallback from runtime hot paths.
11. Prove compilation performs no LDAP, provider, artifact, filesystem, job-state, or other external work.
12. Provide deterministic parity, mutation-isolation, graph, security, and architecture guards.

## Non-goals

- Do not change the provider request or fix `temperature`; P02 owns provider compatibility.
- Do not redefine the canonical attribute/operator policy; P04 owns the shared policy.
- Do not change P06 ceilings, exhaustion behavior, or execution-context accounting.
- Do not change P08 filter grammar, template correlation, expansion limits, or batching.
- Do not change P09 LDAP scheduling/timeouts or execute a connectivity probe.
- Do not change P10 identity, range retrieval, traversal ordering, revisit, depth, or node rules.
- Do not change P11 join, aggregation-key, projection-index, or row-source semantics.
- Do not define final public problem details or cancellation mapping; P13 consumes stable compilation causes.
- Do not redesign job lifecycle, persistence, or atomic transitions; P14 stores compiled commands later.
- Do not accept a partially valid plan or silently drop invalid steps, filters, columns, or groups.
- Do not add a mutable compiled-plan cache or persist executable plans across policy/configuration versions.
- Do not perform broad component decomposition; P21 may reorganize the settled compiler without changing its contract.

## Dependency ownership

### P04 — Canonical security and CSV semantics

P12 consumes one immutable directory-security policy snapshot: allowed object types, operations, attributes, filter operators, and CSV evaluator capabilities. The directory and CSV compilers use the same snapshot. P12 must not copy private allow-list sets into another configuration path.

P04's `ICsvEnrichmentPlanValidator` may become a compatibility adapter over `ICsvEnrichmentPlanCompiler`, but its fail-closed, before-AD, header-aware, flat-operator, normalized-execution, and atomic failure guards remain authoritative.

P18 is not a P12 implementation prerequisite. Before P18, the CSV compiler accepts an untrusted `CsvHeaderSchema` adapter constructed from the P04/P05-bounded `request.CsvHeaders` collection and validates header uniqueness, lengths, row-shape consistency, and referenced columns under P04/P05 rules. After P18 lands, its server parser becomes the sole producer of the same schema and the compatibility adapter is deleted. P12 must not claim pre-P18 client headers are trusted or parsed authoritatively.

### P06 — Limits and execution context

P12 derives one immutable `QueryBudgetLimits`/execution context from validated startup options, trusted server policy, the plan, and the caller's requested limit. Model output cannot raise a ceiling. A lower explicit request may narrow primary rows under P06 rules.

Compilation validates static positive/range relationships and embeds effective per-step scopes. Runtime counters remain authoritative for data-dependent work. Compilation never pre-consumes runtime counters.

### P08 — Filter and template compilation

P12 invokes P08's iterative structural analyzer and pure filter/template compiler. It preserves exact node/leaf/depth limits, logical-vs-leaf shape, placeholder grammar, prior-step/attribute checks, same-source correlation, cross-source Cartesian semantics, and zero-LDAP rejection. It stores P08's immutable compiled filter/template objects in executable operations.

### P10 — Traversal compilation

P12 invokes P10's semantic traversal validation and stores its effective traversal requests. It preserves canonical identity requirements, range-page options, target/source compatibility, recursive semantics, depth/node scope, revisit behavior, and fail-closed incomplete-range rules.

### P11 — Projection and aggregation compilation

P12 becomes the only producer of P11's immutable `CompiledProjectionProgram`. It preserves indexed join requirements, ambiguity rules, P07 cell schema, P06 counters, structured aggregation keys, and the approved `group_by` namespace. It does not retain P11's temporary validator alongside the authoritative compiler.

## Core boundary

Introduce separate raw transport DTOs and internal executable types:

```csharp
public interface IDirectoryPlanCompiler
{
    PlanCompilationResult<ExecutableDirectoryPlan> Compile(
        DirectoryQueryPlan rawPlan,
        DirectoryPlanCompilationContext context);
}

public interface ICsvEnrichmentPlanCompiler
{
    PlanCompilationResult<ExecutableCsvEnrichmentPlan> Compile(
        CsvEnrichmentPlan rawPlan,
        CsvCompilationContext context);
}
```

Compilation is synchronous/pure after validated policy snapshots are created at startup. If an implementation uses `ValueTask` for future policy providers, it still must perform no external request or blocking wait.

Executable types:

- Are sealed, deeply immutable, and constructed only by the compiler assembly boundary.
- Use immutable arrays/value objects, normalized enums, compiled filters, resolved step identifiers, effective limits, P10 traversal requests, and P11 projection programs.
- Contain no mutable `List`, `Dictionary`, raw `DirectoryFilter`, raw `DirectoryPlanStep`, or raw plan reference.
- Expose no public setters, serializer constructor, or general-purpose builder.
- Carry a compiler schema version and immutable policy snapshot version.
- Do not carry signatures, secrets, raw query text, raw model response, user identity, or physical paths.

`IDirectoryPlanExecutor` becomes:

```csharp
Task<PlanExecutionResult> ExecutePlanAsync(
    ExecutableDirectoryPlan plan,
    QueryExecutionContext context,
    IProgress<PlanProgressUpdate> progress,
    CancellationToken cancellationToken);
```

There is no production overload accepting `DirectoryQueryPlan`. Validation is not repeated by the executor, directory service, job manager, controller, artifact store, or exporters.

## Compilation context and origin

The caller creates an internal, non-model-bindable context:

```text
Origin                 InternalGenerated | ExternalValidation | BuiltInSelfTest
RequestedResultLimit   nullable positive server/caller limit
PolicySnapshot         P04 immutable policy and version
BudgetOptions          P06 validated options
SignatureState         NotApplicable | DisabledByConfiguration | Verified
CsvHeaders             CSV compiler only; validated schema input
```

Raw JSON cannot select `Origin`, `SignatureState`, policy version, limits, or trusted context.

- A model-generated plan inside the authenticated query workflow uses `InternalGenerated` plus `NotApplicable`; it is not required to forge an HMAC.
- The direct validation endpoint always uses `ExternalValidation`. With HMAC enabled, only `Verified` reaches the compiler. With HMAC disabled, it uses `DisabledByConfiguration`; this state grants no internal trust and still receives every semantic/security check.
- Health uses a fixed code-owned fixture with `BuiltInSelfTest` plus `NotApplicable`.
- No public endpoint accepts an internal origin enum or compiled bytes.

The valid pairs are `InternalGenerated + NotApplicable`, `BuiltInSelfTest + NotApplicable`, `ExternalValidation + Verified`, and `ExternalValidation + DisabledByConfiguration`. The compiler rejects all five other pairs: `ExternalValidation + NotApplicable`, `InternalGenerated + Verified`, `InternalGenerated + DisabledByConfiguration`, `BuiltInSelfTest + Verified`, and `BuiltInSelfTest + DisabledByConfiguration`. Origin and signature state are constructed after endpoint configuration/verification logic and are never derived from the raw DTO.

## Signature ordering

When `Security:EnableHmacValidation` is enabled for the direct validation endpoint:

1. Model bind into a raw DTO under existing/bounded request-body limits.
2. Verify the signature over the deterministic canonical serialization of that unnormalized DTO using the existing configured key and constant-time byte comparison.
3. On missing/invalid required signature, do not normalize or invoke the compiler. Preserve the current `401` validation shape with `Security.HmacValid=false` and the single fixed error `[plan_signature_invalid] Plan signature validation failed.`; do not include compiler diagnostics, plan contents, or the expected digest.
4. On success, create an `ExternalValidation + Verified` context and compile a private copy.

When HMAC validation is disabled, skip digest work explicitly and create `ExternalValidation + DisabledByConfiguration`. Do not call it verified, signed, or internal. Authorization and every semantic/security compiler phase remain identical to the signed path.

Normalization never changes what was signed. Internal generated plans bypass signature verification only through an internal typed workflow, not a header or raw-plan field.

P12 owns a narrow `IPlanSignatureVerifier` and deterministic raw-plan canonicalizer during this migration. It replaces the current ordinal string comparison with `CryptographicOperations.FixedTimeEquals` over decoded/computed digest bytes; this is an intentional security improvement protected by test 15, not claimed as behavior parity. P16 later owns key binding/secret configuration without changing verification order. P04 owns attribute/operator policy, not HMAC. P12 does not persist or log the key.

## Compilation phases

Compilation operates on a deep private copy and accumulates at most `MaxPlanDiagnostics` stable diagnostics. It stops a phase when later work would rely on an invalid fact, but may collect independent errors within the bounded phase.

### Phase 1 — Input and provenance

- Reject null collections/nodes despite nullable-deserializer edge cases.
- Enforce compiler schema version and typed origin/signature rules.
- Establish the immutable P04 policy and P06 option snapshots.
- Check raw collection counts with checked arithmetic before copying nested structures.
- Enforce a default `MaxPlanDiagnostics` of 64; the attempted 65th sets one terminal `diagnostics_truncated` flag without retaining another message.

### Phase 2 — Explicit normalization

On the private copy only:

- Trim names/attributes only where the approved existing or dependency contract says trimming is semantic.
- Normalize operation and filter-operator aliases through one table.
- Apply P04's canonical attribute aliases, including the existing license mapping.
- Apply documented default values.
- Derive P06's effective requested/plan limits without mutating a source step whose full input is required by downstream work or aggregation.
- Preserve original DTOs for signature/audit evidence; never mutate caller/model objects.

Unknown operations/operators/aliases are not coerced to a default. Normalization returns the token unchanged to the validation phase, which rejects it.

### Phase 3 — Bounded structure

- Invoke P08's iterative filter/tree analyzer before recursive compilation.
- Reject object-reference cycles in in-memory filter DTO graphs.
- Enforce total steps, attributes, filters, projection columns, group fields, string lengths, nested nodes, leaf predicates, group arity, and depth.
- Validate positive and maximum values with checked arithmetic.
- Reject duplicate names/columns case-insensitively before dictionary construction.

### Phase 4 — Step graph

Compile steps in numeric order and require:

- Sequential unique step numbers and nonblank unique names.
- Every source/template dependency names a strictly prior step.
- No forward reference or cycle.
- Operation-specific source presence/absence, target type, attributes, and options.
- Every referenced source attribute is intrinsic or requested/produced by that source contract.
- Every filter attribute is meaningful for the target and allowed by P04 policy.
- P08 template references resolve to a prior produced attribute.
- Static result/size/traversal scopes cannot raise P06 ceilings.

Replace operation strings with a closed executable union such as `CompiledSearch`, `CompiledLookup`, `CompiledExpandMembers`, and `CompiledExpandReports`. Runtime dispatch is exhaustive and has no unknown-operation branch reachable from a compiled object.

### Phase 5 — Traversal and directory operation requests

- Invoke P10's compiler for traversal operations.
- Embed validated P08 compiled filters and batch templates.
- Embed only requested/required allow-listed attributes.
- Derive immutable P09-schedulable request descriptors; do not submit them.
- Reject an operation whose static contract cannot be executed within configured finite timeout/work policies.

### Phase 6 — Projection and aggregation

- Require the row step and every column source step to exist.
- Require projection filters/attributes to be allowed and available.
- Compile P11 join requirements and index ceiling metadata.
- Compile ordered P07 schema/canonical defaults.
- Resolve every approved aggregation field exactly as P11 specifies.
- Preserve the one-candidate-per-row-step-record invariant.

### Phase 7 — Executable result

If and only if no error diagnostic exists:

- Deep-freeze compiled operations, projection, limits, and policy version.
- Compute a deterministic SHA-256 fingerprint over the non-secret versioned executable representation for equality/testing; do not use it as authenticity proof.
- Return the executable plus nonfatal bounded warnings.

On failure return diagnostics and no partial executable object.

## CSV compilation

The CSV compiler follows the same input/provenance, normalization, bounded-diagnostic, policy-snapshot, and immutable-result rules but composes P04/P05 contracts plus the P18 schema producer after it lands, rather than directory step graphs.

It must:

- Accept one validated bounded `CsvHeaderSchema` input. Before P18, construct it from P04/P05-bounded untrusted request headers and verify every row shape; after P18, accept only P18's server-parsed canonical schema.
- Resolve input/output identifiers case-insensitively under P04 rules.
- Reject missing/ambiguous columns.
- Validate lookup, retrieval, and filter attributes against the same P04 policy.
- Resolve only operator/evaluator pairs P04 declares executable.
- Produce P04's normalized immutable CSV execution plan.
- Perform zero directory calls and retain no input rows/cells.

The pre-P18 adapter is an explicit compatibility boundary, not a second CSV parser. It validates the bounded header list and row-key/width agreement needed by P04 compilation but does not reinterpret quoting, encoding, delimiters, or file bytes. P18 replaces ingestion and removes the adapter without changing the compiler contract.

Do not force directory and CSV plans into one generic union. They share policy, diagnostics, provenance, and compiler invariants but retain typed domain-specific executables.

## Diagnostics

Use immutable diagnostics:

```csharp
public sealed record PlanDiagnostic(
    string Code,
    PlanDiagnosticSeverity Severity,
    string Path,
    ImmutableArray<DiagnosticArgument> Arguments);
```

Requirements:

- Codes are stable lower snake case.
- Paths use a bounded JSON-pointer-like grammar with numeric indexes and fixed property names.
- Arguments are typed and bounded. They may include a fixed operation/type or numeric limit, but not filter values, DNs, query text, raw prompts, secrets, or arbitrary exception text.
- User-facing text is rendered from code/arguments outside the compiler.
- Duplicate diagnostics are de-duplicated by `(code,path)` while preserving deterministic phase/path order.
- Errors prevent an executable; warnings do not.
- P13 later maps codes to public problem details without parsing prose.

Representative codes:

```text
plan_description_required
plan_step_count_invalid
plan_step_number_invalid
plan_step_name_duplicate
plan_source_unknown
plan_source_not_prior
plan_operation_unsupported
plan_attribute_forbidden
plan_source_attribute_unavailable
plan_limit_invalid
plan_limit_exceeded
filter_shape_invalid
filter_complexity_exceeded
template_reference_invalid
traversal_contract_invalid
projection_invalid
aggregation_group_not_projected
aggregation_group_ambiguous
csv_column_missing
csv_operator_unsupported
diagnostics_truncated
```

Dependency-specific codes remain owned by their plans; P12 composes rather than renames them casually.

### Interim response renderer

P12 lands before P13 and therefore owns a temporary `IPlanDiagnosticRenderer` for the current string-based surfaces. It is not part of the pure compiler and uses a closed code-to-template table; it never formats arbitrary exception text or raw diagnostic values.

Rendering rules:

- `ValidationResponse.Errors` receives at most 64 entries in compiler order as `[code] fixed safe message (path)`. The path already contains only fixed property tokens and numeric indexes.
- `QueryResponse.Error` and the current queued-job failure string receive one bounded summary: `Plan validation failed: code at path` for the first error plus `(+N more)` when applicable.
- `ValidationResponse.Security` derives its existing booleans from fixed diagnostic categories; it does not copy values into `SecurityErrors` beyond the same rendered bounded list.
- Templates interpolate only compiler-approved enum labels and numeric limits/counts. Attribute names, step names, filter values, DNs, query text, prompts, signatures, and exception messages are omitted.
- Every validation-list entry is at most 512 UTF-16 code units and each query/job summary at most 2,048; truncation occurs only at a valid Unicode-scalar boundary and retains the stable code.
- Rendering failures return a fixed `[plan_validation_failed] Plan validation failed.` fallback and log only the diagnostic code.

P13 later replaces query/job strings with its problem-details/failure descriptors while preserving the compiler codes, paths, ordering, and retryability inputs. The explicit validation endpoint may retain its bounded list for compatibility, but it must use the same stable diagnostic DTOs internally.

## Entry-point migration

### Synchronous query

1. Generate a raw plan through P02.
2. Create internal compilation context with effective server/caller limit.
3. Compile once.
4. On failure, perform no LDAP/artifact work and use P12's bounded interim renderer until P13 maps the same codes.
5. Execute only the executable plan with the same P06 context.
6. Publish through P07.

### Queued query

Compile immediately after provider plan generation inside the job execution lease, replacing the current `ValidateSecurityAsync` step and before the current `Phase="executing"` progress update. Do not invent a new `QueryJob.Status`; P14 later owns lifecycle changes. Store the immutable executable/fingerprint in the in-memory command snapshot only for that attempt. The job manager does not mutate or validate it again.

### Explicit validation endpoint

Verify optional signature first, compile with no LDAP, and return bounded diagnostics/security summary. It does not return executable bytes or a token that a later request can execute. A later execution compiles its own internally generated raw plan against the current policy snapshot.

### CSV enrichment

Before P18, compile with the P04/P05-bounded compatibility `CsvHeaderSchema`; after P18, compile with its server-parsed schema. In either state, compilation precedes every P04/P05 lookup. Execute only the immutable CSV plan. Invalid compilation creates no artifact, cache item, or partial output.

### Health

P20's local self-test compiles one fixed valid and one fixed invalid fixture. It never calls the provider, LDAP, or artifact store and does not compile on every HTTP request.

## Removal and architecture rules

After migration:

- Delete `IPlanPreprocessor` and mutable preprocessing entry points.
- Replace `IPlanValidator` with the compiler, P04 policy, and P12-owned signature verifier; no second semantic validator remains. P16 later owns only signature key/configuration binding.
- Delete executor `ValidatePlanAsync` and every raw-plan execution overload.
- Delete controller/job direct calls to preprocessors/security validators.
- Runtime operation dispatch accepts only closed compiled operation types.
- No production type outside compiler/transport tests constructs an executable plan.
- No DI registration exposes a compiler implementation as a mutable singleton with per-request state.
- No compiled-plan cache survives a policy/configuration snapshot change.

## Telemetry

Use the P06 application meter:

```text
adquery.plan.compile
adquery.plan.compile_duration
adquery.plan.diagnostics
adquery.plan.shape
```

Allowed tags: plan kind (`directory`/`csv`/`self_test`), fixed origin, fixed outcome, diagnostic code, and bounded shape buckets. Never tag plan fingerprint, query, user, step/attribute names, values, paths, provider, raw diagnostic text, or signature state.

Log compilation failures once at the owning workflow boundary using diagnostic codes/counts only. Do not log the raw plan or values merely because compilation failed.

## Deterministic tests

Use fixed policy/options snapshots and fake dependencies that throw if called. No provider, LDAP, filesystem, artifact, IIS, job queue, random delay, or wall-clock sleep.

### Purity and immutability

1. Compilation invokes no provider, directory, artifact, cache, job, filesystem, or network fake.
2. Raw directory and CSV DTOs are byte-equivalent before/after compilation.
3. Mutating any raw nested list/filter after success cannot change the executable, fingerprint, diagnostics, or execution behavior.
4. Executable types expose no public mutable collection/setter/serializer construction path.
5. A failed result contains no executable object.
6. The same raw plan/context/policy produces the same executable fingerprint and ordered diagnostics.
7. A policy-version change requires a newly compiled executable and changes the fingerprint input.

### Provenance and signature

8. Signature verification observes the unnormalized DTO serialization.
9. An alias-normalized signature is not substituted for the signed raw plan.
10. Invalid/missing required signature stops before compiler invocation.
11. Raw JSON cannot select internal origin or `SignatureState`.
12. Internal-generated and self-test origins are constructible only by application code.
13. External validation with HMAC disabled compiles only as `ExternalValidation + DisabledByConfiguration`; the same request under enabled HMAC reaches compilation only as `ExternalValidation + Verified`.
14. Every invalid origin/signature-state pair is rejected before semantic compilation.
15. Signature comparison is constant-time and logs no key/digest/plan.

### Structural and graph semantics

16. Empty/duplicate/out-of-order steps and names fail at stable paths.
17. Unknown, forward, self, and cyclic dependencies fail before executable construction.
18. Operation-specific source/type/attribute/option matrices have table-driven valid/invalid fixtures.
19. A source/template/projection attribute not produced by the referenced step fails.
20. Static limits cannot exceed P06/P08/P10/P11 ceilings.
21. Checked count multiplication/diagnostic limits cannot overflow.
22. The exact diagnostic cap is retained; the next attempted diagnostic sets only `diagnostics_truncated`.

### Dependency composition

23. Every P08 filter/template valid/invalid golden fixture produces the same result through P12.
24. Every P10 traversal semantic fixture produces the same effective compiled request through P12.
25. Every P11 projection/group/join fixture produces the same compiled program through P12.
26. Every P04 CSV policy/operator/header fixture produces the same normalized CSV executable through P12.
27. The pre-P18 header adapter rejects duplicates/row-shape mismatch and yields the same compiler schema as equivalent P18 fixtures later.
28. P12 does not contain a duplicate filter parser, template expander, traversal validator, projection resolver, aggregation compiler, CSV parser, or evaluator capability table.

### Path parity and execution boundary

29. Synchronous, queued, validation, and self-test paths compile an equivalent directory fixture to identical executables under equivalent contexts.
30. Synchronous and queued invalid fixtures yield identical diagnostic codes/paths and make zero LDAP calls.
31. CSV invalid compilation makes zero LDAP/artifact/cache calls.
32. Validation strings and query/job summaries use only closed templates, stable codes, safe paths/numbers, and their exact character caps.
33. Renderer failure uses the fixed fallback and no raw diagnostic/exception content.
34. Executor public production surface accepts only `ExecutableDirectoryPlan`.
35. Directory runtime cannot dispatch an unsupported operation.
36. Mutating/storing a raw plan after compilation cannot influence execution.
37. Each accepted plan is compiled exactly once per execution attempt.
38. No controller, job manager, or executor invokes legacy validation/preprocessing.

## Red/green guard proof

For each test-bearing slice:

1. Add the focused guard and confirm current behavior fails where applicable.
2. Implement the smallest slice and confirm success.
3. Temporarily restore/bypass the protected behavior.
4. Confirm the guard fails for the intended reason.
5. Restore and run P01's canonical verification.
6. Commit only the restored slice.

Mandatory mutations:

- Mutate a raw nested filter after compilation; isolation guard fails if executable changes.
- Compile before signature verification; signature-order guard fails.
- Allow raw origin binding; provenance guard fails.
- Bypass P08/P10/P11/P04 composition with a local parser/table; dependency-ownership guard fails.
- Allow a forward/missing produced attribute; graph semantic guard fails.
- Return a partial executable with diagnostics; fail-closed result guard fails.
- Restore raw-plan executor overload; architecture guard fails.
- Revalidate inside executor/job manager; exactly-once guard fails.
- Change queued compilation order; zero-LDAP-on-invalid parity guard fails.
- Call LDAP from compiler; purity fake fails.

Leave no mutation in the worktree.

## Implementation slices

Each slice is one commit and must pass focused and canonical verification before the next.

### Slice 1 — Compilation results, diagnostics, and immutable shells

Commit intent: `feat: define executable plan boundary`

- Add bounded diagnostics, compilation contexts/origins/signature states, immutable result wrappers, the interim safe renderer, policy version, and executable type shells.
- Add reflection/mutation/purity/fingerprint tests.
- Do not migrate execution yet.

### Slice 2 — Canonical normalization and graph compiler

Commit intent: `feat: compile directory plan graphs`

- Replace mutable preprocessing with deep-copy normalization.
- Compile structure, graph, operations, produced attributes, and effective P06 limits.
- Compose P04 policy and P08 compiled filters/templates.
- Add graph, normalization, alias, limit, and diagnostic-bound tests.

### Slice 3 — Compose traversal and projection compilers

Commit intent: `feat: compile traversal and projection semantics`

- Compose P10 effective traversal requests and P11 compiled projection/aggregation.
- Remove P10/P11 temporary validation producers.
- Add full dependency golden/parity tests.

### Slice 4 — Compile CSV enrichment plans

Commit intent: `feat: compile csv enrichment semantics`

- Replace P04's validator/normalizer pair with the typed compiler adapter.
- Consume the pre-P18 P04/P05-bounded header-schema adapter and P04 shared policy/evaluator capabilities; keep the schema seam ready for P18 to replace its producer.
- Add no-row-retention and zero-I/O invalid-plan tests.

### Slice 5 — Migrate synchronous and validation paths

Commit intent: `refactor: execute compiled query plans`

- Compile generated sync plans once before executor/P07.
- Verify direct validation signatures before compilation.
- Render bounded safe compatibility strings from structured diagnostics until P13.
- Add path parity, signature order, and invalid-zero-I/O tests.

### Slice 6 — Migrate queued and CSV paths

Commit intent: `refactor: compile background query plans`

- Compile queued generated plans once before execution.
- Execute immutable directory/CSV objects only.
- Preserve current job transition timing until P14.
- Add sync/queued parity and mutation-race guards.

### Slice 7 — Enforce the executable-only architecture

Commit intent: `refactor: remove mutable plan execution paths`

- Change executor interfaces to executable-only input.
- Delete preprocessors, duplicate semantic validators, executor validation, raw overloads, and direct callers.
- Make operation dispatch exhaustive.
- Add architecture/reflection guards and run all dependency suites.

### Slice 8 — Health, metrics, and guidance

Commit intent: `docs: establish authoritative plan compilation`

- Wire P20's eventual local self-test seam without provider/LDAP work.
- Add low-cardinality metrics/logging.
- Document compiler versions, diagnostics, policy snapshots, and P13/P14 handoffs.

Verification:

```powershell
pwsh -NoLogo -NoProfile -File scripts/verify.ps1
```

## Acceptance criteria

- Directory and CSV raw DTOs are never execution inputs.
- Exactly one authoritative compiler produces each immutable executable shape.
- Compiler output is deeply immutable and detached from raw objects.
- Every entry point composes the same dependency policies and semantic rules.
- Optional HMAC is checked over the unnormalized submitted DTO before compilation.
- Raw input cannot assert trusted origin, signature status, policy, or limits.
- Invalid compilation performs zero provider/LDAP/artifact/cache/job/filesystem work and returns no executable.
- P08/P10/P11/P04 golden semantics remain unchanged through P12.
- P06 static limits are embedded without pre-consuming data-dependent counters.
- Execution accepts only immutable compiled operations/projection under one P06 context.
- Runtime contains no unsupported-operation or raw string-resolution fallback reachable from compiled input.
- Sync/queued/validation/self-test parity guards pass.
- CSV compilation uses the explicit pre-P18 bounded schema adapter, then P18's server-parsed schema when available, and retains no rows/cells.
- Diagnostics are stable, bounded, deterministic, sanitized, and P13-ready.
- Mutable preprocessing, duplicate validation, executor validation, and raw-plan overloads are removed.
- Canonical verification and all red/green proofs pass.
- Every implementation slice is committed separately.

## Rollback

Use new revert commits; do not rewrite history.

- Revert consumers before executable/compiler contracts they consume.
- Keep P04/P06/P08/P10/P11 guards even if P12 consolidation rolls back; do not restore unsafe behavior to make a revert compile.
- During rollback, a compatibility adapter may convert a successfully compiled executable back to the immediately prior bounded internal request only if no raw unvalidated object crosses into execution.
- Never restore queued-only validation or duplicate aggregation semantics.
- Never bypass HMAC merely because compiler migration is reverted.
- If a compiler schema must roll back, invalidate in-memory compiled objects; none are durable persisted data.
- Reverting diagnostics requires reverting their response/tests together and preserving P13 stable-code expectations if P13 has landed.

## Risks

- **Consolidation can accidentally change dependency semantics.** Reuse dependency fixtures and compare compiled artifacts, not only final rows.
- **Deep copies allocate.** Raw plans are strictly bounded; benchmark compilation and prefer immutable construction in one pass.
- **Immutable wrappers can hide mutable members.** Reflection and post-compile mutation tests traverse the full object graph.
- **A generic compiler can blur CSV/directory rules.** Share infrastructure/policy but retain typed domain compilers.
- **Signature normalization can invalidate trust.** Verify the raw deterministic DTO serialization before any transformation.
- **Internal origin can become a bypass.** Make contexts internal/non-bindable and test public metadata/JSON cannot construct them.
- **Diagnostic collection can become an amplification vector.** Cap count, string/argument sizes, depth, and deterministic work.
- **Static validation may overpromise runtime data facts.** Embed static proofs and leave data-dependent budgets/ambiguity to authoritative runtime checks.
- **Policy changes can stale compiled plans.** Keep them request/attempt scoped with a policy version; no global cache.
- **P12 could duplicate temporary compilers.** Delete P08/P10/P11/P04 temporary orchestration when composition lands; architecture guards forbid parallel owners.
- **Migration leaves a raw overload temporarily.** Keep each route migration guarded and remove the overload in a dedicated final enforcement slice before P12 is complete.
- **P14 later stores commands.** Store only immutable compiled objects/fingerprints and recompile each new attempt against current policy as P14 specifies.

## Open owner decisions

### Decision 1 — Executable-only boundary

Choose immutable compiler-only execution or retain runtime validation of mutable DTOs. Recommendation: executable-only; repeated validation cannot prove the object was not mutated or that every path ran the same checks.

Blocked until decided: Slices 5–7.

### Decision 2 — Normalization policy

Choose explicit canonicalization with fail-closed unknown tokens or permissive fallback rewriting. Recommendation: preserve documented existing aliases/defaults on a private copy, but reject every unknown operation/operator/field rather than silently substituting behavior.

Blocked until decided: Slice 2.

### Decision 3 — Direct-plan signature scope

Choose HMAC only for externally submitted validation plans or require it for internally generated plans too. Recommendation: verify external direct submissions before normalization; represent provider-generated plans as an internal origin because requiring the model to hold a signing key would invert the trust boundary.

Blocked until decided: Slice 5 signature/provenance wiring.

### Decision 4 — Diagnostic collection

Choose first-error failure or a bounded deterministic set. Recommendation: collect up to 64 independent path-addressed diagnostics, then one truncation marker; this keeps validation useful without permitting unbounded error work or output.

Blocked until decided: Slice 1 checked-in diagnostic bound.

## Advisory Review

### Round 1 — 2026-07-21T22:16:24Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Revisions required

- Added an explicit pre-P18 `CsvHeaderSchema` adapter over P04/P05-bounded untrusted headers and row-shape checks; P18 later replaces only the schema producer, so P12 no longer has an undeclared hard dependency.
- Added a closed, length-bounded interim diagnostic renderer for `ValidationResponse.Errors`, `QueryResponse.Error`, and queued failure strings until P13 maps the same stable codes.
- Replaced the incomplete signed-only origin with `ExternalValidation` plus explicit `Verified` or `DisabledByConfiguration` signature states and rejected every invalid origin/state pair.
- Clarified that P12 owns HMAC verification during migration while P04 owns only policy and P16 later owns key binding.
- Aligned queued timing with the current validating/executing progress phases instead of inventing a job status before P14.

### Round 2 — 2026-07-21T22:21:15Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Accepted

- Confirmed the pre-P18 schema adapter, later P18 producer replacement, interim safe renderer, and explicit external-disabled-HMAC state resolve all round 1 blockers.
- Confirmed compiler purity, trust provenance, immutable execution, static/runtime budget ownership, graph composition, migration ordering, and deterministic guards have no remaining material blocker.
- Applied optional clarity by specifying the pre-compiler HMAC failure response, enumerating every valid/invalid origin-state pair, and recording constant-time digest comparison as an intentional security change.

Record no more than three headless Claude review rounds. Each round must identify material findings, the resulting revision or retained disagreement, and the reviewer's final assessment.
