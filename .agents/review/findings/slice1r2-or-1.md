# slice1r2-or-1: Grouping reads the display projection, so an unprojected `group_by` attribute fabricates one `(empty)` bucket

**Severity**: HIGH — a valid plan that groups on an attribute it does not display reports every record in a single `(empty)` bucket, and that fabricated distribution becomes the headline, the chat answer, and every export.

**Status**: Verified
**Branch**: — (fix on `master`)
**Commit**: `db6fb5b` (reviewed head), fix commit below

## Evidence

- `csharp/Services/QueryJobManager.cs:474` (at `db6fb5b`) — `ResolveGroupKey` looked each `group_by` field up in the *projected row* dictionary, which is keyed by `ProjectionColumn.Name`, not by directory attribute.
- Same file, line 495 — an unresolved field became the literal `"(empty)"`, so every row collapsed to one key.
- `tests/AdQueryOrchestrator.Tests/Unit/GroupedResultSettlementTests.cs:146` (at `db6fb5b`) — `UnprojectedGroupByField_WarnsRatherThanFabricatingAnEmptyBucket` asserted exactly that single empty bucket, so the defect was pinned as intended behavior rather than caught.
- `csharp/Services/DirectoryPlanExecutor.cs:531` — `Project` had the row-step `DirectoryRecord` in hand at the moment it built each row, and discarded everything the projection did not name.
- `csharp/Security/PlanValidator.cs` — nothing requires a `group_by` field to appear in `projection.columns`; the plan shape treats them as independent, and the prompt never tells the model to project what it groups on.

## Predicted observable failure

A plan for "how many users of each employee type" that projects `Name` and `Title` and groups by `employeeType` completes successfully. `grouped_counts` is `{"(empty)": 8400}`. The headline reads as one category covering the whole population, the CSV/xlsx/HTML distribution export has one row, and the follow-up value slice tells the model the same thing. A warning is emitted, but the result is presented as an answer, so the user has no signal that the number is synthetic.

The near-identical alias case fails the same way: `group_by: ["extensionAttribute1"]` with a projection column named `Cost Center` sourced from that attribute resolves nothing.

## What

Two independent facts were conflated: what the user should *see* (the projection) and what the result should be *grouped by* (the aggregation). Grouping was derived from the display projection, so a legitimate plan shape — group on one thing, display another — could not produce a correct answer, and the failure was silent because `"(empty)"` is a valid-looking bucket.

## Approach

Source group values where the data actually is. `DirectoryPlanExecutor.Project` already iterates the filtered row-step records to build display rows; in the same pass it now reads each `group_by` attribute off the record and emits a per-row value tuple, carried on `PlanExecutionResult.GroupValues` positionally against `Data`. `ComputeSettledAggregation` consumes that tuple and `ResolveGroupKey` is deleted — the settlement layer no longer inspects rows at all for grouping.

`ResultLimit` truncation truncates rows and group values in lockstep so the positional contract holds.

The reviewer's fallback — fail validation when a field cannot be resolved — was not taken. Those plans are semantically valid and common; rejecting them would turn a wrong answer into a refused answer. Instead, a caller that supplies no group values (or a count that does not match the rows) gets no `grouped_counts` at all plus a warning, so a future regression in the wiring cannot resurrect a fabricated distribution.

Blank and absent attributes normalize to `null` in the executor (`FormatGroupValue`), and `null` is the only signal the settlement layer reads for the `(empty)` bucket — so a genuinely unset attribute is still one real bucket, distinct from a resolution failure.

## Files changed

- `csharp/Services/IDirectoryPlanExecutor.cs` — `PlanExecutionResult.GroupValues`.
- `csharp/Services/DirectoryPlanExecutor.cs` — `Project` returns `(Rows, GroupValues)` sourced from the record; `FormatGroupValue`; `RuntimeResult.GroupValues`; lockstep `ResultLimit` truncation.
- `csharp/Services/QueryJobManager.cs` — `ComputeSettledAggregation`/`ComputeAggregation` take group values; `ResolveGroupKey` deleted; missing-values warning.
- `tests/AdQueryOrchestrator.Tests/Unit/ExecutorGroupValueSourcingTests.cs` (new) — executor-level guards.
- `tests/AdQueryOrchestrator.Tests/Unit/GroupedResultSettlementTests.cs` — the two tests that pinned the defect replaced by ones asserting the record-sourced contract; remaining call sites migrated.
- `tests/AdQueryOrchestrator.Tests/Unit/CaseFoldedGroupingTests.cs` — call sites migrated.

## Guard proof

Reverting `Project` to read group values from the projected row (`row.TryGetValue(field, ...)` in place of `record[field]`) turns three of the four new executor guards red:

```
Failed ExecutorGroupValueSourcingTests.GroupByAnUnprojectedAttribute_StillYieldsTheRealDistribution
Failed ExecutorGroupValueSourcingTests.AliasedProjectionColumn_DoesNotChangeTheGrouping
Failed ExecutorGroupValueSourcingTests.GroupValuesStayPositionalWithRows_IncludingUnsetAttributes
Failed! - Failed: 3, Passed: 1, Skipped: 0, Total: 4
```

The first is the finding's exact scenario: grouping by `employeeType` while projecting only `Name` yields the fabricated single bucket instead of `CWK: 2, FTE: 1`. All four pass with the record-sourced read restored. Full `scripts/verify.ps1` green: 176 tests, 0 warnings, published smoke passed, audit clean.

## Coder dispute (if any)

Partial, on the fallback only. The reviewer's primary remedy — compute aggregation from the filtered row-step records rather than the display projection — is what shipped. The secondary clause ("if a field cannot be resolved unambiguously, fail validation instead of completing with synthetic data") is declined for `group_by` fields absent from the projection, because that is a valid plan shape rather than an ambiguity; the underlying concern is met by refusing to emit `grouped_counts` when group values are missing.

## Known gaps

`GroupValues` is positional against `Data` with no structural enforcement beyond the count check in `ComputeSettledAggregation`. A future executor path that filters rows after `Project` without truncating group values in lockstep would misalign silently up to the point the counts diverge. The structured `grouped_counts` carrier deferred in [slice1-or-2](slice1-or-2.md) would remove the positional coupling entirely.

## Reviewer comments

`Reviewer: codex / gpt-5.6-sol / xhigh / frontier (openreview, inline session-only, codex-commercial.ps1)` — round 2 over Slice 1.

- Harness: codex-cli 0.145.0 (`CODEX_HOME=C:\Users\mcoelho\.codex-commercial`, `-s danger-full-access`)
- Base SHA: `89708047a954d00bf21f860a8f13ecc63f7ca120`
- Head SHA: `db6fb5bdd83b8fc4e28267a7d3ead7869d52c744`
- Verdict: `findings` (2), envelope schema-valid, both SHAs matched the dispatch
- Timestamp: 2026-07-29T20:18Z
- Comment (verbatim `better_approach`): "Compute aggregation from the filtered row-step directory records before display projection, or carry group-by attributes in an internal sidecar independent of visible columns. If a field cannot be resolved unambiguously, fail validation instead of completing with synthetic data."
