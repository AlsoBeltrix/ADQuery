# slice1-or-1: Grouping reads the group_by attribute from rows keyed by projection column name

**Severity**: HIGH — a plan that projects its grouped attribute under a display name produces one `(empty)` bucket holding every record, so the headline, the follow-up value slice, and every Slice 1 export report a false distribution with no error.

**Status**: Verified
**Branch**: — (fix on `master`, one commit)
**Commit**: `f261e50` (defect), fix commit below

## Evidence

- `csharp/Services/DirectoryPlanExecutor.cs:601` — projection writes each value under the column's **display name**: `row[column.Name] = value`. The row dictionary is `OrdinalIgnoreCase`-keyed by `Name`, and the source attribute (`column.Attribute`, read at `:594`) never appears as a key.
- `csharp/Services/QueryJobManager.cs:445-448` — aggregation looks each `group_by` entry up as a row key directly: `row.TryGetValue(field, out var value)`, where `field` comes from `AggregationDefinition.GroupBy` — an **attribute** name. When `Name != Attribute` the lookup misses and `:448` yields `"(empty)"` for every row.
- `csharp/Configuration/prompt_template.txt:57` — the shipped example instructs exactly the mismatching shape: `projection columns = [{ name: 'Department', attribute: 'department' }], aggregation = { group_by: ['department'] }`. Here `"Department"` vs `"department"` survives only because the row dictionary is case-insensitive; any display name that is not a case-variant of the attribute (`name: 'Cost Center'`, `attribute: 'extensionAttribute1'`) breaks.
- `tests/AdQueryOrchestrator.Tests/Unit/GroupedResultSettlementTests.cs:34,47` — the Slice 1 guard writes a plan with `Name = "Cost Center"` / `Attribute = "extensionAttribute1"`, then builds its fixture rows keyed by the **attribute**. The fixture therefore models a row shape the real projection never produces, and the guard passes over the defect.
- `csharp/Services/DirectoryPlanExecutor.cs:231` — a second, duplicate `ComputeAggregation` (`:507-534`) with the identical defect. Its output is assigned to `RuntimeResult.Aggregation` (`:1558`), which `ExecutePlanAsync` at `:60-64` never copies onto the public `PlanExecutionResult` — the type has no such property (`csharp/Services/IDirectoryPlanExecutor.cs:23-38`). The whole second implementation is dead code and a standing divergence hazard.

## Predicted observable failure

A user asks "what's the breakdown of cost centres?" against a plan projecting `extensionAttribute1` as `Cost Center`. Every record folds into a single `(empty)` bucket. The `grouped` headline reports one category covering the whole population; the CSV/HTML/xlsx export Slice 1 just introduced writes a one-row distribution table reading `(empty), 47388`; the follow-up value slice (`FollowUpContextBuilder.cs:91-96`) feeds that same fabricated single bucket to the next turn's model. No warning and no error is raised at any point. Slice 1 raises the stakes: before it, the distribution was a comment block beneath the real rows, so the underlying data still reached the user; now the distribution **is** the exported table, so a wrong distribution is the entire answer.

## What

Two defects with one root cause — aggregation was written against the *pre-projection* record shape (attribute-keyed) but is executed against the *post-projection* row shape (display-name-keyed):

1. **Unresolved alias.** `group_by` names attributes; projected rows are keyed by column names. The lookup only works when the two coincide, which the prompt's own example makes accidental.
2. **Duplicate dead implementation.** `DirectoryPlanExecutor.ComputeAggregation` carries the same bug, is never read, and guarantees the two copies drift.

## Approach

Resolve each `group_by` field to the row key that actually carries it, rather than assuming the attribute is the key: try the field as a key first (covers a plan whose column name equals its attribute, and the pure case-variant of the prompt example), then fall back to the `Name` of a projection column whose `Attribute` matches the field. A field resolvable by neither route is not present in the data at all — that now emits a warning onto the job rather than silently contributing an `(empty)` component, converting a false answer into a visible one.

Delete the dead `DirectoryPlanExecutor` copy and its `RuntimeResult.Aggregation` carrier, leaving one aggregation implementation. Rewrite the Slice 1 guard's fixture to build rows the way `Project` builds them — keyed by column name — so it models the real shape it claimed to model.

## Files changed

- `csharp/Services/QueryJobManager.cs` — `ComputeSettledAggregation` resolves `group_by` to the projected row key and reports unresolvable fields as warnings.
- `csharp/Services/DirectoryPlanExecutor.cs` — dead duplicate `ComputeAggregation`, its call site, and `RuntimeResult.Aggregation` removed.
- `tests/AdQueryOrchestrator.Tests/Unit/GroupedResultSettlementTests.cs` — fixture rows keyed by projection column name; alias, case-variant, and unprojected-field cases added.

## Guard proof

`GroupedResultSettlementTests.AliasedProjectionColumn_GroupsByTheProjectedValue` fails against the pre-fix tree — the aliased plan returns a single `(empty)` bucket of 8 instead of five real buckets — and passes after. `UnprojectedGroupByField_WarnsRatherThanFabricatingAnEmptyBucket` fails when the warning is suppressed. Full `scripts/verify.ps1` green.

## Coder dispute (if any)

None. Both halves are real and the guard-fixture defect is mine: the Slice 1 test asserted over a row shape the production projection does not emit, which is precisely how the alias bug survived a slice that touched this code.

## Known gaps

The resolution is per-field and positional in the composite key; the delimiter-collision defect in that composite key is tracked separately as `slice1-or-2`. Nothing here validates at plan time that a `group_by` field is projected — a plan can still request grouping on an unprojected attribute and get a warned-but-degraded answer rather than a rejection.

## Reviewer comments

`Reviewer: codex / gpt-5.6-sol / xhigh / frontier (inline, session-only)` — openreview over Slice 1, ChatGPT-subscription home via `codex-commercial.ps1`.

- Harness: codex-cli 0.145.0 (`CODEX_HOME=C:\Users\mcoelho\.codex-commercial`, `-s danger-full-access`)
- Base SHA: `89708047a954d00bf21f860a8f13ecc63f7ca120`
- Head SHA: `f261e50ae62f12edbfa17e82cf311880eb08782f`
- Verdict: `findings` (2), envelope schema-valid, both SHAs matched the dispatch
- Timestamp: 2026-07-29T15:48Z
- Comment (verbatim `better_approach`): "Compute aggregation from row-step records before display projection, or explicitly map each group_by attribute to its matching ProjectionColumn.Name and validate or add missing grouping fields. Consolidate the duplicate aggregation implementations and guard the real projected-row shape end to end."
