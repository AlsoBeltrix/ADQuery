# P06 — Finite Per-Query Work Budgets

Status: **Reviewed — implementation is not authorized**

Owner approval: Pending

Implementation dependency: P01 verification foundation must land first. P03 may update the target framework before this plan; use the application's current target when implementation begins. For the shared CSV surface, land P06 before P05 so the finite execution context exists before P05 adds batching and bisection.

Review status: Accepted in advisory round 3

## Problem

A directory query can currently perform effectively unbounded work. The checked-in default `QueryDefaults:MaxResults` is `0`, meaning unlimited, and the executor applies a positive plan result limit only after directory results, intermediate step state, projection rows, and aggregation inputs have already been materialized.

Other dimensions of work are also not bounded cumulatively across a query:

- Directory-service calls.
- Intermediate directory records retained across steps.
- Template combinations and their resulting searches.
- Traversal nodes across recursive steps.
- Distinct aggregation groups retained in result metadata.
- Active execution time.

Several paths have local limits, but they are optional, per-step, or enforced after expensive work. A syntactically valid plan can therefore consume excessive LDAP calls, memory, CPU, or time before the final response is truncated. The same executor is used by synchronous and queued jobs, so moving a query to the background does not make its resource use safe.

## Repository evidence

- `csharp/appsettings.json` sets `QueryDefaults:MaxResults` to `0` and documents `0=unlimited`.
- `QueryController.ExecuteQuery` and `ExecuteQueryAsync` read `QueryDefaults:MaxResults` with a fallback of `0`.
- `PlanPreprocessor.PrepareForExecution` applies a limit only when the supplied limit is positive.
- `DirectoryPlanExecutor.PlanRuntime.ExecuteAsync` executes all steps, projects the result, and then applies `plan.ResultLimit` with `Take(...).ToList()`.
- `QueryController` derives requested limits from `QueryDefaults:MaxResults` and applies another post-execution `Take`, leaving enforcement duplicated outside the production boundary.
- `DirectoryPlanExecutor.ExecuteSearchStep` can expand template references into a Cartesian collection and execute one directory search for each expanded filter set.
- `DirectoryPlanExecutor.TryExpandTemplateFilters` creates a complete `List<List<DirectoryFilter>>` before the searches run.
- `DirectoryPlanExecutor.ExecuteExpandReportsStep` has per-step depth and node values, but there is no cumulative traversal budget across the plan.
- `DirectoryPlanExecutor.StepRuntimeState` retains each step's records for later lookups and projections.
- `ActiveDirectoryService.SearchAsync` calls `DirectorySearcher.FindAll` and copies every returned result into a list.
- `ActiveDirectoryService.LookupAsync` can perform multiple directory-object lookups and returns a fully materialized list.
- `ActiveDirectoryService.ExpandGroupMembersAsync` can recursively perform more directory work inside one executor-level service call.
- `QueryController` clones full rows, generates complete export buffers, and caches complete row collections after execution. P07 owns those output-path allocations, but P06 must prevent execution from producing an unbounded input to them.
- `QueryJobManager` invokes the same plan preprocessor and executor used by the synchronous path.
- Existing `Security:MaxRecursionDepth`, `Security:MaxNodesPerRecursion`, and `Security:DefaultMaxNodes` values validate or default individual traversal instructions; they do not impose one cumulative query-work budget.
- Cancellation tokens are present in service interfaces, but synchronous `System.DirectoryServices` operations cannot currently guarantee interruption during a blocking LDAP call. P09 owns that lower-level timeout and scheduling work.

## Goals

1. Make every directory query finite by default.
2. Enforce a nonzero hard maximum for final result rows.
3. Bound cumulative intermediate records retained across all plan steps.
4. Bound distinct aggregation-group cardinality independently of returned rows.
5. Bound cumulative directory operations across all plan steps and internal expansion work.
6. Bound template combinations before the Cartesian collection or corresponding searches are materialized.
7. Bound cumulative traversal nodes and traversal depth.
8. Bound active plan-execution time.
9. Apply limits before expensive materialization wherever the current interfaces permit.
10. Use one immutable budget and one shared tracker for the entire query execution.
11. Ensure request values and model-generated plans can lower limits but cannot raise configured hard ceilings.
12. Return a typed, stable budget-exceeded failure instead of silently returning incomplete data.
13. Apply identical semantics to synchronous and queued directory-query execution.
14. Emit low-cardinality utilization and exhaustion metrics suitable for later tuning.
15. Provide deterministic fake-directory regression tests without live Active Directory.
16. Add an allocation/load benchmark that proves work scales with the configured budget rather than advertised source cardinality.

## Non-goals

- Do not redesign streaming exports, cache representation, or file generation; P07 owns those changes.
- Do not optimize or batch template searches; P08 owns that algorithm. P06 only rejects excessive expansion before materialization and counts executed work.
- Do not introduce the global LDAP scheduler, dedicated blocking workers, or hard LDAP timeouts; P09 owns those mechanisms.
- Do not redesign cycle detection or traversal data structures; P10 owns those changes. P06 supplies cumulative traversal ceilings.
- Do not optimize projection joins or aggregation passes; P11 owns those changes. P06 bounds their input.
- Do not make job-store transitions atomic or replace the queue; P14 owns that work. P06 only supplies a typed failure for the existing job path.
- Do not add output-byte budgets. P07 must define byte and artifact limits alongside streaming.
- Do not add tenant-, role-, or user-specific higher limits.
- Do not allow callers or LLM-generated plans to disable a hard budget.
- Do not claim that the execution-time budget can interrupt a currently blocking `System.DirectoryServices` call before P09 lands.
- Do not use live LDAP latency or production traffic as an automated test prerequisite.
- Do not silently convert budget exhaustion into a successful partial result.

## Terminology and invariants

### Hard budget

A hard budget is a configured server ceiling. It is always finite and positive. Client input, model output, or plan fields may select a lower value but may never raise or disable it.

### Requested result limit

A requested result limit is an intentional result count chosen by the caller or accepted from the validated plan. Reaching that lower limit is successful completion of the requested contract. It is not a budget-exceeded failure.

The effective requested result limit is:

```text
minimum(
    positive request limit, if present,
    positive validated plan result_limit, if present,
    configured MaxOutputRows
)
```

When neither the request nor plan supplies a positive limit, `MaxOutputRows` is used.

### Budget exhaustion

Budget exhaustion means execution has evidence that completing the query would exceed a hard server ceiling. It produces a typed failure and no successful partial dataset.

A dimension is exhausted only when an additional unit is attempted or an extra sentinel item proves that more data exists. Merely reaching a limit exactly is not failure if no additional work is requested.

### No silent partial success

On hard-budget exhaustion:

- `Success` is false.
- The failure code is `query_budget_exceeded`.
- Partial rows are not returned as successful data.
- No result cache entry or downloadable artifact is created.
- A queued job ends in `Failed`, not `Completed`.
- Structured details identify the exhausted dimension and configured limit.
- Logs and metrics record utilization without recording query text, LDAP filters, distinguished names, usernames, or row values.

### One tracker per query

All steps in one plan share one tracker. Starting a new step, fallback search, template expansion, lookup batch, or traversal does not reset counters.

Queued jobs receive a new tracker when active execution starts. Time spent waiting in the job queue does not consume the active execution-time budget. P14 may add a separate queue-age policy.

## Proposed configuration

Replace the unlimited `QueryDefaults:MaxResults` setting with one canonical section:

```json
"QueryBudgets": {
  "MaxOutputRows": 5000,
  "MaxIntermediateRecords": 25000,
  "MaxAggregationGroups": 5000,
  "MaxDirectoryOperations": 200,
  "MaxExecutionSeconds": 120,
  "MaxTemplateCombinations": 256,
  "MaxTraversalNodes": 10000,
  "MaxTraversalDepth": 10
}
```

These are initial conservative values, not performance guarantees. They require owner approval and must be tuned from emitted utilization metrics after representative use.

Add `QueryBudgetOptions` and register it with `AddOptions<QueryBudgetOptions>()`, configuration binding, validation, and `ValidateOnStart()`.

Validation must reject startup when:

- Any budget is zero or negative.
- `MaxIntermediateRecords` is lower than `MaxOutputRows`.
- `MaxAggregationGroups` exceeds `MaxIntermediateRecords`.
- `MaxTraversalNodes` exceeds `MaxIntermediateRecords`.
- `MaxTraversalDepth` exceeds the validated `Security:MaxRecursionDepth`.
- Integer conversion overflows or configuration is missing.

Do not retain `0=unlimited` compatibility. During implementation, remove `QueryDefaults:MaxResults` and update every consumer in the same slice so there is one canonical output-row ceiling.

The existing security recursion settings remain distinct:

- Security settings define which traversal instructions are valid.
- Query budgets define how much cumulative work one accepted plan may actually consume.
- The effective per-step traversal depth and node count is the minimum of validated step values, security defaults or ceilings, and the remaining query budget.

## Technical design

### Core types

Add an immutable limit record:

```csharp
public sealed record QueryBudgetLimits(
    int MaxOutputRows,
    int MaxIntermediateRecords,
    int MaxAggregationGroups,
    int MaxDirectoryOperations,
    TimeSpan MaxExecutionTime,
    int MaxTemplateCombinations,
    int MaxTraversalNodes,
    int MaxTraversalDepth);
```

Add a per-execution tracker:

```csharp
public interface IQueryBudgetTracker
{
    QueryBudgetLimits Limits { get; }
    QueryBudgetSnapshot Snapshot { get; }

    void CheckDeadline();
    void ConsumeDirectoryOperation(string operationKind);
    void ConsumeIntermediateRecords(int count);
    void ConsumeOutputRows(int count);
    void ConsumeAggregationGroup();
    void ConsumeTemplateCombinations(int count);
    void ConsumeTraversalNode();
    void ObserveTraversalDepth(int depth);
    int GetRemainingIntermediateCapacityWithSentinel();
    int GetRemainingOutputCapacityWithSentinel();
}
```

The production implementation must:

- Be created once per query, not registered as a singleton.
- Use `TimeProvider` and a monotonic timestamp for elapsed time.
- Use checked arithmetic.
- Be safe if P09 later executes directory work concurrently.
- Use `Interlocked` or equivalent atomic counter updates.
- Reject an increment before the expensive work starts when its size is known.
- Permit a bounded `remaining + 1` sentinel read when it is necessary to prove overflow.
- Throw one typed exception on exhaustion.

Add:

```csharp
public sealed class QueryBudgetExceededException : Exception
{
    public QueryBudgetDimension Dimension { get; }
    public long Limit { get; }
    public long Attempted { get; }
    public QueryBudgetSnapshot Snapshot { get; }
}
```

`QueryBudgetDimension` is a string-serialized enum with stable values:

```text
output_rows
intermediate_records
aggregation_groups
directory_operations
execution_time
template_combinations
traversal_nodes
traversal_depth
```

`QueryBudgetSnapshot` contains limits, consumed counters, and elapsed time. It contains no source records or user input.

### Execution context

Create one explicit `QueryExecutionContext` containing:

- `IQueryBudgetTracker Budget`.
- The linked execution cancellation token.
- A stable request or job correlation identifier.
- The externally supplied cancellation token.
- A flag or token source that distinguishes budget-deadline cancellation from caller cancellation.

Pass the context explicitly through the plan executor and directory-service boundary. Do not hide it in a static, singleton, or `AsyncLocal` accessor.

Update `IDirectoryPlanExecutor` overloads so synchronous and queued callers must provide a context. A convenience factory may create the context from validated options, but there must be no production “unlimited” overload.

Update `IActiveDirectoryService` methods to accept the execution context. No directory entry point may bypass the global budget merely because it is not a standard plan step.

For CSV enrichment, `QueryController.CsvEnrich` creates the same finite context through an injected `IQueryExecutionContextFactory` after P05-style transport/request validation and before LLM or directory execution, then passes it explicitly to `CsvEnrichmentService.ExecuteAsync`. P06 lands this signature and wiring before P05. P05 must preserve the context parameter when it replaces per-row lookup with batched lookup and bisection; every physical batch or retry consumes one P06 directory operation and checks the shared deadline before and after the call.

This context alone does not bound the current CSV service's in-memory `outputRows`: rows with empty identifiers and `output_mode = all` rows can be retained without a directory operation. That is an explicit interim residual between P06 and P05. P05's finite row/output limits must land before the combined CSV path is considered memory-bounded; P06 must not claim otherwise.

### Deadline and cancellation classification

Create a budget deadline token at the start of active execution using `TimeProvider` and the configured duration. Link it with the caller or job cancellation token.

Classification rules:

- Caller cancellation before budget expiry remains cancellation.
- Explicit job cancellation remains `Cancelled`.
- Budget deadline expiry becomes `QueryBudgetExceededException` with dimension `execution_time`.
- An unrelated `OperationCanceledException` must not be mislabeled as budget exhaustion.
- A blocking LDAP operation that returns after the deadline must be checked immediately and fail before its results enter step state.
- P09 must later configure LDAP-level timeouts or dedicated workers so a blocking operation itself cannot exceed the wall-clock policy indefinitely.

Do not implement deadline tests using `Thread.Sleep`. Inject `TimeProvider` and advance fake time deterministically.

### Directory-operation accounting

Define one directory operation as one server-bound unit initiated by the application:

- One `DirectorySearcher.FindAll` execution.
- One individual directory-object bind or refresh used by lookup or group expansion.
- One RootDSE lookup when it occurs within query execution.
- Each fallback search.
- Each template-expanded search.
- Each internal recursive lookup or search.

Consume the operation before initiating it. A rejected operation must not contact the fake or real directory service.

Do not count pure in-memory projection, filtering, or aggregation as directory operations; their input is bounded by intermediate-record, aggregation-group, output-row, and deadline budgets.

P09 may later add separate concurrency metrics, physical timeout details, and batching. It must preserve this logical accounting or replace it with a documented stricter accounting model.

### Intermediate-record accounting

Count every directory record admitted into retained runtime state, including:

- Search results.
- Lookup results.
- Expanded group members.
- Direct-report traversal results.
- Fallback-search results.
- Results merged from template-expanded searches.

Deduplicated records count when first admitted, not once per duplicate response. Records discarded before entering runtime state do not consume retained-record budget, but the directory operation that fetched them still counts.

Before a directory request, derive the maximum useful response size from the remaining intermediate budget. Request no more than `remaining + 1`, further reduced by any positive step limit or explicit requested result limit that is semantically applicable.

When a response contains the sentinel record beyond remaining capacity:

1. Do not append any response records to step state.
2. Throw `QueryBudgetExceededException`.
3. Discard the partial execution result.

Do not call `ToList()` on an unbounded result and then check its count.

Where `IReadOnlyList` prevents true streaming, the directory service must still constrain the server request before `FindAll`. P07 may later replace result materialization with streaming abstractions.

`ActiveDirectoryService.ExpandGroupMembersAsync` currently uses `DirectoryEntry.RefreshCache` and exposes no server-side `SizeLimit`. P06 can reject additional members before admission and discard the failed result, but it cannot prevent that API from returning a large `member` property allocation. Record this as a residual bound until P10 replaces it with ranged member retrieval; do not claim that the `remaining + 1` server-request rule applies to this path.

### Output-row and aggregation accounting

`MaxOutputRows` applies to projected rows in the final primary dataset, not to the number of rows examined by aggregation. P06 removes the queued-only compatibility heuristic that replaces projected rows with aggregation-group rows when projection columns happen to equal `group_by`. Aggregation is metadata and must not silently redefine the primary dataset. Any future explicit aggregate-row output mode requires a separately approved contract and must define its own exactly-once final-row accounting.

Move primary-row enforcement into production rather than applying `Take` after `result.Data` is complete. Production must:

- Derive the effective requested limit before executing steps.
- Retain no more than the effective primary-row limit plus one sentinel.
- Treat a caller- or plan-selected lower limit as successful intentional truncation of primary rows.
- Treat evidence of primary rows beyond the hard `MaxOutputRows` ceiling as typed budget exhaustion unless a lower explicit requested limit already defines successful completion.
- Return no partial primary rows after hard-budget failure.

Aggregation input is instead bounded by `MaxIntermediateRecords`, the active deadline, and the invariant that current projection produces at most one candidate row per retained row-step record. Aggregation may process every filter-matched projected row within that bound even when only a smaller primary-row prefix is returned. P11 must preserve that one-to-one bound or introduce a separately reviewed finite expansion budget before adding any one-to-many projection behavior.

Every newly created aggregation bucket consumes one `MaxAggregationGroups` unit before insertion. Updating an existing bucket consumes no new group. Exactly N groups may succeed at a limit of N; attempting group N+1 throws `QueryBudgetExceededException` with dimension `aggregation_groups`, discards aggregation and primary rows, and creates no artifact.

Replace the current one-shot `GroupBy(...).ToDictionary(...)` implementation with an incremental accumulator. For each row, derive the key, update an existing bucket without charging, or call `ConsumeAggregationGroup` before inserting a new bucket. Never materialize all distinct groups and check the count afterward.

`DirectoryPlanExecutor` is the single authoritative charging site. P06 adds its computed aggregation to `PlanExecutionResult` using the existing bounded representation, and synchronous and queued consumers reuse that value unchanged. `QueryJobManager` must delete its duplicate `ComputeAggregation` pass and its aggregation-to-primary-rows mutation; it never invokes `ConsumeAggregationGroup` or recharges output rows. Thus N logical groups consume exactly N units and projected primary rows consume output capacity exactly once in both paths. P11 later replaces the representation and combines projection/aggregation into one optimized pass without reintroducing another charging site.

P11 may change projection indexing, aggregation mechanics, and presentation, but it must preserve the distinct intermediate-input, group-cardinality, and final-primary-row limits.

### Template-combination accounting

The combination budget applies to the true Cartesian path in `TryExpandTemplateFilters`. Before constructing those combinations:

1. Collect referenced step cardinalities.
2. Compute the Cartesian cardinality using checked multiplication.
3. Stop immediately if multiplication overflows.
4. Compare the predicted count to remaining template-combination budget.
5. Fail before allocating the combination list or issuing any corresponding search when it would exceed the ceiling.
6. Consume each combination as it is generated.

Replace eager list construction with bounded iteration sufficient to avoid allocating beyond the approved combination count. This is the minimum safety change. P08 owns batching, query consolidation, canonicalization, and algorithmic optimization.

Each expanded search also consumes one directory operation, so the effective number of searches is the smaller of remaining template combinations and remaining directory operations. Reaching either hard ceiling follows its own dimension's exact-limit/sentinel rule.

`TryEvaluateTemplateSearch` is a linear in-memory candidate scan, not a Cartesian expansion. Its candidates are already bounded by retained intermediate records and the active deadline; do not charge arbitrary template-combination units for that path. P08 owns any tighter scan or generated-filter complexity policy.

### Traversal accounting

At traversal start:

- Determine effective maximum depth from the step request, security settings, and `MaxTraversalDepth`.
- Determine effective node allowance from the step request, security settings, and remaining cumulative `MaxTraversalNodes`.

Before admitting each newly discovered traversal node:

- Check the deadline.
- Check depth.
- Consume one traversal node.
- Consume one intermediate record if the node's record enters retained step state.

A node encountered repeatedly should consume the traversal-node budget only when admitted as a new node after P10 adds authoritative visited-set behavior. Until P10 lands, P06 must not weaken current safeguards and must still stop at the cumulative ceiling.

Traversal budget is cumulative across all traversal steps in one plan.

### Failure contract

Inside the executor, allow `QueryBudgetExceededException` to cross internal step boundaries without conversion to a generic exception. At the outer executor boundary, map it to a structured failed `PlanExecutionResult`:

```text
Success: false
ErrorCode: query_budget_exceeded
Error: stable human-readable explanation
BudgetFailure:
  Dimension
  Limit
  Attempted
  Snapshot
Data: empty
```

Do not expose stack traces or LDAP details.

Synchronous API behavior:

- Return RFC 7807 `ProblemDetails`.
- Use HTTP `422 Unprocessable Entity`.
- Set `type` to a stable application URI or identifier for `query_budget_exceeded`.
- Include `dimension`, `limit`, and the request correlation identifier in extensions.
- Do not include partial rows or create a result artifact.

Queued-job behavior:

- Set the job to `Failed`.
- Preserve stable `ErrorCode`, dimension, limit, and safe message.
- Do not mark it `Completed`.
- Do not assign a results cache key.
- P14 must later make this state transition atomic.

CSV enrichment behavior:

- Use the same typed failure and no-partial-data rule.
- P05 may define a more restrictive CSV request ceiling and batching strategy.

### Metrics and structured logging

Use `System.Diagnostics.Metrics` so instrumentation does not require an exporter package.

Create one application meter with:

- `adquery.query.duration` histogram in milliseconds.
- `adquery.query.output_rows` histogram.
- `adquery.query.intermediate_records` histogram.
- `adquery.query.directory_operations` histogram.
- `adquery.query.template_combinations` histogram.
- `adquery.query.traversal_nodes` histogram.
- `adquery.query.traversal_depth` histogram.
- `adquery.query.budget_exceeded` counter.

Allowed metric tags:

- Execution path: `sync`, `job`, or `csv`.
- Outcome: `success`, `failed`, `cancelled`, or `budget_exceeded`.
- Budget dimension.
- Operation kind from a fixed enum.

Forbidden metric tags:

- Username.
- Request or job identifier.
- Natural-language query.
- LDAP filter, distinguished name, attribute, group, OU, or row value.
- Exception message.

Emit one structured completion or failure log with correlation identifier and `QueryBudgetSnapshot`. Do not log a message for every consumed record or node.

P16 may later configure an OpenTelemetry exporter or durable telemetry sink. P06 only creates and tests instrumentation.

## Deterministic test design

P01 must provide the test project and canonical verification command before implementation.

### Test doubles

Add `BudgetAwareFakeDirectoryService` in the test project. It must:

- Implement `IActiveDirectoryService`.
- Record every attempted operation.
- Assert that a finite execution context is supplied.
- Honor `DirectorySearchRequest.SizeLimit`.
- Generate only the requested bounded number of records, plus an explicitly requested sentinel.
- Expose attempted and completed operation counts.
- Support deterministic scripted responses for search, lookup, membership, and reports.
- Support a callback that advances `FakeTimeProvider`.
- Throw if production code asks it to materialize an unbounded scripted source.

Do not use a live domain, mock framework, random timing, or sleeps.

### Required tests

#### Configuration

1. Checked-in configuration contains no zero or unlimited budget.
2. Startup validation rejects every zero or negative limit.
3. Startup validation rejects intermediate rows below output rows.
4. Startup validation rejects traversal nodes above intermediate rows.
5. Startup validation rejects traversal depth above the security ceiling.

Also prove that `MaxAggregationGroups` is required, positive, and no greater than `MaxIntermediateRecords`.

#### Effective limits

6. A query without request or plan limits receives `MaxOutputRows`.
7. A lower request limit wins.
8. A lower validated plan limit wins.
9. A request or plan value above the hard ceiling cannot raise it.
10. A zero or negative caller value cannot disable the hard ceiling.

#### Directory operations

11. The Nth allowed directory operation runs.
12. Operation N+1 fails before the fake service is invoked.
13. Fallback and template searches consume the same counter.
14. Internal lookup or expansion work consumes operations rather than counting as one opaque executor call.

#### Intermediate records

15. Search requests receive a finite `SizeLimit` derived from remaining capacity.
16. Exactly the allowed number of records can enter step state.
17. A sentinel record causes typed failure before any response records from that operation are appended.
18. The failure returns no final data.
19. Records merged from multiple steps share one cumulative allowance.

#### Output rows

20. Projection stops at a lower explicit requested limit and succeeds.
21. A hard output-row overflow produces typed failure rather than a warning and partial success.
22. Aggregation may process all matched rows bounded by intermediate records even when the returned primary-row limit is lower.
23. The controller does not cache, export, or return partial rows after failure.

Additional aggregation guards:

- Twenty thousand retained/matched rows grouped into ten buckets succeed when the output-row cap is 5,000 and the intermediate-record cap is at least 20,000.
- Exactly `MaxAggregationGroups` distinct buckets succeed; the next new bucket fails before insertion with dimension `aggregation_groups` and publishes neither primary rows nor aggregation.
- Updating an existing bucket at the group ceiling succeeds.
- The queued path reuses executor aggregation, does not recompute it, does not transform it into primary rows, and reports the same `aggregation_groups`/`output_rows` consumption as synchronous execution.
- N groups consume exactly N group units in both synchronous and queued execution; a mutation that restores job-manager recomputation must make the queued guard fail at or before N.

#### Template combinations

24. Cardinality beneath the ceiling succeeds.
25. Cardinality above the ceiling fails before a combination list or LDAP call is created.
26. Checked multiplication overflow produces typed template-budget failure.
27. Multiple template steps share one cumulative allowance.

#### Traversal

28. Traversal at the permitted depth and node count succeeds.
29. The next node fails before admission to retained state.
30. Excess depth fails before the next directory call.
31. Multiple traversal steps share one cumulative node allowance.

#### Time and cancellation

32. Advancing fake time beyond the deadline produces `execution_time` budget failure.
33. A caller cancellation remains cancellation, not budget failure.
34. An explicit job cancellation remains `Cancelled`.
35. Results returned after fake deadline expiry are rejected before entering state.

#### Synchronous, job, and CSV paths

36. Synchronous and queued execution use identical configured ceilings.
37. A queued budget failure ends as `Failed` without a result cache key.
38. CSV enrichment receives a finite execution context and does not return partial success on global budget exhaustion.

#### Metrics

39. Success records one completion measurement with bounded counters.
40. Budget exhaustion increments exactly one counter with the correct dimension.
41. Metrics contain no user, query, LDAP, or row-value tags.

## Red/green guard strategy

For each implementation slice that adds a test:

1. Add a focused test that reproduces the reported failure.
2. Run it against the unmodified behavior and record the expected failure.
3. Implement the smallest fix.
4. Run the focused test and record success.
5. Temporarily revert only the implementation change, without rewriting history, and confirm the test fails.
6. Restore the implementation change and run `scripts/verify.ps1`.
7. Commit the single finding only after the focused and full guards pass.

Required representative mutation proofs:

- Change `MaxOutputRows` handling back to accepting zero; configuration validation test must fail.
- Move intermediate-budget checking after records are appended; sentinel test must fail.
- Skip operation consumption for fallback searches; operation-count test must fail.
- Replace checked template cardinality with unchecked multiplication; overflow test must fail.
- Convert budget deadline cancellation into generic cancellation; classification test must fail.
- Allow controller cache creation after budget failure; no-artifact test must fail.

Leave no test mutation in the worktree.

## Allocation and load benchmark

Add:

```text
benchmarks/
  AdQueryOrchestrator.Benchmarks/
    AdQueryOrchestrator.Benchmarks.csproj
    BudgetedExecutorBenchmarks.cs
```

Use BenchmarkDotNet with `MemoryDiagnoser`. Pin an explicit stable package version and audit it.

Benchmark parameters:

```text
Advertised source cardinality: 10,000; 100,000; 1,000,000
MaxIntermediateRecords: 1,000
MaxOutputRows: 500
Template cardinality: below limit and one above limit
Traversal source width: below limit and substantially above limit
```

The fake directory source must generate records only up to the finite request size rather than preconstructing the advertised source.

Measure:

- Allocated bytes.
- Records generated by the fake.
- Completed directory operations.
- Template combinations instantiated.
- Elapsed time as diagnostic data, not a hard CI threshold.

Required assertions outside BenchmarkDotNet:

- The fake never produces more than remaining capacity plus one sentinel for a single call.
- Raising advertised source cardinality from 100,000 to 1,000,000 does not increase generated application records when budgets are unchanged.
- Over-budget template cardinality performs zero LDAP calls.
- Over-budget traversal never admits more than the configured node ceiling.

Run command:

```powershell
dotnet run --project benchmarks/AdQueryOrchestrator.Benchmarks/AdQueryOrchestrator.Benchmarks.csproj -c Release -- --filter *BudgetedExecutor*
```

Do not gate CI on absolute elapsed milliseconds or absolute allocated bytes because runner hardware and runtime servicing vary. Commit benchmark source, not generated result directories. Record one baseline run in the implementation evidence.

P07 and P11 must extend this benchmark suite for export streaming and projection/aggregation allocation respectively.

## Dependency and ownership boundaries

### P05 — CSV enrichment scale and request limits

Landing order for the shared CSV surface is P06 before P05.

P06 owns:

- Creating a finite `QueryExecutionContext` for `QueryController.CsvEnrich` through `IQueryExecutionContextFactory`.
- Passing that context explicitly through `CsvEnrichmentService.ExecuteAsync` and its directory calls.
- Charging every physical CSV directory batch or retry against the shared directory-operation counter and deadline.
- Preserving typed P06 exhaustion and the no-partial-result rule.

P05 owns:

- Early CSV transport, row, column, cell, identifier, and output limits.
- Deduplication, bounded batch construction, ambiguity detection, and strictly reducing bisection.
- Preserving the P06 context when it changes the CSV and directory-service signatures.
- Aborting its entire enrichment when P06 exhausts during a batch or retry.

If P05 has partially landed when implementation begins, adapt P06 to the current signatures without adding an overload that constructs an unlimited or fresh per-batch tracker. A single CSV request owns one tracker across planning, all batches, all bisection retries, reconstruction, and publication.

### P07 — Streaming results, exports, and artifact caching

P06 owns:

- Finite output-row and intermediate-record ceilings.
- No artifact creation after budget failure.
- Bounded input to output processing.

P07 owns:

- Streaming projection/export interfaces.
- Output-byte limits.
- Avoiding full CSV/XLSX/JSON buffers.
- Cache representation and artifact lifetime.
- End-to-end output backpressure.

### P08 — Template expansion and LDAP filter complexity

P06 owns:

- Checked cardinality calculation.
- The cumulative template-combination ceiling.
- Failure before excessive allocation or calls.
- Counting each resulting directory operation.

P08 owns:

- Eliminating one-search-per-combination behavior.
- Batching and consolidating filters.
- Reducing Cartesian complexity.
- LDAP filter shape and server-side efficiency.

### P09 — Bounded and timeout-aware LDAP execution

P06 owns:

- Active execution deadline.
- Logical directory-operation accounting.
- Deadline checks before and after lower-level work.

P09 owns:

- Global LDAP concurrency.
- Dedicated workers for blocking APIs.
- `DirectorySearcher` server and client timeouts.
- Reliable interruption and disposal.
- Physical-operation and queue-wait telemetry.

Until P09 lands, P06's wall-clock deadline is cooperative around a blocking LDAP call and must be documented as such.

### P10 — Cycle-safe and bounded directory traversal

P06 owns:

- Cumulative traversal node and depth ceilings.
- Typed exhaustion.
- Sharing the budget across traversal steps.

P10 owns:

- Visited-set identity and case handling.
- Cycle prevention.
- Breadth/depth traversal algorithm.
- Duplicate-edge behavior.
- Traversal-specific performance.

### P11 — Indexed projection and single-pass aggregation

P06 owns:

- Bounding aggregation input by retained intermediate records and deadline.
- A finite distinct aggregation-group budget.
- Output-row enforcement over the final primary dataset during production.
- The minimal authority repair required for exactly-once accounting: propagate executor aggregation, remove queued recomputation, and remove the queued-only aggregation-to-rows heuristic.

P11 owns:

- Join indexes.
- Eliminating linear cross-step scans.
- Replacing the interim aggregation representation with structured keys and a typed contract.
- Single-pass aggregation.
- Projection and aggregation allocation benchmarks.

### P14 — Atomic, bounded query-job orchestration

P06 owns:

- Creating a finite execution context for a running job.
- Typed budget failure data.
- Preventing a result cache key after budget failure.

P14 owns:

- Queue capacity.
- Atomic state transitions.
- Cancellation races.
- Worker ownership and cleanup.
- Job retention and durable state.

## Implementation slices

Each numbered slice is one commit. Do not start the next slice until the current slice is verified and committed.

### Slice 1 — Replace unlimited configuration with validated finite options

Commit intent: `feat: require finite query budget configuration`

- Add `QueryBudgetOptions`.
- Add startup validation.
- Add the approved finite defaults.
- Remove `QueryDefaults:MaxResults`.
- Update controller and preprocessor configuration reads without yet changing deeper execution behavior; remove the old configuration-derived fallback from controllers so the finite options have one source.
- Add configuration and effective-limit tests.

Guard proof:

- Restore a zero default temporarily and confirm startup-validation tests fail.
- Restore finite configuration and run the full verification command.

### Slice 2 — Add the budget tracker and typed failure

Commit intent: `feat: add query work budget tracker`

- Add immutable limits, dimensions, snapshot, tracker, and exception.
- Include independent output-row and aggregation-group counters.
- Use checked atomic counters and injected `TimeProvider`.
- Add deadline and cancellation-source classification.
- Add tracker unit tests.
- Add `System.Diagnostics.Metrics` instruments and metric tests.

Guard proof:

- Disable one counter check and confirm its focused test fails.
- Misclassify fake deadline expiry as caller cancellation and confirm the classification test fails.

### Slice 3 — Enforce output and intermediate limits before materialization

Commit intent: `feat: bound query rows before materialization`

- Create one execution context per plan.
- Pass it through executor runtime.
- Apply effective output limits during projection.
- Remove redundant controller-side post-execution `Take` calls; the executor is the only primary-row enforcement site.
- Bound aggregation input by intermediate records and charge new buckets by `MaxAggregationGroups` only in the executor.
- Replace one-shot LINQ grouping with an incremental pre-insertion budget check.
- Carry executor aggregation on `PlanExecutionResult`, delete queued recomputation and the queued-only aggregation-to-primary-rows transform, and prove synchronous/queued counters match.
- Derive bounded directory request sizes from remaining intermediate capacity.
- Reject sentinel overflow before adding records to step state.
- Remove post-materialization hard-limit truncation.
- Retain successful lower requested-limit semantics.
- Add fake-directory output and intermediate-record tests.

Guard proof:

- Move the sentinel check after state insertion and confirm the no-partial-state test fails.
- Restore job-manager aggregation recomputation and confirm the exactly-once queued group-counter test fails.
- Restore the queued-only row transform and confirm the synchronous/queued primary-row parity test fails.
- Restore one-shot `GroupBy().ToDictionary()` and confirm the pre-insertion group-overflow guard fails.
- Restore controller-side truncation and confirm the single-enforcement-site architecture guard fails.
- Restore correct ordering and run verification.

### Slice 4 — Bound calls, templates, traversal, and active time

Commit intent: `feat: enforce cumulative query work dimensions`

- Pass the execution context through `IActiveDirectoryService`.
- Count every defined server-bound directory operation.
- Preflight template cardinality with checked multiplication.
- Generate only approved combinations.
- Apply cumulative traversal node and depth accounting.
- Check deadline before and after directory work.
- Create one finite CSV context through `IQueryExecutionContextFactory`, pass it through `CsvEnrichmentService.ExecuteAsync`, and charge every current lookup; P05 later preserves it across batching and bisection.
- Add fake-directory and fake-time tests.

Guard proof:

- Skip fallback-operation accounting, unchecked template multiplication, and post-call deadline checking one at a time.
- Confirm each focused guard fails with its mutation and passes after restoration.

### Slice 5 — Publish typed API and job failure contracts

Commit intent: `feat: expose query budget failures consistently`

- Add structured budget-failure data to execution results.
- Map synchronous exhaustion to RFC 7807 HTTP 422.
- Map queued exhaustion to `Failed` with no cache key.
- Map CSV exhaustion consistently.
- Prevent caching, export generation, and successful partial response creation.
- Preserve caller and job cancellation semantics.
- Add controller and job-manager tests using fakes.

Do not implement P14 atomic transitions in this slice.

Guard proof:

- Temporarily allow cache creation after exhaustion and confirm the no-artifact test fails.
- Temporarily map deadline exhaustion to `Cancelled` and confirm the job-state test fails.

### Slice 6 — Add allocation/load benchmark and operational guidance

Commit intent: `perf: benchmark bounded query execution`

- Add the BenchmarkDotNet project.
- Add budgeted executor benchmarks and deterministic bounded-work assertions.
- Ignore generated benchmark artifacts.
- Run and record one Release baseline.
- Document metric names, units, tags, and tuning procedure.
- Update `.agents/repo-guidance.md` only if the canonical verification command must include a deterministic benchmark assertion; do not add the full performance run to routine CI.

Verification:

```powershell
pwsh -NoLogo -NoProfile -File scripts/verify.ps1
dotnet run --project benchmarks/AdQueryOrchestrator.Benchmarks/AdQueryOrchestrator.Benchmarks.csproj -c Release -- --filter *BudgetedExecutor*
```

## Acceptance criteria

- Checked-in query-budget defaults are finite and positive.
- Startup fails for missing, zero, negative, or internally inconsistent budgets.
- No production directory-query path can construct an unlimited execution context.
- Request and plan limits can lower but cannot raise server ceilings.
- A query without an explicit result limit is still finite.
- Output rows are bounded during production rather than truncated only after full projection.
- Aggregation may examine every matched row within the intermediate-record/deadline bound even when the primary-row limit is lower.
- Distinct aggregation buckets are bounded independently, and an attempted bucket beyond the cap fails before insertion.
- Executor aggregation is authoritative; queued execution neither recomputes it nor rewrites primary rows from it.
- Synchronous and queued paths charge exactly N group units for N logical groups and charge projected output rows exactly once.
- `FindAll` directory requests are constrained by remaining intermediate capacity plus at most one sentinel; the current `RefreshCache` member path remains an explicit residual risk until P10 adds ranged retrieval.
- Intermediate records are counted cumulatively across all steps.
- Directory operations are counted before server-bound work.
- Template cardinality is checked with overflow-safe arithmetic before combination allocation.
- Traversal node and depth budgets are cumulative across traversal steps.
- Active execution time uses a monotonic clock and distinguishes deadline exhaustion from caller cancellation.
- Hard-budget exhaustion produces `query_budget_exceeded`.
- Hard-budget exhaustion returns no successful partial dataset.
- Synchronous exhaustion returns HTTP 422 problem details.
- Queued exhaustion produces `Failed` with no results cache key.
- CSV enrichment cannot bypass global budgets.
- Success, cancellation, generic failure, and budget exhaustion remain distinct outcomes.
- Structured metrics report utilization and exhaustion without high-cardinality or sensitive tags.
- Required fake-directory and fake-time tests pass without network or domain access.
- Mutation-based guard proof is recorded for every implementation slice containing tests.
- The allocation/load benchmark shows generated records remain bounded when advertised source cardinality increases.
- The full P01 canonical verification command passes.
- Each implementation slice is committed separately.
- No P07–P11 or P14 behavior is implemented beyond the boundaries stated here.

## Rollback

Use new revert commits; do not rewrite history.

- Reverting API contract mapping requires reverting corresponding clients and tests in the same rollback.
- Reverting lower-level enforcement while leaving finite configuration in place is unsafe because configuration alone does not bound materialization.
- Reverting the tracker requires restoring the former executor and directory-service signatures together.
- Do not restore `MaxResults: 0`; if the full plan must be rolled back, retain an emergency positive output cap in a separately reviewed safety commit.
- Metrics and benchmarks may be reverted independently only if enforcement and tests remain intact.
- P09, P10, and P14 must not remove P06 accounting when replacing execution internals.

## Risks and mitigations

- **Initial ceilings may reject legitimate large queries.** Emit utilization metrics, choose conservative but usable defaults, and tune configuration deliberately. Do not permit client-side bypasses.
- **Failure instead of partial success changes API behavior.** Use a typed stable contract and clear message; require explicit future design before supporting partial data.
- **Result-limit and budget-limit semantics can be confused.** Keep requested lower limits separate from hard ceilings and test exact-limit versus sentinel-overflow cases.
- **Counting only executor calls would undercount recursive or batched internals.** Pass the context to the directory-service boundary and consume at each server-bound unit.
- **`System.DirectoryServices` can block beyond the cooperative deadline.** Document the limitation and require P09 for hard LDAP interruption.
- **Sentinel reads add one record of work.** The single extra record is necessary to distinguish exact completion from overflow and remains bounded.
- **Multiple counters can charge one record.** Traversal records intentionally consume both traversal-node and intermediate-record budgets because they represent distinct resource dimensions.
- **Atomic counters add overhead.** The overhead is small compared with LDAP calls and protects future concurrent execution; measure it in the benchmark.
- **Metrics can leak sensitive or high-cardinality data.** Restrict tags to fixed enums and never tag user or query content.
- **Benchmark results vary by machine.** Gate deterministic work counts, not absolute time or allocation values; treat BenchmarkDotNet measurements as tuning evidence.
- **Changing service signatures touches CSV and job paths.** Compile and test every caller in the same slice; do not add an unlimited compatibility overload.
- **The interim aggregation authority repair overlaps P11.** Keep P06's change minimal: propagate the existing executor value and delete duplicate interpretation; P11 owns structured keys, typed contracts, indexes, and the optimized single pass.
- **P06 precedes P05, leaving a short-lived CSV memory residual.** Do not promote the combined CSV feature as bounded until P05's row/output limits land; the context only bounds calls and active time.
- **Later optimization plans may duplicate enforcement.** P07–P11 and P14 must consume the shared tracker instead of inventing parallel ceilings.

## Open owner decisions

### Decision 1 — Exhaustion behavior

When a hard safety ceiling is exceeded, choose between failing the query or returning explicitly marked partial data. Recommendation: fail with typed `query_budget_exceeded` and return no partial rows; partial data can mislead users and complicates caching, exports, aggregation, and retries.

Blocked until decided: Failure-contract implementation in Slices 3–5.

### Decision 2 — Initial finite ceilings

There is no production telemetry from which to derive limits. Approve initial defaults of 5,000 output rows, 25,000 intermediate records, 5,000 aggregation groups, 200 directory operations, 120 active seconds, 256 template combinations, 10,000 traversal nodes, and depth 10. Recommendation: start with these conservative ceilings and tune from low-cardinality utilization metrics.

Blocked until decided: Checked-in values in Slice 1.

### Decision 3 — Aggregation authority and output parity

The current queued path recomputes aggregation and sometimes replaces projected rows with group/count rows, while synchronous execution does not. Recommendation: make executor aggregation authoritative, expose it as metadata in `PlanExecutionResult`, and remove both queued-only behaviors in P06. This is the smallest reliable way to charge group and output budgets exactly once and make query modes agree.

Alternative: preserve the queued row transform through a trusted explicit output-mode contract with separate finalization accounting. That adds an accidental behavior mode before production and leaves P11 with more compatibility work.

Blocked until decided: Aggregation/output authority changes in Slice 3.

## Advisory Review

### Round 1 — 2026-07-21T20:44:05Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Revisions required

- Separated aggregation input from final primary-row output: intermediate records and deadline bound rows examined, `MaxOutputRows` bounds final primary rows, and a new finite `MaxAggregationGroups` counter bounds metadata cardinality.
- Defined exact-limit and overflow behavior for aggregation groups and required both current aggregation paths to share the tracker until P11 consolidates them.
- Added the P06-before-P05 landing order and the explicit controller-to-CSV execution-context wiring, including accounting for every P05 batch and bisection retry.
- Clarified the true Cartesian template path, the tighter of combination and directory-operation ceilings, and the linear in-memory template path.
- Recorded the current `RefreshCache` group-member allocation as a residual risk until P10 adds ranged retrieval.

### Round 2 — 2026-07-21T20:59:45Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Revisions required

- Confirmed the P05/CSV wiring, aggregation input/output separation, finite group cardinality, template-path distinction, traversal limits, and `RefreshCache` residual are coherent.
- Found that charging both current aggregation implementations would double-count logical groups and could falsely exhaust queued queries.
- Made `DirectoryPlanExecutor` the sole aggregation charging site, propagated its existing result, and removed the queued recomputation and queued-only group-to-row mutation from P06's target behavior.
- Added synchronous/queued exactly-once counter and primary-row parity guards.
- Recorded the pre-P05 CSV retained-row memory gap as an explicit interim residual.

### Round 3 — 2026-07-21T21:07:16Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Accepted

- Confirmed the executor-only aggregation authority resolves queued double-counting and that all eight P06 budget dimensions are finite.
- Confirmed every deferred dimension has a named plan owner and that the P06-before-P05 ordering has a safe partially-landed fallback.
- Applied optional clarity: remove redundant controller-side post-execution truncation and replace one-shot grouping with incremental group-budget checks before insertion.
- Identified a non-blocking P05 wording drift around global admission ownership; track that as a separate documentation slice rather than reopening P06.

Record no more than three headless Claude review rounds. Each round must identify material findings, the resulting revision or retained disagreement, and the reviewer's final assessment.
