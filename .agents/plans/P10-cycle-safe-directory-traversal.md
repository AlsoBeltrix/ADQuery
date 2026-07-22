# P10 — Cycle-Safe and Bounded Directory Traversal

**Status:** Reviewed — implementation is unauthorized. Owner decisions are unresolved. All three advisory rounds accepted the technical plan; two optional Round 3 wording/test clarifications were applied after the final review and were not re-reviewed because no fourth round is permitted.

## Finding

Recursive directory traversal is neither identity-safe nor consistently bounded at the directory boundary.

`expand_members` recursively calls `ActiveDirectoryService.ExpandGroupMembersAsync` without carrying a visited set, depth, node allowance, or execution context through recursive calls. A nested-group cycle can therefore recurse until stack exhaustion or budget/deadline intervention outside the recursive method. It also reads the complete `member` property with `DirectoryEntry.RefreshCache`, so groups above Active Directory's multivalue response range can be incomplete or allocate beyond the intended traversal allowance.

`expand_reports` uses breadth-first levels and a case-insensitive distinguished-name set, but a DN is a mutable locator rather than canonical object identity. It marks only the current frontier visited, admits directory-return order into results, and truncates after materialization when a local node limit is crossed. Cyclic, convergent, renamed, duplicate, and wide graphs can therefore produce duplicate or unstable results, misleading cycle telemetry, excess work, or successful partial output.

Evidence was verified against commit `5a49390afd3b9dd656318e1de5c72519bfb947f1`.

- `csharp/Services/ActiveDirectoryService.cs:86-153` implements group expansion recursively inside the directory adapter.
- `ActiveDirectoryService.cs:94-127` reads `member` once with `RefreshCache(new[] { "member" })`; it does not request or validate ranged attributes.
- `ActiveDirectoryService.cs:141-149` recursively calls itself with no visited set or depth/node parameter.
- `ActiveDirectoryService.cs:123-126` catches per-group exceptions and continues, returning an indistinguishable partial result.
- `ActiveDirectoryService.cs:155-207` resolves member DNs with a local `Parallel.ForEach`, a hard-coded concurrency of four, and `ConcurrentBag`, making completion order nondeterministic and bypassing P09's target scheduler ownership.
- `csharp/Services/DirectoryPlanExecutor.cs:339-351` delegates `expand_members` to the opaque recursive adapter, then applies only a coarse group/non-group output filter.
- `DirectoryPlanExecutor.cs:376-495` implements a separate report traversal with local defaults, materialized level lists, and DN-based visited state.
- `DirectoryPlanExecutor.cs:422-426` marks the current frontier visited only after it has already been admitted.
- `DirectoryPlanExecutor.cs:446-454` detects the local node limit after receiving a complete batch, truncates with `Take`, and returns success with a warning.
- `DirectoryPlanExecutor.cs:459-464` suppresses revisits only when constructing the next frontier; repeated nodes may already exist in the retained level.
- `DirectoryPlanExecutor.cs:483-487` flattens directory-return order, which Active Directory and the current concurrent lookup path do not define.
- `DirectoryPlanExecutor.cs:489-493` derives a “cycles detected” count by subtracting unrelated collection counts; the value is not a valid cycle or duplicate-edge count.
- `csharp/Models/DirectoryRecord.cs` exposes DN and attributes but no internal immutable directory identity.
- `csharp/Security/PlanValidator.cs:448-510` validates depth and nodes only for `expand_reports`; recursive `expand_members` has no equivalent semantic validation.
- `csharp/appsettings.json:126-130` supplies per-step recursion settings, while P06 establishes separate cumulative query budgets.

## Desired Outcome

- Group membership and direct-report expansion use one iterative traversal engine.
- Every production directory object participating in traversal has a canonical immutable `objectGUID` identity.
- Seeds, visited nodes, duplicate edges, and cycles are evaluated by canonical identity rather than DN spelling.
- Traversal is breadth-first, non-recursive, stable, and deterministic.
- Each unique discovered node is admitted at most once per traversal step.
- Seeds are excluded from output and pre-marked visited so back-edges cannot re-emit them.
- Recursive membership retrieves every `member` value through validated bounded LDAP ranges.
- P06 remains the sole owner of cumulative node, depth, intermediate-record, directory-operation, and deadline budgets.
- P08 remains the sole owner of compiled filters, escaping, filter-size bounds, and lookup/search batching.
- P09 remains the sole owner of all blocking LDAP scheduling, concurrency, timeouts, cancellation bridges, and physical-operation telemetry.
- Hard-budget, identity, malformed-range, and operational failures publish no successful partial result.
- Results and metrics contain no DN, GUID, user value, or LDAP filter leakage.

## Scope

### Included

- Internal canonical directory identity.
- Iterative breadth-first traversal for `expand_members` and `expand_reports`.
- Visited-set, cycle/revisit, convergent-edge, and duplicate-edge semantics.
- Stable depth and result ordering.
- Bounded ranged retrieval of group `member` values.
- Target-type filtering after actual object-type resolution.
- P06 budget integration at node admission and before server work.
- P08/P09 gateway integration.
- Traversal validation required by the existing plan model.
- Safe progress, logs, metrics, deterministic tests, and benchmarks.

### Excluded

- New query-budget dimensions or total ceilings; P06 owns them.
- LDAP filter compilation, escaping, batching limits, or Cartesian/template behavior; P08 owns them.
- LDAP worker count, queueing, parallelism, client/server timeout values, blocking-call disposal, or physical-operation accounting; P09 owns them.
- General semantic-plan compiler architecture; P12 later absorbs P10's validation rules without changing them.
- Public error envelopes and cancellation taxonomy; P13 owns them.
- Job transitions, artifact publication, and partial-result policy outside traversal; P14 and P07 own them.
- Generic graph libraries, graph persistence, transitive-closure caching, or live change subscriptions.
- Transactionally consistent Active Directory snapshots. LDAP range reads and searches can observe concurrent directory changes.
- Changes to projection or aggregation order after traversal; P11 owns those stages.

## Dependencies and Boundaries

### P01 — verification foundation

P01 must land first. P10 adds deterministic fake-directory tests and extends the one canonical verification command; it does not create another verifier.

### P04 and P12 — policy and semantic validation

P10's internal `objectGUID`, `objectClass`, `distinguishedName`, and `manager` reads are operational metadata, not user-selectable output attributes. They must travel through a distinct internal projection and never bypass authorization for plan-requested output attributes.

P10 adds the minimum semantic validation needed to make both traversal operations safe. P12 later makes the canonical compiler authoritative and must preserve these rules. P10 must not create a second long-lived validator once P12 lands.

### P06 — cumulative work budgets

P06 owns:

- `MaxTraversalNodes` and `MaxTraversalDepth`;
- cumulative sharing across all traversal steps;
- `MaxIntermediateRecords`, `MaxDirectoryOperations`, and active execution time;
- exact-limit versus attempted-over-limit semantics;
- `QueryBudgetExceededException`, `query_budget_exceeded`, and no successful partial result.

P10 consumes the same `QueryExecutionContext` and tracker for every traversal step and server-bound unit. It must not create local replacements for those counters.

P10 may require a remaining-capacity/sentinel query on P06's tracker so P08 requests and membership ranges can be constrained before materialization. That method remains a view over P06's canonical counter; it is not another allowance.

The effective per-step depth and node scope is the minimum already defined by P06 across the validated step, security defaults/ceilings, and remaining cumulative budget.

### P08 — compiled filters and batching

P10 supplies ordered frontier locators and a remaining-capacity sentinel to P08. P08 owns:

- compiled equality/disjunction filters for manager and DN lookups;
- LDAP escaping;
- maximum filter bytes and clauses;
- splitting a frontier into safe request batches;
- setting a bounded result size on each compiled search.

P10 never concatenates an LDAP filter string or invents a second batch-size option. Ranged `member;range=<start>-<end>` attribute names are generated only from checked integers by the membership page adapter; they contain no user-controlled filter text.

For direct reports, P10 preserves P08's deterministic first-seen distinguished-name merge, shared step-wide result capacity, exact packer, and final renderer gate. After P08 returns that bounded merged result, P10 resolves canonical identity, suppresses duplicate GUIDs, and imposes traversal order. This later GUID admission does not change P08's ownership or within-layer merge contract.

For membership identity resolution, P10 supplies first-seen member locators as `distinguishedName` equality branches to P08's existing pure renderer and greedy bounded OR packer. P08 performs its bounded first-seen DN merge; P09 schedules each resulting physical search; P10 then applies canonical-GUID admission and ordering. P10 adds no LDAP compiler, packing limit, or per-member production bind fallback. P08 is a prerequisite, so an unavailable packing path fails closed instead of degrading into one operation per member. The separately described one-bind compatibility resolution remains limited to a legacy seed that lacks internal identity.

### P09 — sole LDAP scheduler

Every search, base-object identity resolution, membership range page, and batched lookup executes through P09's scheduler. P10 does not use `Parallel.ForEach`, `Task.Run`, `Task.WhenAll`, raw `DirectorySearcher.FindAll`, or `DirectoryEntry.RefreshCache` outside the P09 adapter.

P09 owns physical concurrency, blocking workers, timeouts, cancellation/disposal, queue metrics, and overload behavior. P10 awaits gateway operations and sorts returned nodes after completion, so scheduler completion order cannot affect results.

P10 never consumes a P06 directory-operation unit around a gateway wrapper or before P09 admission. Submission checks the P06 deadline without consuming a unit. After a worker successfully claims an admitted server-bound item, P09 atomically consumes exactly one P06 unit at its final pre-ADSI gate. P06's post-operation deadline check still runs after P09 returns success. Saturation rejection, queued cancellation, queue timeout, and shutdown before start consume zero units; a worker-claimed unit is not refunded. P10 does not add an outer charge for entering P08's direct-report helper.

P10 replaces P09's provisional whole-property `ILdapBlockingOperations.ReadGroupMembers` operation with a one-range-page operation on that same blocking adapter and scheduler; it does not introduce a second ADSI adapter or execution path. The traversal gateway adopts P10's atomic integrity policy while leaving P09's general non-traversal best-effort lookup behavior unchanged. P09's typed cancellation, deadline, saturation, timeout, shutdown, and fatal dependency failures always propagate.

### P11, P13, P14, and P21

- P11 consumes the deterministic traversal records and does not re-sort, deduplicate, or reinterpret graph identity.
- P13 maps P10's internal integrity/operation failures and preserves cancellation causality.
- P14 and P07 publish nothing when traversal or a P06 budget fails.
- P21 may move the traversal engine into a smaller component but must preserve this identity, ordering, range, and budget contract.

## Safety and Correctness Invariants

1. `objectGUID` is the only production visited-set and output-deduplication key.
2. A DN is an opaque lookup locator, never canonical identity.
3. No DN parser, case folding beyond ordinal-ignore-case locator deduplication, Unicode normalization, or string rewriting attempts to canonicalize a DN.
4. Missing or malformed `objectGUID` after a directory resolution is an integrity failure, never a DN-identity fallback.
5. Actual object type comes from internal directory metadata, not the caller's requested fallback type.
6. Every seed identity enters `visited` before the first edge is read.
7. A discovered identity enters `visited` at admission, before it can enter another frontier or output.
8. One identity appears at most once in the output of one traversal step.
9. A revisit consumes neither another traversal-node unit nor another intermediate-record unit.
10. A unique discovered node consumes one P06 traversal-node unit even when it is only an internal nested-group connector and is not emitted.
11. A node consumes an intermediate-record unit immediately before its record enters retained step output.
12. Seeds consume no new traversal-node or intermediate-record unit in this step because their source step already admitted them.
13. Depth zero is the seed set. Direct children are depth one.
14. `recursive = false` membership reads one level only.
15. Recursive traversal uses no CLR recursion and cannot overflow the call stack.
16. Exactly reaching a validated depth or node allowance is not failure. Attempting to admit one additional unique node produces P06's typed failure before retention.
17. The engine never submits child discovery for a frontier beyond the effective maximum depth.
18. Budget, timeout, cancellation, integrity, or LDAP failure produces no traversal records for successful publication.
19. Membership pages are finite, strictly progressive, and validated before their values enter traversal.
20. Directory and scheduler return order never affects final output order.

## Owner Decisions Required

### P10-D1 — canonical identity

**Recommendation:** Require a valid Active Directory `objectGUID` for every seed and discovered object, keep it as internal non-serialized metadata, and fail atomically if identity cannot be established. Never fall back to distinguished-name equality.

DN fallback is more permissive but cannot safely recognize renamed objects or semantically equivalent DN encodings and can therefore miss cycles or duplicate output.

### P10-D2 — traversal and output semantics

**Recommendation:** Use breadth-first traversal for both operations. Exclude seeds, emit each discovered object once only when its actual type equals `target_type`, and use nested groups as internal frontier nodes even when they are not emitted. Accept `User`, `Group`, and `Computer` as membership target types; reject `OrganizationalUnit` for `expand_members` before directory work.

The compatibility alternative preserves the current coarse “group versus non-group” filter, which can return users for a computer target and non-groups for a group target.

### P10-D3 — deterministic order

**Recommendation:** Order results by ascending traversal depth and then by the lowercase `N` representation of `objectGUID` using ordinal comparison. Sort every next frontier the same way after global identity deduplication.

Preserving server or scheduler completion order is cheaper but makes previews, exports, tests, and limit-boundary selection nondeterministic.

### P10-D4 — stale references and operation failures

**Recommendation:** Fail the traversal atomically when a seed or membership DN cannot be resolved, identity/range metadata is malformed, access is denied, or LDAP work fails. Treat cancellation separately through P09/P13. Do not silently skip stale edges or publish an aggregate “best effort” graph.

Skipping stale member DNs improves availability during directory churn but returns an incomplete graph whose missing nodes cannot be distinguished from valid filtering.

### P10-D5 — membership range size

**Recommendation:** Add a startup-validated `DirectoryTraversal:MemberRangePageSize` default of 500 values, constrained to 1–1500. Each page is further reduced to P06's remaining traversal sentinel capacity when smaller.

A 500-value page keeps transient DN storage bounded while retrieving a 10,000-node traversal in approximately 20 range reads before identity-resolution batches. P06 still caps total operations; P08 and P09 own batch and concurrency tuning.

**All decisions must be recorded and the plan status changed to `Approved` before implementation begins.**

## Canonical Identity Contract

Add an internal value type such as:

```text
DirectoryObjectIdentity
  ObjectGuid: Guid
  StableSortKey: ObjectGuid.ToString("N")
```

Attach identity to the runtime record through an internal, non-serialized property or a runtime wrapper. Do not place it in the user-visible `Attributes` dictionary under a magic key.

All directory reads that can feed traversal request internal metadata independently of plan-selected attributes:

- `objectGUID` for identity;
- `objectClass` or equivalent schema metadata for actual type;
- `distinguishedName` as the current locator;
- `manager` when correlating direct-report batches.

The record mapper must accept only an actual `Guid`, a 16-byte AD `objectGUID`, or another explicitly guarded provider representation. It must not hash a DN or call `ToString()` on an unknown value.

Internal operational metadata is stripped from user-visible attributes unless the validated plan independently requested and authorized the corresponding public attribute.

Source records should already carry identity after the directory mapper is updated. A compatibility source lacking internal identity may be resolved once through the P08/P09 gateway before traversal; that resolution consumes P06 directory work. Production traversal fails if identity remains unavailable.

## Unified Traversal Contract

Introduce one engine, for example:

```text
IDirectoryTraversalEngine.TraverseAsync(
  DirectoryTraversalRequest request,
  QueryExecutionContext context,
  CancellationToken cancellationToken)
    -> DirectoryTraversalResult
```

The request contains:

- kind: `GroupMembers` or `DirectReports`;
- canonical seed nodes;
- target object type;
- recursive flag;
- effective maximum depth and per-step node allowance calculated under P06;
- authorized output attributes;
- step number for safe diagnostics, not the model-generated step name.

The result contains:

- ordered unique output records;
- deepest completed depth;
- unique nodes admitted;
- total edges observed;
- revisited/duplicate edges suppressed;
- membership range pages;
- identity-resolution batches;
- safe warnings approved by this plan.

It contains no DN, GUID, LDAP filter, raw directory error, or physical scheduler detail.

## Breadth-First Algorithm

For each traversal step:

1. Validate and resolve all seed identities.
2. Deduplicate seeds by `DirectoryObjectIdentity` and sort by stable identity key.
3. Add every seed identity to `visited` before reading edges.
4. Set the seed frontier at depth zero; seeds are not output.
5. For each next depth up to the effective maximum:
   - check caller cancellation and the P06 deadline;
   - observe the depth through P06 before any child-discovery call;
   - ask the traversal gateway for bounded candidates from the current frontier;
   - resolve candidate locators to canonical nodes through P08/P09;
   - sort candidates by stable identity key;
   - for each candidate, suppress it if already visited;
   - otherwise consume one P06 traversal node, then add it to `visited`;
   - if actual type matches `target_type`, consume one intermediate-record unit and append it to this depth's output;
   - if the node is traversable for this operation and another depth is allowed, add it to the next frontier.
6. Sort the next frontier by identity and continue.
7. Concatenate depth outputs in ascending depth order.

`visited` and all frontiers are local to one traversal step. P06 counters remain shared across steps. A node rediscovered by a later traversal step is a new node for that step's graph semantics and consumes another cumulative traversal unit; the query-wide tracker still bounds total work.

The validated step/security depth is semantic traversal scope, while P06's `MaxTraversalDepth` is the hard ceiling and defense-in-depth guard. Reaching the effective depth boundary completes normally and emits no warning: no deeper edge has been observed, and no child read is submitted merely to prove one exists. P10 still calls P06's depth observation before each permitted child-discovery call. P06's excess-depth guard and required test remain independently reachable by a direct tracker test and by a mutated engine that attempts the next depth; both must fail before LDAP. This reconciles normal boundary completion with P06's exact-limit rule without restoring the current warning-plus-truncation behavior.

### Group membership

- The frontier contains groups only.
- Each group is read through the ranged membership adapter.
- Every member DN is resolved to actual type and canonical identity.
- Non-group objects are terminal.
- Nested groups are terminal when `recursive` is false.
- Nested groups enter the next frontier when `recursive` is true and another depth is allowed.
- Matching target records are emitted whether terminal or traversable.

### Direct reports

- The frontier contains users acting as managers.
- P08 compiles and batches manager equality filters.
- P08 merges each bounded batch sequence by non-empty DN in first-seen order; P09 charges every worker-claimed physical search once at its final pre-ADSI gate, and P10 adds no wrapper charge.
- P10 requires `manager`, `objectGUID`, actual type, and DN as internal correlation metadata.
- P10 applies canonical-GUID visited admission and identity ordering only after receiving P08's bounded, first-seen merge.
- A returned record that cannot be correlated safely to the submitted manager frontier is an operation-integrity failure, not an ignorable result.
- Every newly discovered user may enter the next frontier until the depth boundary.

### Duplicate and cycle behavior

- A self-edge is suppressed because the source identity is already visited.
- A back-edge to a seed or ancestor is suppressed.
- A diamond/convergent edge emits the shared descendant once.
- Multiple textual DNs resolving to one GUID emit one object.
- Duplicate values across pages or batches are suppressed after identity resolution.
- The safe metric is `revisited_edges`; do not label every revisit an exact graph cycle.

## Bounded Membership Range Retrieval

Replace P09's provisional whole-property `ILdapBlockingOperations.ReadGroupMembers` member read with a one-page operation on the same blocking adapter. That operation performs one base-object LDAP read for a checked attribute name:

```text
member;range=<start>-<requested-end>
```

For each page:

1. Check cancellation and the P06 deadline before submission without consuming a directory-operation unit; P09 owns the atomic worker-claim charge immediately before ADSI.
2. Calculate `requestedCount` as the minimum of the approved page size and remaining traversal capacity plus one sentinel, never below one while testing for another unique node.
3. Calculate `requestedEnd = checked(start + requestedCount - 1)`.
4. Submit exactly one base read through P09.
5. Accept exactly one case-insensitive response property of:
   - `member;range=<start>-<numeric-end>`; or
   - `member;range=<start>-*` for the terminal page.
6. A plain `member` response may be accepted only on the initial request as a terminal interoperability form and must still contain no more than `requestedCount` values. A larger plain response violates the adapter's bounded response contract and fails immediately with the fixed internal cause `membership_range_response_invalid`; it is not relabeled as P06 exhaustion. P06 remains authoritative when valid bounded pages reveal the next unique node beyond budget.
7. Validate that the returned start equals the requested start, numeric end is at least start and no greater than requested end, value count is bounded by requested count, and the next start increases strictly under checked arithmetic.
8. Treat a present group with no member property on the initial read as an empty terminal membership.
9. Reject duplicate range properties, malformed suffixes, overlaps, gaps, nonprogress, excessive value counts, or arithmetic overflow.
10. Deduplicate locator strings ordinal-ignore-case before identity resolution, then deduplicate canonically in the traversal engine.

Each page and each subsequent physical P08 identity-resolution batch executes through P09. Every worker-claimed physical item consumes the P06 logical operation counter exactly once at P09's final pre-ADSI gate. Rejected or queued-tombstoned items consume zero; a claimed unit is not refunded. Do not count a complete group traversal as one opaque operation, and do not add an outer charge around a P08 helper.

The adapter returns only one bounded page at a time. It never builds the complete member-DN collection before the traversal engine processes pages.

## Limits and Failure Semantics

P10 adds only the range-page-size option. Total work continues to use P06.

Before server work:

- calculate effective step depth/node scope from the validated plan and P06;
- use P06 remaining intermediate/traversal capacity plus one sentinel as applicable;
- let P08 apply filter and result-size bounds;
- submit through P09 after its non-consuming deadline check; P09 alone consumes one directory-operation unit after worker claim and immediately before ADSI.

At node admission:

- duplicates consume no node unit;
- a new identity consumes exactly one node unit;
- matching output consumes exactly one intermediate-record unit;
- exact capacity succeeds;
- the next new identity fails before `visited`, frontier, or output retention.

P06 budget exhaustion remains `query_budget_exceeded`. P10 supplies sanitized internal causes for missing identity, malformed range metadata (including `membership_range_response_invalid` for an oversized plain response), unsafe correlation, or traversal gateway failure; P13 owns their public status, retryability, and problem details.

Do not catch and continue after a P09 operation failure. Do not convert timeout or cancellation into an empty edge set. Do not truncate and return success at a node ceiling.

## Validation

Until P12 replaces the current validator, validate:

- `expand_members` and `expand_reports` require an existing earlier source step;
- source step types are compatible with the operation;
- `expand_reports` targets `User`;
- `expand_members` accepts only `User`, `Group`, or `Computer` target types, rejects `OrganizationalUnit`, and traverses only actual groups;
- `max_depth` and `max_nodes`, when present, are positive and no higher than security/P06 ceilings;
- omitted values receive the existing security defaults capped by P06;
- nonrecursive membership has effective depth one;
- recursive traversal is rejected when recursive queries are disabled;
- requested attributes are authorized independently of internal identity/correlation metadata.

Validation produces no LDAP call and does not mutate the model plan. P12 later emits the same effective traversal request in its validated-plan wrapper.

## Progress, Logging, and Metrics

Progress reports use committed traversal state only:

- nodes admitted;
- output records retained;
- current completed depth;
- phase from a fixed enum.

Remove heuristic remaining-node estimates unless P06/P14 has a truthful bounded contract for them. Do not expose speculative counts as progress facts.

Structured per-step logs may include:

- operation kind;
- recursive flag;
- seed count;
- depth reached;
- nodes, edges, revisits, outputs, pages, and batches;
- fixed completion or failure reason.

Never log or tag DNs, GUIDs, manager values, member values, filters, source step names, queries, or raw exceptions containing directory paths.

Suggested metrics, using the application meter established by P06:

```text
adquery.directory.traversal.requests
adquery.directory.traversal.nodes
adquery.directory.traversal.edges
adquery.directory.traversal.revisits
adquery.directory.traversal.depth
adquery.directory.membership.range_pages
adquery.directory.traversal.duration
adquery.directory.traversal.failures
```

Allowed tags are traversal kind, recursive flag, fixed outcome, and fixed failure reason. P09 remains the only source of LDAP queue, active-worker, timeout, and physical-operation metrics.

## Implementation Slices and Commits

Each slice addresses one item, receives its own red/green proof, and is committed before the next begins. Do not amend, squash, or combine completed commits.

### Slice 1 — canonical runtime identity

**Commit:** `feat(directory): add canonical traversal identity`

- Add the internal identity value type/runtime metadata.
- Request and map internal `objectGUID`, actual object type, and current DN on traversal-capable directory reads.
- Keep operational metadata out of serialized/user-visible attributes.
- Add strict mapping, missing/malformed identity, alternate-DN, and serialization tests.

Do not change traversal algorithms in this commit.

Guard proof:

1. Temporarily restore DN-based identity in the focused identity test.
2. Confirm two locators for one GUID are treated as different and the guard fails.
3. Restore GUID identity.
4. Confirm focused tests and canonical verification pass.

### Slice 2 — ranged group membership adapter

**Commit:** `fix(directory): read group membership in bounded ranges`

- Add the validated range-page option.
- Add the pure response-property parser and page result.
- Add the P09-scheduled base-read adapter.
- Charge every page through P06.
- Add empty, terminal, multipage, malformed, nonprogress, overflow, limit, cancellation, and failure tests.

Do not switch recursive expansion yet.

Guard proof:

1. Temporarily restore one whole `member` read or assume a fixed 1500-value response.
2. Confirm the 1501-plus fixture is incomplete or the bounded-read architecture guard fails.
3. Restore progressive range retrieval.
4. Confirm all focused tests and canonical verification pass.

### Slice 3 — cycle-safe traversal engine

**Commit:** `feat(directory): add deterministic bounded traversal engine`

- Add the unified iterative BFS engine over a fakeable gateway.
- Integrate P06 depth, node, intermediate, operation, deadline, and cancellation accounting.
- Implement visited-on-admission, seed exclusion, actual-type filtering, and deterministic ordering.
- Add graph-shape and budget tests without contacting LDAP.

Do not wire production operations in this commit.

Guard proof:

1. Temporarily move visited insertion from admission to dequeue or replace GUID keys with DNs.
2. Confirm self-cycle, diamond, or alternate-DN fixtures duplicate or fail to terminate under the deterministic fake bound.
3. Restore admission-time GUID tracking.
4. Confirm focused tests and canonical verification pass.

### Slice 4 — migrate group membership traversal

**Commit:** `fix(directory): make group expansion cycle safe`

- Route `expand_members` through the engine and ranged gateway.
- Resolve member locators as P08-rendered and packed `distinguishedName` equality batches, preserve P08's first-seen DN merge, and route every physical search through P09.
- Apply target-type and recursive semantics.
- Remove the recursive `ExpandGroupMembersAsync` implementation and direct whole-property read.
- Add semantic validation for membership traversal.

Guard proof:

1. Temporarily route the recursive fixture through the former self-recursive implementation.
2. Confirm the nested-group cycle fails the bounded termination/operation-count guard.
3. Restore the engine route.
4. Confirm focused and full verification pass.

### Slice 5 — migrate direct-report traversal

**Commit:** `fix(directory): make report expansion identity safe`

- Route `expand_reports` through the same engine.
- Use P08 compiled/batched manager filters and P09 exclusively.
- Require safe manager correlation metadata.
- Remove the executor-local traversal, truncation, heuristic estimate, and invalid cycle count.
- Preserve P06/P13 failure propagation.

Guard proof:

1. Temporarily restore server-order result admission or DN-based visited state.
2. Confirm shuffled scheduler results or renamed-node cycles fail exact-output guards.
3. Restore identity sorting and shared traversal.
4. Confirm focused and full verification pass.

### Slice 6 — telemetry and benchmark

**Commit:** `perf(directory): measure bounded traversal`

- Add safe metrics and structured events.
- Add the opt-in deterministic benchmark cases.
- Record the command, environment, and baseline.
- Add leakage tests with sentinel DNs/GUIDs.

Do not change traversal semantics or limits in this slice.

## Deterministic Tests

Use P01's canonical test project. Use an in-memory graph gateway, fake P09 scheduler, fake `TimeProvider`, and P06 execution context. No automated test contacts Active Directory or sleeps.

### Identity

1. Same GUID with case-different DNs is one node.
2. Same GUID with differently escaped/moved DNs is one node.
3. Distinct GUIDs are distinct even when locator test data differs only by case.
4. Missing, wrong-length, or unsupported identity values fail.
5. Internal identity and operational attributes are not serialized or projected unless independently authorized.
6. Actual object type, not requested fallback type, controls output and frontier behavior.

### Graph behavior

7. Empty seeds return empty output with zero edge calls.
8. Seeds are excluded and a back-edge to a seed is suppressed.
9. A self-cycle terminates and emits no duplicate.
10. `A -> B -> A` terminates.
11. A longer nested-group cycle terminates without CLR recursion.
12. A manager cycle terminates.
13. A diamond emits the shared descendant once.
14. Duplicate edges in one page, across pages, and across parents emit once.
15. The shallowest discovery wins when one node is reachable at multiple depths.
16. Nonrecursive membership reads and emits depth one only.
17. Recursive membership traverses nested groups and emits only the requested actual `User`, `Group`, or `Computer` type; `OrganizationalUnit` is rejected before the gateway.
18. Group target output contains groups, not unrelated non-group members.
19. Report traversal accepts only safely correlated users.

### Ordering

20. Results are depth-first in the ordering sense of depth ascending, then identity ascending; traversal itself remains breadth-first.
21. Randomized server order produces identical results over repeated seeds.
22. Reversed scheduler completion produces identical results.
23. Case, locale, and current culture do not alter identity ordering.

### Ranged membership

24. Empty group completes with one bounded page.
25. Fewer than one page completes.
26. Exactly one full terminal page completes.
27. 1,501 and 10,000 members are retrieved without whole-property materialization.
28. Server-returned pages smaller than requested advance by the returned end.
29. Terminal `*` stops further reads.
30. Initial plain `member` interoperability response is bounded and terminal.
31. Malformed range name, mismatched start, gap, overlap, duplicate property, excessive values, and nonprogress fail; an oversized plain response uses `membership_range_response_invalid`, not `query_budget_exceeded`.
32. Checked end/next-start overflow fails before LDAP work.
33. Every worker-claimed page or identity batch consumes one P06 logical operation after claim and immediately before fake ADSI; saturation rejection, queued cancellation, queue timeout, and shutdown before start consume zero, while a claimed unit is not refunded.
34. A P09 timeout or cancellation stops further pages and yields no successful partial result.

### Budgets and failure integrity

35. Exactly permitted depth and nodes succeed.
36. The next unique node fails before visited/frontier/output retention.
37. Duplicates at the node ceiling do not falsely exhaust it.
38. Multiple traversal steps share one cumulative P06 allowance.
39. Internal nested groups consume traversal nodes even when not output.
40. Output nodes consume intermediate-record capacity exactly once.
41. The engine never schedules a child read beyond effective depth; direct P06 and mutated-engine excess-depth guards fail before a fake LDAP invocation.
42. Directory-operation exhaustion prevents the next P09 invocation.
43. Deadline expiry after a gateway return prevents candidate admission.
44. Caller cancellation remains cancellation, not budget or traversal failure.
45. Identity, correlation, range, and operational failures return no step data, artifact, cache entry, or completed job.

### Leakage and architecture

46. Sentinel DN, GUID, member, manager, and filter values appear in neither logs, metrics, nor client failures.
47. No production traversal path uses CLR recursion.
48. No production traversal path invokes `Parallel.ForEach`, direct blocking LDAP, or an unscheduled directory bind.
49. No second LDAP filter compiler or batch-size option exists in P10.
50. No second cumulative depth/node/operation counter exists outside P06.

## Red-Green Guard Proof

For every test-bearing slice:

1. Implement the slice and run its focused tests green.
2. Temporarily reverse only the protected production behavior with a patch.
3. Run the focused guard and record the intended failure.
4. Restore production behavior with a patch.
5. Run the focused tests and canonical verification green.
6. Leave no mutation or test artifact in the worktree.
7. Commit only the restored single-item slice.

Minimum non-vacuous proofs:

- replace GUID identity with DN equality: alternate-locator cycle/duplicate guard fails;
- mark visited on dequeue: self-cycle or diamond guard fails;
- restore recursive group calls: bounded cycle guard fails;
- restore one-shot `member`: 1,501-plus range guard fails;
- trust server order: shuffled-result guard fails;
- charge duplicate nodes: exact-limit duplicate guard fails;
- check node budget after append: no-partial/admission guard fails;
- bypass P09 for one page: scheduler architecture guard fails;
- catch one LDAP failure and continue: atomic failure guard fails.

## Benchmark Plan

Extend the established benchmark project with a deterministic fake gateway. No benchmark contacts production AD.

Graphs:

- chains of depth 1, 10, and the approved maximum;
- balanced trees of 100, 1,000, and 10,000 unique nodes;
- wide one-level frontiers;
- 50% and 90% convergent/duplicate edges;
- self-cycle, long cycle, and dense cyclic graphs;
- membership groups with 1,499, 1,500, 1,501, 5,000, and 10,000 values;
- shuffled completion order;
- mixed terminal users/computers and nested groups.

Record:

- elapsed time and allocations;
- unique nodes, edges, revisits, and maximum frontier size;
- visited-set size;
- range pages and identity-resolution batches;
- P06 logical directory operations;
- fake P09 submissions and maximum active work;
- sort time by depth;
- records retained and emitted.

Deterministic acceptance outside wall-clock benchmarks:

- one output per unique matching identity;
- visited/frontier/output memory is `O(V)` within P06 limits;
- edge processing is `O(E)` plus deterministic per-level sorting;
- CLR call-stack depth is constant;
- range page storage never exceeds the configured page size;
- shuffled completion does not change output;
- duplicate density does not increase node-budget consumption;
- no case exceeds P06 node, intermediate, operation, or depth ceilings.

Do not place absolute time thresholds in CI. Record the environment before drawing a performance conclusion.

## Manual Staging Checks

Against a non-production directory fixture under an approved test account:

- expand a group with more than 1,500 members and compare the exact unique GUID count with an independent directory administration tool;
- expand nested groups containing a deliberate cycle;
- expand a direct-report diamond or temporary manager cycle;
- verify repeated runs return identical ordering;
- cancel during a later membership page and during a report batch;
- verify LDAP scheduler concurrency/timeouts remain P09's configured values;
- inspect logs and metrics for identity or DN leakage.

If no approved directory fixture exists, report these checks as not run; do not use production identities as test data.

## Acceptance Criteria

P10 is complete only when:

- P01, P06, P08, and P09 prerequisites are landed.
- Required owner decisions are durably recorded.
- The plan status is `Approved`.
- Both traversal operations use one iterative BFS engine.
- Every production visited key is a valid `objectGUID`.
- Seeds, cycles, convergent paths, alternate DNs, and duplicate edges cannot duplicate output.
- Output is deterministic by depth and canonical identity.
- Group membership retrieves and validates all bounded LDAP ranges.
- No traversal path reads the whole `member` property or recursively calls itself.
- P06 is the only owner of cumulative traversal/depth/intermediate/operation/deadline accounting.
- P08 is the only owner of LDAP filter compilation and batching.
- P09 is the only owner of blocking LDAP execution, scheduling, concurrency, and timeouts.
- Exact limits succeed and the next unique node fails before retention.
- Failures and cancellation produce no successful partial result or artifact.
- Internal identity/correlation metadata does not become user-visible data.
- Logs, metrics, and public failures contain no DN, GUID, member, manager, or filter values.
- Every new behavioral guard has documented revert-fails/restore-passes evidence.
- The benchmark command and one baseline are recorded.
- Canonical verification passes.
- Advisory review is resolved in no more than three substantive rounds.

## Rollback

Use new revert commits; do not rewrite history.

- Identity metadata may remain when reverting a later traversal consumer; it is non-serialized and useful to later safe implementations.
- A range-page parser or adapter can revert independently before production wiring.
- Do not restore recursive group traversal or whole-property `member` reads as a production fallback. Disable recursive membership until repaired.
- Do not restore DN-based visited state for recursive traversal. Disable the affected operation if GUID identity is unavailable.
- If P08 lookup compilation fails, retain traversal validation and fail the operation rather than concatenate filters locally.
- If P09 scheduling fails, retain traversal validation and fail rather than restore `Parallel.ForEach` or raw blocking calls.
- Do not restore warning-plus-truncation at a hard P06 ceiling.
- No persisted schema or data migration is introduced by P10.

## Risks and Mitigations

- **`objectGUID` adds an internal read to directory operations.** Keep it outside output authorization and load it with existing bounded requests rather than a separate bind where possible.
- **Legacy/fake records lack identity.** Resolve once through P08/P09 or fail; never silently use DN identity.
- **AD membership can change between range pages.** Deduplicate by GUID and validate page progress. Document that reads are not transactional; do not claim a point-in-time snapshot.
- **A stale member reference may make a query fail under the recommended policy.** This protects completeness. Revisit only through an explicit partial-data contract.
- **Page size trades operations for transient memory.** Keep it finite, startup-validated, and tune from P06/P09 metrics.
- **Range syntax is provider-specific.** Isolate and unit-test it in the AD adapter; the traversal engine sees only bounded pages.
- **GUID sorting is not human-friendly.** It is stable and locale-independent; user-requested business sorting would be a separate semantic feature.
- **Internal connector groups consume traversal nodes.** This is intentional because they consume graph work even when not output.
- **Large duplicate-edge graphs can perform work without many new nodes.** P06 directory-operation limits plus bounded pages and P08 result sizes keep work finite; do not add a competing unapproved edge budget.
- **Batch results lose parent association.** Require internal `manager` correlation for reports and fail if it is absent or ambiguous.
- **P08 or P09 may land with different type names.** Adapt P10 to their authoritative contracts; do not recreate equivalent compilers, batches, or schedulers under the names used here.
- **P12 later consolidates validation.** Preserve P10's semantic rules and delete the temporary validator path when P12 becomes authoritative.
- **Failure semantics are stricter than current best effort.** The application is not in production; choose correctness before clients depend on incomplete graphs.
- **Metrics can leak identity.** Use fixed tags and aggregate counts only.
- **Benchmarks can overstate timing conclusions.** Gate work counts, ordering, memory bounds, and operation counts; treat elapsed time as environment-specific.

## Advisory Review

Use no more than three headless Claude Code review rounds with the configured model, maximum effort, structured JSON, read-only tools, and a strict empty MCP configuration. Each round records material findings, revisions or retained disagreements, and the reviewer's final assessment. If Round 3 requires a revision, apply it and explicitly record that the final revision was not re-reviewed; do not run Round 4.

### Round 1 — 2026-07-21T22:17:20Z

- Harness: Claude Code 2.1.217, configured `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8`, maximum effort, structured JSON, `Read`/`Grep`/`Glob` only, strict empty MCP, no session persistence, and no `--model` override.
- Assessment: `accepted`.
- Required changes: none.
- Reviewer conclusion: canonical GUID identity, iterative BFS semantics, ranged membership validation, failure integrity, adjacent-plan ownership, slices, deterministic guards, acceptance, rollback, and risks were implementable without a material invented contract.
- Optional clarification accepted: membership DN resolution now explicitly reuses P08's equality renderer, greedy OR packer, and first-seen DN merge before P10 GUID admission. The suggested unbatched P09 bind fallback was deliberately not adopted because P08 is a prerequisite and such a fallback would undermine bounded batch work; only a single legacy seed may use the already-specified compatibility bind.
- Optional clarification accepted: an oversized plain `member` interoperability response is now classified as `membership_range_response_invalid`; valid bounded-page discovery beyond remaining capacity remains P06 `query_budget_exceeded`.
- Optional clarification accepted: membership target types are explicitly `User`, `Group`, and `Computer`; `OrganizationalUnit` is rejected before I/O.
- Optional clarification accepted in part: the plan now reconciles semantic depth scope with P06's hard defensive guard and tests both. No boundary warning was added because reaching an approved scope is normal completion and no deeper edge has been observed.

Round 2 reviewed these clarifications and the unchanged overall contract.

### Round 2 — 2026-07-21T22:23:15Z

- Harness: Claude Code 2.1.217, configured `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8`, maximum effort, structured JSON, `Read`/`Grep`/`Glob` only, strict empty MCP, no session persistence, and no `--model` override.
- Assessment: `accepted`.
- Required changes: none.
- Confirmed: the four Round 1 clarifications preserve P06/P08/P09 ownership and leave identity, range progression, BFS ordering, no-partial behavior, commits, guard proofs, acceptance, rollback, and risks cold-agent implementable.
- Optional clarification accepted: P09's now-authoritative charging contract is stated exactly throughout P10. Submission checks deadline but consumes no unit; saturation and queued tombstones consume zero; a worker claim consumes once at the final pre-ADSI gate and is not refunded. P10 adds no wrapper charge.

Because that wording changed after Round 2, Round 3 is limited to confirming the accounting repair and detecting any resulting regression. It is the final permitted review round.

### Round 3 — 2026-07-21T22:26:44Z

- Harness: Claude Code 2.1.217, configured `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8`, maximum effort, structured JSON, `Read`/`Grep`/`Glob` only, strict empty MCP, no session persistence, and no `--model` override.
- Assessment: `accepted`.
- Required changes: none.
- Confirmed: every P10 accounting reference matches P09's worker-claim/final-pre-ADSI charge, zero-charge pre-start outcomes, non-refund after claim, and no-wrapper rule. P06/P08 ownership, bounded range retrieval, failure atomicity, deterministic tests, and cold-agent implementability remain intact.
- Optional clarification applied after the final review: the canonical paragraph now attributes the post-success deadline check to P06 after P09 returns success, matching the authoritative P09 wording.
- Optional clarification applied after the final review: deterministic accounting test 33 now names saturation rejection, queued cancellation, queue timeout, and shutdown before start as zero-charge outcomes.
- The two final edits are editorial extensions of the accepted contract but were not re-reviewed. The three-round limit is exhausted; do not run Round 4.
