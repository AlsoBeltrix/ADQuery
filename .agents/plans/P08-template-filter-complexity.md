# P08 — Bounded Template Expansion and LDAP Filter Complexity

Status: **Reviewed — implementation is not authorized**

Owner approval: Pending

Implementation dependencies: P01 verification foundation and P06 finite per-query work budgets must land first. Use the application target framework and canonical verification command present when implementation begins. P08 must preserve P06's single execution context, checked template-combination admission, directory-operation accounting, and no-partial-result rule.

Review status: Accepted in advisory round 2

## Problem

Template-bearing search steps currently have two unsafe and inefficient execution paths.

For a template that references more than one prior step, `DirectoryPlanRuntime.TryExpandTemplateFilters` eagerly materializes the Cartesian product of every referenced step's records. It clones a complete filter tree for each combination and `ExecuteSearchStep` issues one LDAP search per clone. Even after P06 makes the combination count finite, an admitted plan can still turn 256 combinations into 256 serial LDAP searches and allocate all expanded trees before the first result is processed.

For a template that references one prior step, `TryEvaluateTemplateSearch` first evaluates the downstream search filter against records already returned by the referenced step. If a referenced record happens to satisfy that filter, the executor returns that source record without querying the downstream target. For example, a downstream search for every user whose department equals `{{manager.department}}` can return only the manager record when the manager's own department matches. That is observably different from executing the generated LDAP filter.

The current complexity check does not bound the filter tree that reaches recursive normalizers and the LDAP renderer. `PlanValidator.ValidateComplexity` checks only `step.Filters.Count`, so one top-level logical group may contain an arbitrary number of nested predicates. The validator, preprocessor, runtime template walkers, in-memory evaluator, clone routines, and `ActiveDirectoryService.BuildFilterClause` all recurse through `DirectoryFilter.Conditions`. The README nevertheless claims a maximum of five predicates per step.

P06 prevents unbounded total query work and eager Cartesian allocation, but intentionally leaves template batching, correlation, canonicalization, LDAP filter shape, and server-side efficiency to P08.

## Repository evidence

- `csharp/Security/PlanValidator.cs` defines `MaxFiltersPerStep = 5`, but `ValidateComplexity` compares that limit only with the top-level `step.Filters.Count`.
- `ValidateComplexity` is also the only enforcement site for `Security:MaxPlanComplexity`; removing its Boolean filter check without migrating the step-count check would silently remove the documented ten-step ceiling.
- `PlanValidator.ValidateFilters` recursively validates children without tracking total nodes, leaf predicates, group arity, or depth. A group with a non-logical operator is accepted because logical and leaf operators share one allow-list.
- `ProjectionDefinition` exposes both plural `Filters` and singular `Filter`. The plural setter mirrors only a one-item list into the singular property, while `DirectoryPlanRuntime.Project` executes every plural filter and then any distinct singular filter. `PlanValidator.ValidateProjectionFilter` validates only the singular property, so a plural list with two or more entries currently bypasses projection attribute, operator, and complexity checks.
- `csharp/Services/PlanPreprocessor.cs` recursively normalizes every filter before execution and before the explicit validation endpoint calls `ValidatePlanAsync`.
- `DirectoryPlanExecutor.ExecuteSearchStep` invokes `TryEvaluateTemplateSearch` before template expansion.
- `TryEvaluateTemplateSearch` iterates records from the referenced step and can return those records as the downstream search result without calling `IActiveDirectoryService.SearchAsync`.
- `TryExpandTemplateFilters` calls `BuildRecordCombinations`, then clones and retains a `List<List<DirectoryFilter>>` for every Cartesian combination.
- `BuildRecordCombinationsRecursive` creates a new dictionary at every Cartesian leaf.
- `ExecuteSearchStep` issues one `SearchAsync` call for every expanded filter set and only afterwards deduplicates results by distinguished name.
- If every expanded search returns no records, the current method falls through to a search containing the original unresolved `{{...}}` text and then to person-search fallback logic.
- Template discovery, resolution, and replacement repeatedly run `TemplateRegex` over the same values in separate recursive passes.
- `ReplaceTemplatePlaceholders` uses `DirectoryRecord.GetString`, so repeated placeholders from the same source step are correlated to one source record and multi-valued attributes currently use their first value.
- `ActiveDirectoryService.BuildFilter` wraps all request filters in an implicit LDAP AND. `BuildCompoundFilterClause` recursively emits nested AND/OR clauses without an emitted-node, depth, or encoded-byte ceiling.
- `GetDirectReportsBatch` builds one OR group containing every supplied manager DN, so internally generated filters also need the final renderer boundary even though they did not originate in a plan filter.
- LDAP assertion values are escaped in `ActiveDirectoryService.EscapeLdapValue`, but the code never measures the final escaped UTF-8 filter sent to `DirectorySearcher`.
- No automated test project exists in the current tree. P01 establishes `tests/AdQueryOrchestrator.Tests`, fake-based unit testing, and the canonical verification script that P08 must extend rather than replace.

## Goals

1. Enforce finite, explicit filter-tree limits across nested step and projection filters.
2. Preserve the existing finite plan-step ceiling when replacing the current complexity method.
3. Reject ambiguous logical/leaf shapes before execution.
4. Parse template values once per execution into a bounded representation with explicit diagnostics.
5. Require every template reference to name an existing prior step and an available attribute.
6. Preserve correlations between multiple placeholders from the same source record.
7. Preserve the current Cartesian semantics between distinct referenced steps for admitted combinations.
8. Remove the incorrect in-memory shortcut and never send unresolved template syntax to LDAP.
9. Replace one-search-per-combination execution with exact-deduplicated, bounded OR batches.
10. Bound the actual LDAP filter's assertion count, node count, depth, branch count, and escaped UTF-8 byte length before a directory call.
11. Apply the same final renderer guard to internally generated LDAP filters.
12. Preserve P06 accounting exactly: raw Cartesian admission remains P06-owned, each generated combination is charged once, and each physical LDAP batch is one directory operation.
13. Stream combination generation and batch execution instead of retaining every expanded filter tree.
14. Return no successful partial result when template compilation, filter packing, or a P06 budget fails.
15. Carry validation and execution failure codes in typed fields rather than encoding them in display messages.
16. Add deterministic tests and a bounded-work benchmark without live Active Directory.
17. Emit only low-cardinality optimization metrics; never log filter text or substituted directory values.

## Non-goals

- Do not raise, duplicate, or bypass P06's cumulative template-combination or directory-operation ceilings.
- Do not change P06's typed `query_budget_exceeded` semantics.
- Do not introduce LDAP worker scheduling, hard operation timeouts, or global concurrency; P09 owns them.
- Do not redesign traversal or authoritative visited-set behavior; P10 owns it.
- Do not perform the repository-wide validation consolidation owned by P12. P08 supplies reusable structural and template diagnostics that P12 can later compose.
- Do not redesign the general API/job error envelope; P13 owns that contract. P08 defines stable internal error codes for P13 to map.
- Do not perform broad service decomposition; P21 owns it. Extract only the pure compiler, cost, and packing seams needed for this behavior and its tests.
- Do not use aggressive Boolean factorization that changes source-record correlation.
- Do not query live LDAP in the default automated suite.
- Do not promise stable ordering from LDAP. Preserve deterministic planner and first-seen merge order, but compare directory results by identity in tests.
- Do not expand a multi-valued template attribute into multiple values. Preserve the existing first-value behavior until a separate semantic design approves otherwise.

## Terminology and invariants

### Plan filter tree

Every `DirectoryFilter` instance is one explicit plan node.

- A **leaf** has no non-empty `Conditions` and has a non-logical filter operator, attribute, and value.
- A **group** has non-empty `Conditions`, uses only `and` or `or`, and has no meaningful attribute or value.
- Explicit depth starts at 1 for each root in `DirectoryPlanStep.Filters`, each root in `Projection.Filters`, and the singular `Projection.Filter` when it is not already the same object as a plural root. The implicit AND across a root list is not part of explicit plan depth.
- A shared object instance appearing at two locations counts twice. A reference cycle is invalid.
- A null root or null child is invalid rather than silently ignored.

### Template token

The accepted grammar remains `{{step_name.attribute}}`, with optional whitespace immediately inside the braces. Step and attribute lookup is case-insensitive, but values are not case-folded. Literal text may surround tokens. Unmatched delimiters, empty identifiers, extra separators, self references, forward references, unknown steps, and unavailable attributes are validation errors.

Templates are allowed only in leaf values of `search` steps. Projection filters and filters on other operations may not contain template tokens because their current execution paths do not resolve them.

Treat the plural and singular projection properties as one **effective projection filter forest**. Enumerate plural roots in list order, then add the singular root only when it is non-null and not already present by object identity. One shared helper must supply this exact forest to preprocessing, structural analysis, allow-list/operator validation, and `Project`; no projection root may reach `RecordMatchesFilter` through a different enumeration path. Apply the configured projection leaf/node/token totals to the forest in aggregate and the depth/value limits to every root/value, so many shallow plural roots cannot evade the ceiling.

`distinguishedName` is always available from a prior directory step because the directory service adds it to loaded attributes. Any other referenced attribute must be present in the referenced step's requested attributes after preprocessing.

### Resolved branch

One **resolved branch** is the complete conjunction represented by one template record combination after every token is substituted. It is not an independently submitted request. Multiple resolved branches are alternatives and therefore combine under LDAP OR.

Two branches are duplicates only when their normalized filter structures and ordinal values are exactly equal. Attribute and operator names may compare case-insensitively after normalization; directory values must compare ordinally. Do not use AD-specific case assumptions to remove branches.

### Correlation invariant

All tokens naming one source step in a resolved branch use the same source record. Thus records `(Alice, Smith)` and `(Bob, Jones)` generate:

```text
(givenName=Alice AND sn=Smith)
OR
(givenName=Bob AND sn=Jones)
```

They must never be factored into `(givenName=Alice OR Bob) AND (sn=Smith OR Jones)`, which admits the unintended cross-pairs Alice Jones and Bob Smith.

Distinct referenced steps retain current Cartesian semantics. P06 decides whether their raw cardinality is admissible before P08 expands them.

### LDAP filter cost

Cost is measured on the final escaped LDAP expression, including:

- The implicit outer AND.
- Object-category/object-class assertions.
- Every generated OR/AND/NOT wrapper.
- Special `Enabled` and `AccountExpirationDate` expansions.
- The complete UTF-8 byte count after RFC-style LDAP value escaping.

The filter string itself and substituted values are sensitive and must never be logged, used as metric tags, or included in client diagnostics.

### Failure invariants

- Plan-shape or template-reference failure is a validation failure with stable internal code `plan_filter_invalid` or `plan_filter_too_complex`; it performs zero LDAP calls.
- A resolved single branch that cannot fit an emitted LDAP ceiling fails with `ldap_filter_too_complex` before an LDAP call. It is never truncated.
- P06 Cartesian or operation exhaustion remains `query_budget_exceeded` with the P06 dimension.
- Any failure after earlier batches discards accumulated rows under P06's no-partial-result rule.
- Empty referenced source state produces an empty step result and zero LDAP calls; it is not an error.
- Missing values in a particular source record make that combination non-emittable, but the combination remains charged to P06 because it was generated. If all admitted combinations lack required values, the step returns empty without sending literal template syntax.

### Typed failure carriers

Add an immutable `PlanValidationIssue` with `Code`, safe `Message`, and structural `Path`. `PlanSecurityResult.FilterIssues` is the canonical collection for P08 validation findings; existing `SecurityErrors` and `PlanValidationResult.Errors` become display-only projections populated from `Message`. No caller may derive a code from those strings.

P06 adds `PlanExecutionResult.ErrorCode` and `BudgetFailure`. P08 reuses `ErrorCode`, adds `ValidationIssues` for rejected plans and a mutually exclusive `FilterFailure` detail (`LimitKind`, `Limit`, `Attempted`) for emitted-filter rejection:

```text
validation rejection:
  ErrorCode: plan_filter_invalid | plan_filter_too_complex
  ValidationIssues: one or more typed issues
  BudgetFailure: null
  FilterFailure: null

emitted-filter rejection:
  ErrorCode: ldap_filter_too_complex
  ValidationIssues: empty
  BudgetFailure: null
  FilterFailure: exact exceeded dimension and numeric values

P06 exhaustion:
  ErrorCode: query_budget_exceeded
  ValidationIssues: empty
  BudgetFailure: populated by P06
  FilterFailure: null
```

When multiple validation issues exist, choose the primary execution `ErrorCode` deterministically in plan/path order, with structural invalidity before structural excess; retain the complete typed issue list. Existing safe messages remain for current UI compatibility. P13 later maps these typed fields to its canonical API/job envelope without parsing display text.

## Proposed configuration

Add one startup-validated options section; callers and model output cannot override it:

```json
"Security": {
  "FilterComplexity": {
    "MaxPlanLeafPredicatesPerStep": 5,
    "MaxPlanNodesPerStep": 16,
    "MaxPlanDepth": 4,
    "MaxFilterValueUtf8Bytes": 1024,
    "MaxTemplateTokensPerStep": 16,
    "MaxLdapBranchesPerBatch": 32,
    "MaxLdapAssertionLeaves": 256,
    "MaxLdapNodes": 320,
    "MaxLdapDepth": 8,
    "MaxLdapFilterUtf8Bytes": 32768
  }
}
```

Apply the plan leaf/node/depth/value limits independently to each step. Apply leaf/node/token totals to the complete effective projection forest and depth/value limits to each projection root/value; reject template tokens in every singular or plural projection root. Preserve the existing `Security:MaxPlanComplexity` plan-step ceiling as a separate plan-level check. Together, the step ceiling and per-step/per-projection bounds make aggregate plan structure finite.

All values must be present and positive. `MaxPlanLeafPredicatesPerStep <= MaxPlanNodesPerStep`; the emitted limits must be large enough to represent at least one legal non-template search with object-class clauses. Validate relationships at startup and fail closed with option names but no user filter content.

Do not add another template-combination limit. `QueryWorkBudgets:MaxTemplateCombinations` from P06 remains the sole cumulative admission ceiling.

The numeric values are initial conservative limits and require owner approval. The five-leaf value matches the README's existing promise; unlike the current implementation, nested leaves count toward it.

## Technical design

### 1. One iterative structural analyzer

Add a pure `FilterComplexityAnalyzer` and immutable `FilterComplexityOptions`. Walk filter object graphs with an explicit stack carrying root location, current depth, and active-path identity. Do not recurse while inspecting untrusted plan shape.

For every step and the effective projection filter forest, compute:

- Total explicit nodes.
- Leaf predicates.
- Maximum explicit depth.
- UTF-8 bytes in each leaf value.
- Template-token count.
- Whether a reference cycle, null node, mixed group/leaf shape, invalid operator domain, or invalid delimiter exists.

Stop collecting additional detail after a small fixed diagnostic count, while retaining an invalid outcome. Diagnostics identify step number and structural path such as `filters[0].conditions[2]`; they never echo values.

Replace the top-level-only filter portion of `ValidateComplexity`, but preserve its plan-step ceiling. At the beginning of `ValidateSecurityAsync`, before `ValidateFilters`, `ValidateProjectionFilter`, or any other recursive filter helper:

1. Reject `plan.Steps.Count > Security:MaxPlanComplexity` with a typed `plan_filter_too_complex` issue and no LDAP work.
2. Run the iterative analyzer across every step root and the complete effective projection forest.
3. Set `PlanSecurityResult.ComplexityValid = false` and record typed issues on any invalid or over-limit shape.
4. Skip all recursive/detailed filter validation for each failed forest. It is permissible to continue non-filter validation that cannot traverse the failed graph.
5. Invoke attribute/operator/template validation only for analyzer-approved forests, whose depth and node counts are now bounded.

Remove the second Boolean-only complexity pass from `DirectoryPlanExecutor.ValidatePlanAsync`; do not keep two authorities with different limits. The plan-step and filter-tree checks both live in the retained `ValidateSecurityAsync` authority.

Rewrite `PlanPreprocessor.NormalizeFilter` as an iterative traversal with reference-cycle protection so preprocessing cannot overflow the stack before validation. Use the effective projection forest helper so both plural and singular projection filters receive identical treatment. It may normalize attribute/operator/value text, but it must not silently delete invalid nodes. P12 may later move preprocessing behind an authoritative validated-plan boundary.

The recursive runtime and renderer helpers may remain recursive only after the analyzer has enforced the small approved depth. Add defense-in-depth assertions at their entry points; do not depend on default JSON serializer depth as the application policy.

### 2. Bounded template parser and reference validation

Replace repeated `TemplateRegex` discovery/replacement passes with a parser that returns immutable literal and token segments. The parser consumes an already byte-bounded leaf value, rejects malformed brace sequences, and never returns a partially parsed template.

During analyzer-approved plan validation:

1. Build the case-insensitive step-name map and retain plan order.
2. For each search-step template token, require a strictly earlier referenced step.
3. Require `distinguishedName` or an attribute requested by that prior step.
4. Count every token occurrence against the per-step token ceiling.
5. Reject templates in every singular or plural projection root and on non-search operations.

At execution, compile each accepted search filter tree once. A compiled leaf retains normalized attribute/operator plus parsed value segments. No execution path reruns a regex for discovery, reference collection, and replacement.

If validation and execution are currently separate object traversals, reparsing the bounded values once at execution is acceptable. Do not introduce a mutable global compiled-plan cache. P12 may later carry the immutable compiled representation through an authoritative validation result.

### 3. Remove the in-memory and unresolved-template fallbacks

Delete `TryEvaluateTemplateSearch`, `EvaluateTemplateFilter`, `ResolveTemplateValue`, and their calls from normal and person-search fallback paths.

A search step containing any validated template always follows the template compiler/batcher path. It returns the merged batch result, including an empty result, and never falls through to:

- A raw LDAP request containing `{{...}}`.
- Person-name fallback using unresolved template text.
- Reuse of a prior step's record collection as the downstream result.

Non-template person-search fallback remains unchanged and continues to consume P06 directory-operation budget for every physical search.

### 4. Lazy correlated combination compilation

Introduce `EnumerateAdmittedTemplateCombinations` as the narrow P06/P08 seam if P06 has not already named an equivalent. It accepts the one `QueryExecutionContext`, preflights raw cardinality through P06, lazily yields record maps, and consumes one P06 combination unit before each yield. P08 owns its branch-compilation consumer, while P06 remains authoritative for admission and charging. Do not create a second counter or iterator with different semantics.

Order referenced steps by plan step number, then name as a deterministic tie-breaker; preserve source-record order within each step.

For each admitted combination:

1. P06 consumes one `template_combinations` unit before resolution.
2. Substitute every token from the combination's record map using current `GetString` first-value behavior.
3. If any required source value is absent, discard only that branch.
4. Produce the complete resolved conjunction as an immutable branch.
5. Compute a structural key without serializing or logging values.
6. Retain a collision-safe bounded set of keys and skip exact duplicate branches.
7. Offer each first-seen unique branch to the batch packer immediately.

The key set is bounded by P06's cumulative combination ceiling. Do not lower P06 admission based on deduplication: raw source cardinality is checked first exactly as P06 specifies, and every enumerated raw combination remains charged even when it resolves to a duplicate or lacks a value.

Do not perform Boolean algebra across branches. Whole-branch OR preserves conjunctions, negation, nested grouping, and source-record correlation.

### 5. Pure LDAP renderer and exact cost model

Extract LDAP expression rendering from `ActiveDirectoryService` into a pure component used by both planning tests and the service. It must be the sole implementation of:

- Object class/category clauses.
- Leaf operator rendering.
- AND/OR/NOT grouping.
- `Enabled` and `AccountExpirationDate` special clauses.
- LDAP assertion-value escaping.
- Final node, assertion-leaf, depth, and UTF-8 byte measurement.

Replace the direct `DateTime.UtcNow` read currently inside `BuildAccountExpirationDateFilterClause` with an injected `TimeProvider` or an explicit evaluation instant so candidate measurement and emitted text use one instant in deterministic tests.

Return an immutable rendered value plus its cost. The value's constructor must not be publicly usable to bypass rendering. At the final `DirectorySearcher.Filter` assignment boundary, reject a rendered value over any configured emitted ceiling before `FindAll` or other directory I/O.

Input-plan limits and emitted limits are intentionally separate. A legal five-leaf plan can expand special clauses or template alternatives into a larger generated filter, and the batch packer must use actual emitted cost rather than estimating from `DirectoryFilter` count.

### 6. Greedy bounded OR packing

Represent every resolved conjunction as one OR alternative. Pack alternatives in first-seen order using a deterministic greedy algorithm:

1. Start an empty batch.
2. Tentatively add the next branch under the implicit object-class AND and generated OR wrapper.
3. Render/measure the exact candidate shape.
4. If every emitted ceiling and branch-count ceiling still holds, retain it.
5. Otherwise flush the current non-empty batch and retry the branch in a new batch.
6. If the branch does not fit alone, fail `ldap_filter_too_complex` before any call for that branch.
7. Flush the final batch.

One branch may omit redundant OR/AND wrappers only when the renderer proves the expression is identical. Multiple branches must be ORs of complete conjunctions. The optimizer may remove exact duplicate nodes inside an AND/OR group but must not reorder non-commutative structure or case-fold values.

Execute one `IActiveDirectoryService.SearchAsync` per emitted batch. P06 consumes exactly one directory-operation unit immediately before each physical search. Pass the remaining useful P06 record capacity and the remaining step-wide size limit to each request, including the P06 sentinel where required. Stop scheduling later batches once the successful requested step limit is satisfied; a plan `size_limit` is not multiplied by the number of branches or batches.

Merge results by non-empty distinguished name, retaining the first-seen record. P06 charges a record only when it is first admitted to retained intermediate state. If any later batch fails or exhausts a hard budget, discard the accumulated set and propagate failure.

### 7. Internally generated filter paths

The final renderer ceiling applies to every `DirectorySearchRequest`, not only template-generated requests. Ordinary plan searches are already bounded by the plan analyzer. `GetDirectReportsBatch` can generate a larger internal OR, so route its manager-equality alternatives through the same packer and merge results by distinguished name.

Do not add an outer P06 directory-operation charge merely for entering `GetDirectReportsBatch`; charge each physical packed search exactly once at the existing service-boundary accounting point. P10 must preserve this packing and charging when it replaces traversal internals.

For a wide manager set, safe packing can consume more P06 directory-operation units than today's single oversized OR request. That is the intended safety trade-off: the operation budget may reject a traversal before all manager batches run, and P06's no-partial-result rule applies. Do not enlarge either the LDAP filter ceilings or P06 budget implicitly to preserve the old one-call count.

Keep the extraction minimal: a pure renderer and packer plus the narrow directory-search execution seam required for deterministic tests. P09 may decorate or replace the lower-level blocking transport, but must not bypass the renderer gate.

### 8. Metrics and logging

Reuse P06's template-combination and directory-operation instruments. Add low-cardinality histograms only where they answer optimization questions:

- `adquery.query.template_unique_branches`
- `adquery.query.ldap_filter_batches`
- `adquery.ldap.filter_utf8_bytes`
- `adquery.ldap.filter_nodes`
- `adquery.ldap.filter_depth`

Allowed tags are operation kind (`ordinary`, `template`, `direct_reports`) and outcome (`executed`, `rejected`). Do not tag or log plan descriptions, filter text, step names, attributes, distinguished names, values, canonical keys, or source cardinalities tied to identity. Log only numeric cost, configured limit, stable error code, and request correlation identifier already provided by the surrounding operation.

## Deterministic test design

Add tests beneath the P01 test project. Prefer hand-written fakes over a mocking package. The default suite must require no domain, LDAP server, IIS, LLM endpoint, clock, or network.

### Test seams

- Construct `FilterComplexityOptions` directly with small values.
- Use an in-memory configuration only for options-binding/startup-validation tests.
- Use a fake P06 execution context/tracker that records combination and directory-operation consumption.
- Use a fake `IActiveDirectoryService` for executor tests; record immutable copies of requests before returning scripted records.
- Inject a fixed `TimeProvider` into the renderer.
- Test the pure renderer and packer directly for exact boundary behavior.
- If the concrete directory service needs a lower transport seam to prove the final pre-I/O guard, introduce one narrow fakeable search transport. It accepts only a renderer-produced immutable filter and normalized attributes; it does not own scheduling, retries, or timeouts.

### Structural complexity tests

1. Five total leaf predicates nested under groups are accepted.
2. A single top-level group containing six leaves is rejected, reproducing the current top-level-count bypass.
3. Exactly the configured node and depth limits are accepted; the next node or depth is rejected.
4. Limits apply independently to each step and in aggregate across the effective plural/singular projection forest.
5. A group operator other than `and`/`or` is rejected.
6. A leaf with logical operator, a group with meaningful leaf fields, an empty logical group, a null node, and an object-reference cycle are rejected.
7. A multi-byte value is limited by UTF-8 bytes rather than UTF-16 character count.
8. Iterative preprocessing handles the maximum accepted depth and rejects/terminates safely on a cyclic programmatic graph without recursive overflow.
9. `ValidateSecurityAsync` sets `ComplexityValid = false` and returns the same stable diagnostic used by full plan validation.

### Template validation and semantics tests

10. Valid tokens with supported internal whitespace parse into the expected literal/token segments.
11. Unmatched braces, empty identifiers, extra separators, self/forward/unknown steps, unavailable attributes, and too many tokens are rejected without echoing values.
12. `distinguishedName` is accepted even when not explicitly listed by the prior step.
13. Templates in every plural or singular projection root and on non-search operations are rejected.
14. Two placeholders from one source step retain record correlation across separate leaf predicates.
15. Placeholders from two distinct steps retain the admitted Cartesian semantics.
16. Multi-valued source attributes use only the existing first value.
17. Missing source values discard only their branch; all-missing produces an empty step with zero LDAP calls.
18. Special characters are substituted as raw values and escaped exactly once by the renderer.
19. A template-bearing downstream step always calls the fake directory service when it has an emittable branch, even if a source record would satisfy the filter in memory.
20. Zero batched results return empty without a second request containing unresolved braces or invoking person fallback.

### Deduplication, packing, and accounting tests

21. Four raw combinations that resolve to one exact branch consume four P06 combination units but emit one branch.
22. Values differing only by case are not deduplicated.
23. A crafted structural-hash collision still uses full equality and retains distinct branches. Expose an internal comparer-injection constructor or test-visible hashing seam so the test can deterministically force a constant hash without changing production equality.
24. A batch exactly at every configured emitted ceiling is accepted.
25. The next branch, node, depth, assertion, or UTF-8 byte starts another batch when it fits alone.
26. A single branch over any emitted ceiling fails before the fake transport or directory service is invoked.
27. Escaped characters, object-class clauses, NOT wrappers, `Enabled`, and `AccountExpirationDate` expansions contribute to exact cost.
28. Every emitted request independently satisfies every final renderer ceiling.
29. 256 unique admitted combinations with five leaves and a 32-branch ceiling use at most eight LDAP searches unless a stricter byte/node/depth limit requires more; the test derives and asserts the deterministic expected batch count from its fixed values.
30. No test permits one search per admitted combination when multiple branches fit one batch.
31. Every batch consumes exactly one P06 directory-operation unit; no outer or duplicate charge occurs.
32. Operation-budget exhaustion before a later batch performs no later call and returns no partial result.
33. A step-wide size limit is shared across batches and stops later calls after enough unique records are admitted.
34. Duplicate directory results from separate batches are admitted and charged once by distinguished name.
35. Direct-report alternatives are split by the same packer and every physical search is charged once.
36. Cancellation between combination generation and batch execution prevents the next call.

### Integration and contract tests

37. A multi-entry `Projection.Filters` list is included in the effective forest; non-allow-listed attributes, over-limit aggregate nodes/leaves/depth, and template tokens are rejected before `RecordMatchesFilter`.
38. When the same filter object is present in plural and singular projection properties, it is normalized, analyzed, validated, and executed once.
39. A plan over `Security:MaxPlanComplexity` is rejected before LDAP after the old Boolean complexity method is removed.
40. An over-depth or cyclic graph reaching `ValidateSecurityAsync` is rejected by the analyzer and never enters recursive `ValidateFilters`.
41. Synchronous and queued execution reject the same over-complex plan before LDAP.
42. The explicit validate endpoint reports complexity invalid for nested step filters and multi-entry plural projection filters.
43. Typed fields distinguish invalid plan shape, plan complexity excess, emitted LDAP complexity, and P06 budget exhaustion without parsing messages.
44. Metrics contain only approved fixed tags and numeric measurements.
45. No unresolved `{{` or `}}` reaches the fake directory service in any generated request.

## Red/green guard strategy

For each behavior slice:

1. Add the focused test and run it against current behavior to record the expected failure.
2. Implement the smallest coherent change.
3. Run the focused test and record success.
4. Temporarily revert or disable only the targeted production behavior without rewriting history.
5. Confirm the focused test fails for the predicted reason.
6. Restore the change and run the P01 canonical verification command.
7. Leave no mutation, generated benchmark output, or test artifact in the worktree.

Required mutation proofs include:

- Restore `step.Filters.Count` as the only filter limit; the nested-six-leaf test must fail.
- Omit `Projection.Filters` from the effective forest; the plural projection allow-list/complexity/template guard must fail.
- Drop the migrated plan-step check with the old Boolean method; the over-step-limit test must fail.
- Restore recursive unguarded preprocessing or let `ValidateFilters` run before analyzer approval; the corresponding structural safety test must fail deterministically without inducing a process crash.
- Restore `TryEvaluateTemplateSearch`; the downstream-directory-call/result test must fail.
- Factor same-step fields independently instead of preserving whole branches; the Alice/Jones cross-pair guard must fail.
- Stop charging duplicate or missing-value raw combinations; the P06 accounting test must fail.
- Disable exact branch deduplication; the unique-branch/batch-count test must fail.
- Reintroduce one request per combination; the multi-branch call-count test must fail.
- Measure unescaped characters or UTF-16 length; the escaped multi-byte byte-boundary test must fail.
- Omit generated object/special clauses from cost; the exact renderer-cost test must fail.
- Bypass the final pre-transport renderer check; the oversized-filter zero-I/O test must fail.
- Reset `size_limit` per batch; the step-wide-limit test must fail.

Use filtered xUnit commands for red/green evidence, then the canonical verification script. Record exact commands and failing assertion names in the implementation evidence.

## Allocation and call-count benchmark

Extend the bounded executor benchmark project established by P06 rather than adding another benchmark project.

Benchmark fixed scenarios:

- 1, 32, 64, 128, and 256 unique admitted combinations.
- 256 raw combinations collapsing to 1, 16, and 256 unique branches.
- One and two referenced steps with correlation-preserving multi-leaf filters.
- Short ASCII, escaped-special-character, and bounded multi-byte values.
- Byte-, node-, and branch-limited packing.

Record operations, unique branches, batches, rendered bytes, and managed allocations. Do not gate CI on elapsed time or exact allocated-byte totals. Add deterministic assertions that:

- No eager complete expanded-filter collection is constructed.
- LDAP call count scales with emitted batches, not raw combinations.
- Raising duplicate raw combinations within P06's approved ceiling does not raise unique branch or batch count.
- No emitted batch exceeds a configured ceiling.
- An over-ceiling single branch performs zero directory calls.

Run the implementation machine's baseline once and record it as tuning evidence; do not commit generated benchmark output.

## Dependency and ownership boundaries

### P01 — Verification foundation

P08 adds focused xUnit tests to `tests/AdQueryOrchestrator.Tests` and runs P01's canonical `scripts/verify.ps1`. It does not create another solution, test project, analyzer policy, or verification entry point.

### P06 — Finite query-work budgets

P06 owns:

- Checked raw Cartesian cardinality before allocation or LDAP.
- The cumulative `MaxTemplateCombinations` ceiling.
- Lazy admission of only approved combinations.
- Cumulative directory-operation, intermediate-record, output, and deadline budgets.
- Typed budget exhaustion and no partial success.

P08 owns:

- Compiling each P06-admitted combination without an eager clone collection.
- Exact branch deduplication after P06 admission and charging.
- Correlation-preserving OR batching.
- LDAP expression cost and per-request shape ceilings.
- Reducing physical searches from combinations to bounded batches.

P08 must use the one P06 execution context. It must not create a fresh tracker per batch, use deduplication to evade raw combination admission, or recharge a physical operation at both outer and inner layers.

### P09 — LDAP execution

P08's renderer gate runs before the actual blocking call and remains mandatory when P09 adds scheduling, concurrency, and hard timeouts. P08 does not claim that a bounded filter makes `DirectorySearcher.FindAll` interruptible.

### P10 — Directory traversal

P08 packs the current direct-report OR filter so it cannot exceed renderer limits. P10 must preserve that packer, first-seen identity merge, P06 accounting, and final renderer gate when it redesigns traversal and cycle handling.

### P12 — Authoritative semantic validation

P08 makes filter complexity and template-reference diagnostics reusable and consistent but does not redesign all validation flow. P12 may compose them into a validated-plan artifact and remove duplicate orchestration passes; it must preserve P08 limits, token grammar, correlation rules, and zero-LDAP rejection tests.

### P13 — Error contracts

Until P13 lands, P08 uses stable internal codes and safe messages in existing validation/execution results. P13 later maps them to the canonical API and job envelope without parsing messages. P13 must keep `plan_filter_invalid`, `plan_filter_too_complex`, `ldap_filter_too_complex`, and P06 `query_budget_exceeded` distinguishable.

### P21 — Component decomposition

The pure analyzer, template parser/compiler, renderer, and batch packer are justified testing and safety seams. Do not use P08 to move unrelated executor, controller, or directory behavior. P21 may reorganize these components while preserving their contracts and tests.

## Implementation slices

Each numbered slice is one commit. Do not start the next slice until the current slice is verified and committed.

### Slice 1 — Enforce nested plan-filter structure

Commit intent: `feat: bound nested directory filter plans`

- Add startup-validated filter complexity options with owner-approved values.
- Add the iterative structural analyzer and stable diagnostics.
- Add typed `PlanValidationIssue` carriers and display-only message projections.
- Map analyzer-first validation rejection at `DirectoryPlanExecutor.ExecutePlanAsync` into P06's `PlanExecutionResult.ErrorCode` plus the complete `ValidationIssues` collection; leave `BudgetFailure` and `FilterFailure` null.
- Distinguish group and leaf operator domains and reject ambiguous/null/cyclic shapes.
- Reconcile plural and singular projection filters through one effective-forest helper and validate every executed root.
- Run the analyzer first inside `ValidateSecurityAsync`; gate recursive detailed validation on analyzer approval.
- Replace the Boolean top-level-only filter authority with analyzer results while migrating the existing plan-step ceiling into `ValidateSecurityAsync`.
- Rewrite preprocessor traversal iteratively with cycle protection.
- Apply structure rules to step and projection filters.
- Update checked-in configuration and README wording.
- Add options, analyzer, validation parity, and preprocessor safety tests.

Guard proof: restore the top-level-only count, omit plural projection roots, drop the migrated step ceiling, bypass iterative preprocessing, and enter recursive validation before analyzer approval separately; confirm each focused guard fails, restore, then run canonical verification.

### Slice 2 — Compile and validate template references

Commit intent: `feat: validate and compile filter templates`

- Add the bounded literal/token parser and immutable compiled representation.
- Validate prior-step and attribute availability, placement, delimiter grammar, and token counts.
- Replace repeated regex discovery/replacement for the execution path.
- Remove the in-memory template-search shortcut and all unresolved-template fallthroughs.
- Preserve first-value semantics explicitly.
- Add parser, reference, no-shortcut, no-unresolved-I/O, and empty-source tests.

Guard proof: restore the in-memory shortcut and allow malformed/unresolved text separately; confirm the targeted tests fail, restore, then run canonical verification.

### Slice 3 — Generate correlated branches lazily

Commit intent: `perf: stream correlated template branches`

- Consume P06's admitted lazy combination iterator.
- Preserve one-record-per-source-step correlation.
- Generate immutable complete conjunction branches one at a time.
- Add exact collision-safe structural deduplication after P06 charging.
- Eliminate the eager combination and cloned-filter-set collections.
- Add correlation, Cartesian, missing-value, duplicate, collision, cancellation, and P06 accounting tests.

Guard proof: independently introduce uncorrelated factoring, bypass raw-combination charging, and disable deduplication; confirm each focused guard fails, restore, then run canonical verification.

### Slice 4 — Render and pack bounded LDAP batches

Commit intent: `perf: batch template ldap filters safely`

- Extract the pure LDAP renderer and exact cost model.
- Add the deterministic greedy branch packer.
- Execute one LDAP search per batch and merge identities first-seen.
- Share step-wide size and P06 remaining-capacity limits across batches.
- Enforce the final pre-I/O renderer gate.
- Map emitted-filter rejection into P06's `PlanExecutionResult.ErrorCode` plus typed `FilterFailure`, leaving `BudgetFailure` null.
- Add exact-cost, edge-limit, batch-count, global-size-limit, no-partial, and zero-I/O rejection tests.

Guard proof: reintroduce one-search-per-combination, omit one generated cost category, bypass the final gate, and reset size limit per batch one at a time; confirm the respective guards fail, restore, then run canonical verification.

### Slice 5 — Bound internally generated filters

Commit intent: `feat: bound generated ldap filter batches`

- Route direct-report manager alternatives through the same packer.
- Ensure each physical batch consumes exactly one P06 operation.
- Keep the lower transport seam limited to renderer-produced filters and normalized attributes if one is required for deterministic proof.
- Add direct-report batch, merge, operation-count, and final-boundary tests.

Guard proof: bypass direct-report packing and duplicate the outer operation charge separately; confirm the boundary and exactly-once accounting tests fail, restore, then run canonical verification.

### Slice 6 — Add optimization evidence and operational guidance

Commit intent: `perf: measure bounded template filter batching`

- Extend P06's benchmark project with the fixed scenario matrix.
- Add low-cardinality metrics and privacy tests.
- Update prompt guidance to keep generated plans within the approved leaf/depth limits.
- Document stable validation outcomes, tuning procedure, and P09/P10/P12 handoff boundaries.
- Record one local benchmark baseline without committing generated output.

Verification includes the canonical script and the targeted benchmark command recorded by the P06 benchmark project.

## Acceptance criteria

- Nested filters cannot bypass leaf, node, depth, value-byte, or token ceilings.
- Multi-entry plural projection filters and the singular projection filter share one effective bounded and allow-listed execution forest; no projection root bypasses analysis.
- The documented finite plan-step ceiling remains enforced after the old Boolean complexity method is removed.
- Exactly five nested leaf predicates are accepted under the recommended default; a sixth is rejected before LDAP.
- Preprocessing does not recursively traverse untrusted filter graphs.
- The iterative analyzer runs before and gates every recursive detailed filter validator.
- Logical groups and leaves have unambiguous, validated shapes.
- Every template names an existing prior step and available attribute.
- Template values are parsed into a bounded representation and are not repeatedly regex-scanned during execution.
- The in-memory template shortcut and unresolved-template LDAP fallback are removed.
- Same-source placeholders preserve record correlation; distinct sources preserve admitted Cartesian semantics.
- P06 preflights and charges raw combinations before P08 deduplicates or discards branches.
- No complete expanded-filter-set collection is materialized.
- Exact duplicate branches are emitted once without case-folding directory values.
- Multiple fitting branches are sent as ORs of complete conjunctions, not one LDAP request per combination.
- Every final escaped LDAP filter is within branch, assertion, node, depth, and UTF-8 byte limits before directory I/O.
- A single un-packable branch performs zero LDAP calls and returns no partial result.
- A step-wide size limit and P06 retained-record capacity are not reset per batch.
- Each physical template or direct-report batch consumes exactly one P06 directory operation.
- Direct-report-generated OR filters use the same packer and renderer gate.
- Deterministic tests pass without Active Directory or external services.
- Mutation-based guard proof is recorded for every behavior slice.
- Benchmark assertions show LDAP calls scale with bounded batches rather than raw combinations.
- Metrics and logs contain no filter text, template values, distinguished names, or canonical branch keys.
- Validation, LDAP-filter, and P06 budget codes remain machine-distinguishable in typed carriers; display strings are never parsed.
- The P01 canonical verification command passes.
- Each slice is committed independently and no implementation begins while this plan remains unauthorized.

## Rollback

Use new revert commits; do not rewrite history.

- Slice 6 metrics, benchmark, and guidance may be reverted without removing enforcement tests.
- Reverting Slice 5 restores direct-report behavior but would reintroduce potentially oversized internal OR filters; do not retain the final renderer gate with an unbatched caller unless over-limit failure is explicitly accepted as the temporary behavior.
- Reverting Slice 4 restores one-search-per-combination behavior and removes the emitted-filter ceiling. Do not do so while claiming P08 is implemented; P06 remains a safety backstop but not an efficiency fix.
- Reverting Slice 3 requires restoring the prior template expansion code and its P06 bounded lazy-generation adaptation together. Never restore the original unbounded eager Cartesian collection after P06.
- Reverting Slice 2 restores the incorrect in-memory shortcut and unresolved-template fallthrough. Treat that as a functional rollback with explicit owner approval, not a harmless optimization rollback.
- Reverting Slice 1 restores the nested-filter limit bypass. If a full rollback is unavoidable, retain an emergency conservative request rejection at the plan boundary in a separately reviewed safety commit.
- Configuration and options validation must be reverted with their consumers; do not leave unused or falsely documented ceilings.
- P09, P10, P12, P13, and P21 must not remove P08 guards or tests when replacing adjacent internals.

## Risks and mitigations

- **Conservative whole-branch ORs do not minimize every Boolean expression.** They preserve behavior and still reduce calls through batching. Defer algebraic factoring until equivalence can be mechanically proven for the full operator set.
- **A batched filter can stress LDAP even when query work is finite.** Measure actual escaped shape and enforce independent per-request leaves, nodes, depth, branches, and bytes.
- **Initial limits may reject legitimate generated plans.** Keep errors stable, emit value-free utilization metrics, and tune server configuration deliberately; never allow request-side overrides.
- **Changing five top-level filters to five total leaves is a compatibility change.** It aligns implementation with existing documentation but requires owner approval and prompt/test updates.
- **Source attributes can be longer than input template literals.** Re-measure after substitution and reject an un-packable single branch before I/O.
- **Deduplication can change behavior if value equality is too broad.** Use ordinal value equality and collision-safe structural comparison; do not assume AD matching-rule case behavior.
- **Batching can change effective size-limit behavior.** Treat size limits as step-wide and share remaining capacity across batches, preserving P06's exact-limit/sentinel rules.
- **Later-batch failure could expose partial results.** Keep accumulation private until every required batch succeeds; propagate P06 or LDAP complexity failure without publishing rows or artifacts.
- **Renderer and service can drift.** Use one renderer implementation and enforce cost again at the final pre-I/O boundary.
- **Time-dependent expiration clauses can make tests or measurements inconsistent.** Inject one evaluation instant and render/measure from it.
- **Direct-report batching overlaps traversal internals.** Limit P08 to request packing and exactly-once operation charging; P10 owns traversal semantics.
- **A temporary transport seam can become architecture debt.** Keep it narrow and renderer-gated; P09 may replace it while retaining tests.
- **Validation is still spread across existing entry points.** Make P08's analyzer reusable and consistent, then let P12 establish the final authoritative validated-plan pipeline.
- **A bounded filter does not bound blocking duration.** P09 remains required for timeout-aware LDAP execution.

## Open owner decisions

### Decision 1 — Template consolidation semantics

Choose conservative whole-branch batching or more aggressive Boolean factoring. Recommendation: preserve each source record's complete conjunction, deduplicate exact branches, and OR-pack them; this cuts LDAP calls without creating cross-record matches, while aggressive factoring is smaller but can silently return wrong users.

Blocked until decided: Slices 2–4.

### Decision 2 — Initial filter complexity ceilings

Approve five plan leaves, 16 plan nodes, depth four, 1 KiB values, 16 template tokens, and emitted batches capped at 32 branches, 256 assertions, 320 nodes, depth eight, and 32 KiB. Recommendation: align the leaf cap with existing documentation and tune only from value-free metrics.

Blocked until decided: Configuration values and Slices 1, 4, and 5.

## Advisory Review

No more than three substantive headless Claude Code review rounds are permitted. Each round must record the actual assessment, every required finding, the applied revision or retained disagreement, and whether the resulting text was re-reviewed. If round 3 requires changes, apply them, state that they were not independently re-reviewed, and do not run a fourth round.

### Round 1 — 2026-07-21T22:30:49Z

**Reviewer:** Headless Claude Code 2.1.217 / configured model / maximum effort

**Assessment:** Revisions required

- **F1 — plural projection-filter bypass (critical):** The draft covered only singular `Projection.Filter`, while the runtime independently executes every root in plural `Projection.Filters` and the current validator ignores multi-entry plural lists. Added one effective projection forest used by preprocessing, analysis, detailed validation, and execution; aggregate bounds and template rejection now cover every executed root, with duplicate-identity and multi-entry tests.
- **F2 — plan-step ceiling dropped:** Removing `ValidateComplexity` as drafted would also remove its sole ten-step check. Required migration of `Security:MaxPlanComplexity` into analyzer-first `ValidateSecurityAsync`, plus a zero-LDAP regression and mutation proof.
- **F3 — recursive validator not gated:** The draft made preprocessing iterative but did not require the analyzer to run before recursive `PlanValidator.ValidateFilters`. Specified analyzer-first ordering, per-forest short-circuiting, and cyclic/over-depth validator guards.
- **F4 — typed validation code carrier unspecified:** Current validation models contain only strings and one Boolean. Added canonical typed validation issues, deterministic primary-code selection, `PlanExecutionResult` integration with P06, mutually exclusive filter/budget detail, and a no-message-parsing test.
- Applied the reviewer's optional clarity suggestions by naming the admitted-combination seam, making the branch hasher collision-injectable in tests, and naming `BuildAccountExpirationDateFilterClause` as the time-provider extraction site.

Round 1 changes will be submitted for advisory round 2.

### Round 2 — 2026-07-21T22:41:21Z

**Reviewer:** Headless Claude Code 2.1.217 / configured model / maximum effort

**Assessment:** Accepted

- Confirmed the effective projection forest closes the plural/singular execution and validation gap, including identity deduplication and aggregate limits.
- Confirmed `Security:MaxPlanComplexity` remains enforced after the old Boolean complexity method is removed.
- Confirmed analyzer-first ordering prevents cyclic or over-depth graphs from reaching recursive detailed validation.
- Confirmed typed validation issues, `ErrorCode`, `ValidationIssues`, `FilterFailure`, and P06 `BudgetFailure` keep all four failure classes machine-distinguishable without message parsing.
- Confirmed correlation-preserving OR batching, final escaped-filter enforcement, direct-report packing, P06 accounting, deterministic tests, and mutation proofs are internally coherent.
- Applied two optional clarity suggestions after acceptance: Slice 1 now names the executor validation-result mapping, and the P06/P10 boundary records that wide direct-report packing may intentionally consume more physical-operation budget than today's oversized single request.
- No third advisory round is necessary because round 2 reported no required findings; the optional wording additions do not change the accepted design.
