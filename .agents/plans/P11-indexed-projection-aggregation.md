# P11 — Indexed Projection and Single-Pass Aggregation

Status: **Reviewed — implementation is not authorized**

Owner approval: Pending

Implementation dependencies: P01 verification foundation, P06 finite query-work budgets, and P07 result-row/artifact contracts must land first. P11 must land before P12 makes semantic compilation authoritative and before P14 consumes final projection/artifact results.

Review status: Advisory review accepted in round 2

## Problem

Projection currently resolves every cross-step column separately for every output candidate. `StepRuntimeState` indexes only `distinguishedName`; any other `match_on` calls `FirstOrDefault` over the complete source step. At P06's proposed finite ceilings, a valid 25,000-row source and 25 projection columns can still trigger hundreds of millions of record comparisons.

Aggregation then scans the complete materialized projected list again. It allocates a list and joined string for every group key, collapses null with the literal `"(empty)"`, allows delimiter collisions, formats values through ambient culture, and exposes an untyped dictionary. The current queued path also independently recomputes aggregation and may rewrite the primary rows, although P06's reviewed target removes both duplicate behaviors.

P06 makes the current algorithms finite and establishes the executor as the sole aggregation authority. P07 removes downstream copies and supplies a one-pass row source, but initially adapts P06's bounded materialized list. P11 must improve the algorithms and producer boundary without weakening P06 accounting or P07 atomic publication.

## Repository evidence

- `DirectoryPlanExecutor.Project` looks up `sourceStep` state inside the row/column loop and calls `ResolveProjectionSourceRecord` for every cross-step cell.
- `ResolveProjectionSourceRecord` calls `StepRuntimeState.FindByAttribute`.
- `StepRuntimeState` has one `distinguishedName` dictionary; every other attribute lookup scans `_records.FirstOrDefault(...)` and calls `GetString` repeatedly.
- The security validator permits up to 25 projection columns and up to five aggregation `group_by` fields.
- `Project` materializes every projected row before `ComputeAggregation` begins.
- `ComputeAggregation` uses LINQ `GroupBy`, allocates `List<string>` keys, joins them with `|`, and uses `value?.ToString() ?? "(empty)"`.
- `"a|b", "c"` and `"a", "b|c"` therefore produce the same composite key; null and the literal `"(empty)"` also collide.
- Formatting through `ToString()` is type- and culture-dependent. Arrays can group by their CLR type name rather than their contents.
- `QueryJobManager` contains a second equivalent grouping pass and a queued-only group-to-row transform. P06's reviewed plan already assigns their deletion to P06 and makes `PlanExecutionResult.Aggregation` authoritative.
- P06 bounds retained intermediate records, final primary rows, aggregation groups, active time, and other directory work; it explicitly leaves projection indexing, structured keys, typed aggregation, and the optimized pass to P11.
- P07 defines an ordered typed row schema, one-pass `IResultRowSource`, atomic staging, and a manifest that includes authoritative aggregation.

## Goals

1. Compile projection references and join requirements once before row production.
2. Replace repeated source scans with bounded indexes shared by equivalent joins.
3. Make runtime join ambiguity explicit and deterministic.
4. Normalize projected cells once into P07's canonical typed representation.
5. Replace delimiter strings with typed structured aggregation keys.
6. Fuse projection, primary-row selection, and aggregation updates into one candidate-row pass.
7. Stream admitted primary rows directly into P07 staging with bounded lookahead.
8. Finalize aggregation only after the row source completes successfully.
9. Preserve P06's separate intermediate-input, output-row, aggregation-group, and deadline semantics exactly once.
10. Preserve P06's executor-only aggregation authority and synchronous/queued parity.
11. Bound projection-index entries through the shared query-work tracker.
12. Provide deterministic correctness, complexity, allocation, cancellation, and publication guards.

## Non-goals

- Do not change LDAP request compilation, template expansion, scheduler behavior, or traversal; P08–P10 own those concerns.
- Do not add one-to-many projection. One retained row-step record still produces at most one candidate row.
- Do not restore queued aggregation or convert aggregation metadata into primary rows.
- Do not add sum, average, minimum, maximum, distinct count, or arbitrary expressions; the current contract is grouped count only.
- Do not define the final authoritative semantic compiler; P12 later absorbs P11's checked projection program.
- Do not change P06 hard-budget, intentional lower-limit, deadline, or no-partial semantics.
- Do not create another artifact store, row cache, or export representation outside P07.
- Do not infer missing join relationships or silently choose among ambiguous records.
- Do not retain a successful partial artifact after projection, aggregation, budget, cancellation, or completion failure.

## Accepted dependency boundaries

### P06

P11 consumes one shared per-query execution context and tracker. It preserves:

- At most one projection candidate per retained row-step record.
- Aggregation may inspect every filter-matched candidate even when a lower requested limit retains only a primary-row prefix.
- A lower caller/plan result limit is intentional successful primary-row truncation.
- Evidence of primary row `MaxOutputRows + 1` without a lower explicit limit is hard budget exhaustion.
- Every new aggregation bucket consumes one `aggregation_groups` unit before insertion.
- An existing bucket update consumes no group unit.
- Hard-budget failure discards primary rows and aggregation and publishes no artifact.
- `DirectoryPlanExecutor` is the sole aggregation charging and computation authority.
- Synchronous and queued callers reuse the same result and counters.

P11 adds `projection_index_entries` as another dimension on that same tracker. It does not introduce a parallel limiter.

### P07

P11 emits P07's ordered schema and canonical typed cells. P07 remains the only artifact writer and owns encoded cell, manifest, export, owner, retained-byte, and disk limits.

P07 initially adapts a materialized `PlanExecutionResult.Data`. P11 replaces that adapter with a single-use projected result source whose completion metadata becomes available only after enumeration. P07 enumerates it into staging, then consumes its completion before writing the manifest and commit marker. Any source or completion failure aborts staging.

### P12

P11 introduces a narrow checked `CompiledProjectionProgram` so execution no longer resolves arbitrary strings in hot loops. Until P12 lands, construct it only after all existing validation succeeds. P12 later becomes its sole producer and may rename the wrapper without changing P11 runtime semantics.

## Invariants

- Projection is compiled once per accepted plan, never per row.
- Equivalent `(source step, match_on attribute, comparison)` requirements share one index.
- Each source record contributes at most one entry to each required scalar join index.
- Every index entry is charged before insertion; exactly at the limit succeeds and the next entry fails without partial publication.
- Index building and row production check the P06 deadline and cancellation token at bounded intervals.
- A join lookup does not scan the source collection.
- A used ambiguous join key fails the query; record enumeration order never selects a winner.
- Projected column order and spelling come from one compiled ordered schema.
- Each candidate row is projected and normalized once.
- Aggregation consumes the same normalized cells that P07 would serialize.
- Aggregation keys are structured and type-aware; delimiters and display strings are never identity.
- New buckets consume the P06 group budget before insertion.
- Primary output rows consume P06 output capacity exactly once.
- Synchronous, queued, retry, preview, and export paths observe the same primary rows and aggregation.
- P07 does not write the manifest or publish an artifact until row-source completion succeeds.
- Failure or cancellation releases row-source state and P07 staging.
- No producer or consumer re-enumerates the projected source.

## Proposed shared budget extension

Add to P06's validated `QueryBudgetOptions`:

```json
"MaxProjectionIndexEntries": 250000
```

Add stable dimension `projection_index_entries` to `QueryBudgetDimension`, snapshots, metrics, and `QueryBudgetExceededException`.

An entry means one unique normalized scalar key retained for one distinct compiled join-index requirement. Duplicate source values do not consume another retained dictionary key, although their ambiguity state is recorded on the existing entry. The pre-insertion operation is:

1. Normalize the source value with the compiled scalar join normalizer.
2. Probe the index.
3. If absent, call `ConsumeProjectionIndexEntry()`.
4. Insert only after the charge succeeds.
5. If present for another canonical record identity, mark the entry ambiguous without another entry charge.

The default is finite and lower than the theoretical 25 columns multiplied by 25,000 records. Plans whose distinct join requirements exceed it fail safely instead of exchanging CPU scans for uncontrolled index memory. Low-cardinality utilization metrics guide later tuning.

Startup validation requires a positive finite value, checked arithmetic, and a value no greater than `MaxIntermediateRecords * PlanShapeLimits.MaxProjectionColumns`. Slice 1 moves the current private validator constant into one internal `PlanShapeLimits` authority consumed by both the existing validator and budget validation without changing its value. P12 later absorbs that authority and rejects a projection whose conservative required-index estimate cannot fit when source cardinalities are statically known; runtime accounting remains authoritative.

## Compiled projection program

Introduce immutable internal contracts resembling:

```csharp
internal sealed record CompiledProjectionProgram(
    string RowStep,
    ResultSchema Schema,
    ImmutableArray<CompiledProjectionFilter> Filters,
    ImmutableArray<CompiledProjectionColumn> Columns,
    ImmutableArray<ProjectionIndexRequirement> Indexes,
    CompiledAggregation? Aggregation);

internal sealed record CompiledProjectionColumn(
    string OutputName,
    string SourceStep,
    string SourceAttribute,
    int? JoinIndex,
    string? MatchValueFrom,
    ResultCell? DefaultValue);
```

Compilation resolves and validates once:

- Row step and every source step.
- Authoritative output column name/order and ordinal-ignore-case uniqueness.
- Source attributes and defaults.
- Projection filters through the existing validated filter semantics.
- Default `source_step`, `match_on`, and `match_value_from` values.
- Aggregation group attributes to exactly one projected column and then its output-schema ordinal, not repeated dictionary names.
- Distinct join-index requirements.

Execution accepts only the compiled object. It does not fall back to resolving an invalid raw plan. Before P12, compilation returns a typed failure through the existing validation boundary; after P12, invalid raw plans cannot reach execution at all.

### Aggregation field resolution

`group_by` is an ordered list of directory attribute names, matching the checked-in model comment, provider prompt, examples, and allow-list validation. Output column `name` is presentation text and is not aggregation identity.

For each `group_by` token, compilation finds projection columns whose `attribute` equals that token ordinal-ignore-case:

- Exactly one match resolves to that output-schema ordinal, regardless of its display `name`.
- No match fails compilation with `aggregation_group_not_projected`.
- More than one match fails compilation with `aggregation_group_ambiguous`; the current JSON shape cannot identify which source-step value was intended.

The current executor uses each `group_by` attribute token to probe a projected-row dictionary whose keys are output column `name` values, while the validator accepts allow-listed attributes. An unresolved token therefore silently becomes the same `"(empty)"` group as a genuine null today. P11 deliberately removes that mismatch: unresolved or ambiguous fields are plan errors, and a null structured key can arise only from a successfully resolved projected column whose runtime value is actually null. The behavior change requires Decision 5 and prompt/model/validator fixtures. P12 later owns the same rule authoritatively.

## Join normalization and indexing

### Compatibility baseline

Current join matching is ordinal-ignore-case over `DirectoryRecord.GetString(attribute)`. P11 preserves that comparison and the first scalar returned by `GetString` for this plan. It does not broaden joins to every value of a multivalue attribute without a separate contract.

For `distinguishedName`, use P10's canonical directory identity when P10 has landed; otherwise preserve the current raw string plus ordinal-ignore-case comparer and leave the P10 adapter explicit. The current lookup does not trim. Do not introduce trimming or a second DN parser in P11.

Reject null, empty, or whitespace-only lookup values as no match. Do not index them.

### Shared indexes

Build indexes only for requirements present in the compiled program. Group requirements by source step, enumerate that bounded retained source once, and update all its required indexes during that scan. Cache the immutable index set for the projection lifetime only.

Each key maps to:

```text
Missing       — no dictionary entry
Unique        — exactly one canonical record identity
Ambiguous     — two or more different canonical record identities
```

Repeated instances of the same canonical record do not create ambiguity. Different records with the same key do.

At lookup:

- Missing returns null and allows the compiled default.
- Unique returns the record.
- Ambiguous throws typed `projection_join_ambiguous` before producing a successful row or artifact.

Diagnostics may identify the fixed source step and attribute but never log the join value, row contents, DN, or user query.

## Canonical projected cells

Normalize each source/default value once through the P07 cell normalizer:

```text
null
string
boolean
integer
decimal
finite floating point
UTC date/time
ordered scalar list
```

The resulting `ResultRow` stores cells by schema ordinal rather than a new dictionary per row. A compatibility adapter may expose the existing case-insensitive dictionary DTO only at a legacy response boundary until all P07 consumers migrate; it must not become the canonical internal representation or be retained after artifact publication.

Unsupported provider values follow P07's single invariant-string rule. Cell-byte enforcement remains P07's responsibility. Projection must not normalize a value one way for aggregation and another way for artifacts.

## Structured aggregation contract

Replace `Dictionary<string, object>` and `grouped_counts` string keys with:

```csharp
public sealed record AggregationSummary(
    int SchemaVersion,
    ImmutableArray<string> GroupBy,
    ImmutableArray<AggregationBucket> Buckets);

public sealed record AggregationBucket(
    ImmutableArray<ResultCell> Key,
    long Count,
    long FirstSeenOrdinal);
```

`FirstSeenOrdinal` is internal ordering evidence and need not be exposed publicly if serializers preserve the resulting order.

Key identity is the ordered sequence of canonical typed cells:

- Null is distinct from empty string and the literal `"(empty)"`.
- Strings use ordinal, case-sensitive equality, preserving current value-case behavior.
- Boolean is distinct from string `"True"`.
- Integer, decimal, and floating-point values retain their P07 canonical numeric kinds; cross-kind equality is not inferred.
- UTC instants compare by canonical instant.
- Ordered scalar lists compare element by element and remain distinct from scalar strings and other types.
- Non-finite numbers are rejected by P07 normalization before grouping.

Serialize buckets as structured key arrays and counts. Never serialize identity as a delimiter-joined map key. For deterministic output, order buckets by descending count, then ascending first-seen ordinal. Ties therefore retain the bounded input order without culture-dependent sorting.

P07 manifests and exporters consume the typed summary. The browser/API presentation layer creates display labels from structured keys only at rendering time and HTML/CSV/Excel encodes them under P07 rules.

## Fused row pipeline

Introduce a single-use result resembling:

```csharp
public interface IProjectedResultSource : IResultRowSource, IAsyncDisposable
{
    ValueTask<ResultFinalization> GetCompletionAsync(CancellationToken cancellationToken);
}

public sealed record ResultFinalization(
    long CandidateRows,
    long PrimaryRows,
    AggregationSummary? Aggregation,
    ImmutableArray<ResultWarning> Warnings,
    QueryBudgetSnapshot Work);
```

`GetCompletionAsync` succeeds only after `ReadRowsAsync` reaches natural completion. Calling it early fails fast with an internal contract error; it never waits in a way that can deadlock an unstarted enumerator. Enumeration is allowed exactly once.

For each filter-matched row-step record:

1. Check caller cancellation and the P06 deadline.
2. Resolve all columns from the row record or O(1) compiled indexes.
3. Normalize one ordered `ResultRow`.
4. Increment candidate count with checked arithmetic.
5. If aggregation exists, derive its structured key from schema ordinals and update/create one bucket, charging a new group before insertion.
6. Apply primary-row semantics:
   - With a lower explicit requested limit, yield only the first requested rows but continue candidates when aggregation requires the full bounded input.
   - Without a lower limit, charge/yield through `MaxOutputRows`; candidate `N+1` causes hard budget failure before success.
   - Without aggregation and after a lower intentional limit is satisfied, stop early and release state.
7. Do not retain a second projected copy after P07 accepts the yielded row.

P07 preparation:

1. Acquires its artifact reservation and staging area.
2. Enumerates the source once and writes yielded primary rows with existing backpressure.
3. Obtains successful completion metadata after enumeration.
4. Writes the bounded manifest containing that exact aggregation and counters.
5. Publishes only after all P07 checks pass.

If enumeration or completion fails, P07 aborts staging and no preview/reference is returned. A later hard failure is safe even if earlier primary rows were written to private staging.

The source owns the projection indexes and step-state lease until completion/disposal. Cancellation, failure, or disposal releases them exactly once. A controller or job manager cannot cache or re-enumerate the source.

## Failure contract

P11 introduces stable causes for P13 to map later:

```text
projection_invalid
projection_index_limit_exceeded
projection_join_ambiguous
projection_source_unavailable
aggregation_group_not_projected
aggregation_group_ambiguous
aggregation_key_invalid
projection_pipeline_incomplete
```

P06 hard ceilings continue to use `query_budget_exceeded` with the exact dimension, including `projection_index_entries`. Cancellation/deadline classification remains P06/P13-owned.

No client error includes row values, join keys, DNs, source record contents, raw plan JSON, or stack traces.

## Telemetry

Extend the P06 application meter:

```text
adquery.projection.candidate_rows
adquery.projection.primary_rows
adquery.projection.index_entries
adquery.projection.index_build_duration
adquery.projection.join_lookup
adquery.projection.ambiguous_join
adquery.aggregation.groups
adquery.aggregation.updates
adquery.projection.duration
```

Allowed tags are fixed outcome, fixed failure reason, join kind (`dn` or `attribute`), and bounded plan shape buckets. Never tag attribute names from untrusted plans, step names, values, DNs, owner, query, artifact, or error text.

## Deterministic tests

Use fake bounded step states, injected time, an instrumented record collection, P06's real tracker, and P07's temporary artifact store. No AD, provider, IIS, wall-clock sleeps, or production disk is used.

### Compilation and schema

1. Raw invalid steps, columns, duplicate output names, filters, and aggregation fields cannot produce a compiled program.
2. Defaults for row/source/match fields are resolved once.
3. Equivalent join requirements share one index.
4. Schema order/spelling is stable and matches P07 rows.
5. Execution accepts a compiled program and has no raw-plan fallback.
6. `group_by` resolves by source attribute to exactly one projected output ordinal even when its display name differs.
7. Missing or multiply projected group attributes fail compilation; only a resolved runtime null creates a null key.

### Index behavior

8. DN and attribute joins match ordinal-ignore-case compatibility fixtures without adding pre-P10 trimming.
9. Source enumeration occurs once per source step, not once per column.
10. Row lookup performs no source scan; instrumented source enumeration count remains unchanged during projection.
11. Missing key applies the default exactly once.
12. Two different records sharing a used key produce `projection_join_ambiguous` and no artifact.
13. Duplicate instances of the same canonical identity are not ambiguous.
14. Null/blank keys are not indexed.
15. Equivalent columns reuse an index without duplicate entry charges.
16. Exactly `MaxProjectionIndexEntries` succeeds; the next new key fails before insertion and publishes nothing.
17. Cancellation/deadline during index construction releases indexes and staging.

### Aggregation identity

18. `("a|b", "c")` and `("a", "b|c")` remain separate buckets.
19. Null, empty string, and literal `"(empty)"` remain separate.
20. Boolean, string, integer, decimal, floating point, date/time, and list fixtures retain documented typed identity.
21. Culture changes do not alter keys, bucket counts, order, or serialized bytes.
22. Existing bucket updates do not consume another group unit.
23. Exactly `MaxAggregationGroups` succeeds; the next distinct bucket fails before insertion and leaves no artifact.
24. Bucket order is count-descending then first-seen and is stable across runs.

### Fused pipeline and P07

25. Every filter-matched candidate is normalized once and aggregation receives exactly that normalized row.
26. With aggregation and a lower requested limit, primary rows stop at the limit while aggregation observes every bounded matching candidate.
27. Without aggregation, a satisfied lower requested limit stops source work early.
28. Hard output row `N+1` fails before publication and P07 removes staged earlier rows.
29. The row source is enumerated once and never more than one row ahead of blocked P07 writes.
30. Completion requested before enumeration fails immediately rather than hanging.
31. Natural completion exposes exactly one immutable finalization; failure/cancellation exposes none.
32. Source disposal releases state once on success, failure, cancellation, and abandoned enumeration.
33. P07 writes no manifest/marker before completion and preserves typed aggregation unchanged.
34. Synchronous and queued paths produce byte-identical canonical rows/aggregation and identical P06 counters.
35. `QueryJobManager`, controllers, and exporters contain no aggregation implementation or group charging.
36. No projected row list or duplicate dictionary cache remains after direct-source migration.

## Red/green guard proof

For every test-bearing slice:

1. Add the focused guard and prove the pre-change behavior fails.
2. Implement the smallest slice and prove it passes.
3. Temporarily restore or disable the protected behavior.
4. Confirm the focused guard fails for the intended reason.
5. Restore the implementation and run P01's canonical verification.
6. Commit only the restored slice.

Mandatory mutations:

- Restore `FirstOrDefault` source scans; lookup-complexity/source-enumeration guard fails.
- Build one index per column instead of per equivalent requirement; entry-charge/reuse guard fails.
- Choose the first ambiguous record; ambiguity/no-artifact guard fails.
- Restore delimiter-joined string keys; collision and null-literal guards fail.
- Restore ambient `ToString()` key conversion; culture/type guards fail.
- Aggregate in a second projected-row pass; single-normalization/enumeration guard fails.
- Charge an existing bucket; exact group-counter guard fails.
- Stop aggregation at a lower primary-row limit; full bounded aggregation guard fails.
- Publish P07 before completion; late-failure atomicity guard fails.
- Restore queued aggregation; exactly-once authority/parity guard fails.
- Materialize all projected rows; allocation and row-source retention guards fail.

Leave no mutation in the worktree.

## Benchmarks

Extend the P06 benchmark project.

Parameters:

```text
Row-step records:       1,000 / 5,000 / 25,000
Source-step records:    1,000 / 5,000 / 25,000
Projection columns:     5 / 15 / 25
Distinct join indexes:  0 / 1 / 5 / 25
Join hit rate:           0% / 50% / 100%
Aggregation fields:     0 / 1 / 5
Distinct groups:        1 / 100 / 5,000
Primary limit:           10 / 1,000 / hard ceiling
```

Measure source-record enumerations, join probes, candidate rows, cell normalizations, bucket probes, allocations, retained bytes, collections, and throughput.

Structural gates:

- Source index build work is proportional to source records times distinct index requirements.
- Join lookup performs one dictionary probe per cross-step column and no source scan.
- Each candidate row is normalized once.
- Aggregation is updated in that same pass.
- Managed retention is bounded by charged index entries, P06 groups, and the P07 write buffer, not all projected rows.
- P07 row lookahead remains bounded.
- Synchronous and queued operation/counter totals match.

Wall-clock comparisons are informational unless the environment is controlled. Operation counts, allocation growth, and retained-shape regressions are gating.

## Implementation slices

Each slice is one commit and must pass focused plus canonical verification before the next begins.

### Slice 1 — Compiled projection and typed aggregation contracts

Commit intent: `refactor: compile projection execution contracts`

- Reuse P07's ordered `ResultSchema`, `ResultCell`, `ResultRow`, and normalizer; add only immutable compiled-projection, structured-key, bucket, summary, and finalization types.
- Move the existing projection-column maximum unchanged into shared internal `PlanShapeLimits` for validator/budget use.
- Compile only after current validation.
- Add schema, invalid-plan, key identity, culture, and serialization tests.
- Do not change hot-path execution yet.

### Slice 2 — Shared bounded join indexes

Commit intent: `perf: index projection joins`

- Extend the shared P06 tracker with `projection_index_entries`.
- Build/reuse immutable indexes by compiled requirement.
- Add explicit ambiguity handling and cleanup.
- Replace projection source scans.
- Add exact-bound, complexity, ambiguity, deadline, and cancellation guards.

### Slice 3 — Incremental structured aggregation

Commit intent: `refactor: structure projection aggregation`

- Replace P06's interim string-key accumulator with typed structured buckets.
- Preserve executor-only group charging.
- Update P07 manifest/export serialization adapters.
- Add collision, type, order, exact-budget, and compatibility tests.

### Slice 4 — Fused finalizable row source

Commit intent: `perf: fuse projection and aggregation`

- Add the single-use projected row source and completion contract.
- Fuse filtering, projection, normalization, primary-row selection, and aggregation updates.
- Preserve lower-limit/full-aggregation and hard-limit/no-partial semantics.
- Add enumeration, completion, disposal, cancellation, and allocation tests.

### Slice 5 — Stream projection into P07

Commit intent: `perf: stream projected rows into artifacts`

- Make P07 consume the source once, then its finalization before manifest commit.
- Remove the executor projected-row materialization and P07 list adapter.
- Migrate synchronous and queued paths without adding caches or recomputation.
- Add late-failure staging, backpressure, byte-parity, and exactly-once counter tests.

### Slice 6 — Migrate presentation and remove compatibility shapes

Commit intent: `refactor: consume typed aggregation summaries`

- Update response DTOs, P07 exporters, and browser rendering to use structured buckets.
- Remove `grouped_counts`, delimiter splitting, dictionary aggregation, and any remaining queued transform.
- Preserve primary dataset semantics.
- Add API/export/browser golden fixtures.

### Slice 7 — Benchmarks and architecture guards

Commit intent: `perf: benchmark projection pipeline`

- Add operation/allocation benchmarks and record one Release baseline.
- Add dependency/source guards for sole aggregation ownership and absence of hot-loop source scans/materialized projected lists.
- Document tuning and P12 handoff.

Verification after each code slice:

```powershell
pwsh -NoLogo -NoProfile -File scripts/verify.ps1
```

Run the focused benchmark command after Slices 2, 4, 5, and 7.

## Acceptance criteria

- Projection strings are resolved once into an immutable compiled program.
- Every cross-step lookup uses a shared bounded index and performs no source scan.
- Projection-index entries use the shared tracker and fail before over-limit insertion.
- Used ambiguous join keys fail deterministically with no partial artifact.
- Ordered canonical typed cells are normalized once per candidate row.
- Aggregation identity is structured, typed, culture-independent, and collision-safe.
- Aggregation updates during the projection pass and does not rescan a projected list.
- New groups and primary rows consume their P06 counters exactly once.
- Lower explicit primary limits do not truncate authoritative aggregation input.
- Hard row/group/index/deadline failure publishes no artifact.
- P07 consumes one row source with bounded lookahead and commits only after finalization.
- No complete projected-row collection remains after migration.
- The executor remains the sole aggregation authority.
- Synchronous and queued rows, aggregation, and counters agree.
- P07 artifacts/exporters and browser/API presentation consume structured buckets without delimiter parsing.
- P12 can become the compiled-program producer without changing execution.
- Deterministic guards, canonical verification, and structural benchmarks pass.
- Each slice is committed separately.

## Rollback

Use new revert commits; do not rewrite history.

- Revert consumers before reverting structured aggregation contracts.
- Retain P06's executor-only authority even if P11 aggregation representation rolls back.
- Retain P06 hard budgets and never restore queued recomputation or primary-row mutation.
- If direct streaming must roll back, use P07's bounded materialized adapter temporarily; do not restore downstream clones or unbounded exports.
- Reverting indexes may restore bounded linear lookup only as a short-lived compatibility fallback with P06 deadline enforcement; do not claim the optimization complete.
- Revert the shared index-budget dimension only after every index allocation and metric is removed.
- A manifest/schema rollback requires an explicit supported reader or artifact invalidation; never reinterpret structured keys as delimiter strings.

## Risks

- **Indexes trade CPU for memory.** Charge exact unique entries through the shared tracker and benchmark retained bytes.
- **Many distinct join attributes multiply entries.** Compile/reuse requirements and enforce the finite index-entry ceiling.
- **Ambiguity failure changes first-record behavior.** Make it an owner decision; silent nondeterministic joins can attach the wrong directory data.
- **Typed grouping changes legacy display keys.** Version the DTO/manifest and use golden API/export/browser fixtures.
- **Numeric kind distinctions may surprise callers.** Document canonical equality and do not infer conversions during grouping.
- **Lower primary limits still require full aggregation work.** This is the accepted P06 semantic; deadline/intermediate/group limits bound it.
- **Late failure occurs after staging rows were written.** P07 staging is private and aborts atomically, so no partial success escapes.
- **A finalizable source can deadlock if misused.** Early completion access fails immediately and deterministic contract tests enforce call order.
- **Step state must outlive deferred enumeration.** The source owns an explicit lease and releases it exactly once.
- **P10 may change DN identity.** Consume its canonical identity abstraction rather than implementing another parser.
- **P12 may rename compiled types.** Preserve semantics and one authoritative producer; do not duplicate validation.
- **P14 may change job orchestration.** It consumes final artifacts/metadata and must never enumerate or aggregate again.

## Open owner decisions

### Decision 1 — Join ambiguity

Choose fail-closed ambiguity or preserve first-record-wins for non-unique cross-step joins. Recommendation: fail with `projection_join_ambiguous`; selecting the first LDAP record is order-dependent and can attach another directory object's data to a row.

Blocked until decided: Slice 2 ambiguity behavior.

### Decision 2 — Structured aggregation identity

Choose typed structured keys or preserve delimiter-joined display strings. Recommendation: type-tagged ordered keys; they eliminate delimiter/null/type/culture collisions at the cost of a versioned API, manifest, exporter, and browser migration.

Blocked until decided: Slices 1, 3, and 6.

### Decision 3 — Projection index ceiling

Approve an initial 250,000 unique index-entry ceiling per query in the shared P06 tracker. Recommendation: start finite and tune from allocation/utilization metrics; the theoretical plan-shape maximum is too large to accept without a memory bound.

Blocked until decided: Slice 2 checked-in value.

### Decision 4 — Direct finalizable streaming

Choose direct single-use projection into P07 staging or retain the bounded projected list. Recommendation: direct finalizable streaming; it removes the remaining complete primary-row materialization while P07's private staging preserves atomic no-partial semantics, at the cost of a stricter producer/consumer lifecycle.

Blocked until decided: Slices 4 and 5.

### Decision 5 — Aggregation field namespace

Choose attribute-based grouping with strict projected-column resolution or preserve the current accidental output-name lookup and silent null bucket. Recommendation: keep the documented/validated attribute namespace, require exactly one projected column for each group attribute, and reject missing/ambiguous mappings; silent null aggregation hides malformed plans.

Blocked until decided: Slices 1, 3, and 6 plus the later P12 rule.

## Advisory Review

### Round 1 — 2026-07-21T21:59:55Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Revisions required

- Defined `group_by` as the documented allow-listed attribute namespace and required exactly one projected-column mapping to a schema ordinal.
- Replaced the current silent unresolved-field null bucket with explicit missing/ambiguous compilation failures behind an owner decision; genuine runtime nulls remain structured null keys.
- Clarified that P11 reuses P07's schema/cell contracts rather than duplicating them.
- Moved the existing projection-column maximum into one internal plan-shape authority so index-budget validation has an implementable checked bound.
- Corrected the DN compatibility baseline: current matching is raw ordinal-ignore-case and does not trim before P10.

### Round 2 — 2026-07-21T22:05:58Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Accepted

- Confirmed the attribute-to-projected-column aggregation rule, explicit missing/ambiguous failures, and genuine-null key semantics resolve round 1.
- Confirmed P07 type reuse, the shared plan-shape limit, raw pre-P10 DN behavior, exact shared-budget accounting, finalizable source lifecycle, and commit-after-finalization contract.
- Found no remaining material correctness, security, resource, atomicity, or verifiability blocker.
- Applied optional clarity by aligning P06 type names and precisely describing the current attribute-token/output-name-key mismatch.

Record no more than three headless Claude review rounds. Each round must identify material findings, the resulting revision or retained disagreement, and the reviewer's final assessment.
