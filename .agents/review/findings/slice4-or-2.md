# slice4-or-2: The model-free invariant lock misses calls hidden behind DI dispatch

**Severity**: MEDIUM — the F04 Slice 4 invariant guard stopped at interface method bodies and
checked two hard-coded field names, so a routine export-service extraction could reintroduce
model execution while all three tests stayed green. Nothing asserted the exported bytes came
from the settled result at all.

**Status**: Verified
**Branch**: — (repo works on `master`; one commit per finding)
**Commit**: filled in at commit

## Evidence
- `tests/AdQueryOrchestrator.Tests/Unit/ExportIsModelFreeTests.cs:124-148` (at `c205656`) —
  `ReachableMethods` enqueues the resolved callee only. An interface method's body is empty, so
  the walk terminates there and never sees the implementation's calls.
- `:38-42` — `ForbiddenControllerFields` is the literal pair `_claudeService` / `_planExecutor`,
  scoped to `typeof(QueryController)`. A service extracted out of the controller holds its model
  reference in a field this list does not name.
- No test in the suite asserted the download bytes derive from the job's settled result.

## Predicted observable failure
Refactor `DownloadAsync` to call an injected `IExportService`, and have that implementation call
`IClaudeService`. Both call-graph assertions stay green — verified by construction below. The
binding invariant ("export must never risk producing a different result than the answer the user
already read") would then be unguarded while appearing guarded, which is worse than no guard.

## What
Slice 4's walk was built for the shape the code had — one controller method calling private
helpers. It encoded that shape twice: by not descending through virtual dispatch, and by naming
the two fields the controller happened to declare. Both assumptions break under the most likely
future refactor, which is the one the guard exists to survive.

## Approach
Two changes to the walk, plus the missing positive claim as a separate guard.

1. **Descend through virtual dispatch.** A call to an interface or abstract method now enqueues
   every application-assembly implementation of it (`ImplementationsOf`, via
   `Type.GetInterfaceMap` for interfaces and `GetBaseDefinition` matching for overrides). This
   over-approximates — it walks implementations the runtime might never select — which is the
   safe direction for an invariant the guard must not miss. Open generic type definitions are
   skipped: they have no runtime interface map, and any concrete instantiation is reached
   through its own type.
2. **Discover forbidden fields by type, not by name.** `ForbiddenFields` is every field in the
   application assembly whose type is assignable to `IClaudeService` or `IDirectoryPlanExecutor`,
   found by reflection. A renamed field, or a new one on an extracted service, is covered without
   editing the test.
3. **Guard the positive claim separately.** New `ExportSerializesTheSettledArtifactTests` drives
   the real `GenerateFileContent` — the whole content-producing tail of `DownloadAsync` — with
   distinctively seeded values and asserts the bytes carry the settled rows, the settled
   distribution (and *not* the underlying rows, per F04-D2), and the settled job's provenance
   metadata, across csv/html/text.

The reviewer's `better_approach` proposed instead a runtime endpoint test using a temporary
configurable output root, keeping the structural check as supplemental. **Declined, with the
substance adopted:** `QueryLogHelper.OutputRoot` is a `const` consumed by `QueryJobManager` and
`JsonLinesFeedbackStore` as well as the download path, so making it configurable is a change to
the storage model — which is Slice 7's scope (F04-D5, the completion-time artifact of record),
not a test fixture's. Deferring it avoids a throwaway seam that Slice 7 would immediately
rework. The finding's actual substance — that nothing proved the bytes came from the settled
artifact — is addressed by guard 3 above at the serializer rather than the endpoint. What that
leaves unguarded is the ~15 lines of `DownloadAsync` between the cache read and
`GenerateFileContent`; the call-graph assertions cover those for the model-free half.

## Files changed
- `tests/AdQueryOrchestrator.Tests/Unit/ExportIsModelFreeTests.cs` — `ImplementationsOf`,
  `MapInterfaceMethod`, type-discovered `ForbiddenFields`, updated class doc.
- `tests/AdQueryOrchestrator.Tests/Unit/ExportSerializesTheSettledArtifactTests.cs` — new,
  5 tests.

## Guard proof
**The descent fix**, proven by building the reviewer's exact scenario: a temporary
`IProofExportService` / `ProofExportService` pair whose implementation calls
`IClaudeService.GenerateExecutionPlanAsync`, injected into `QueryController` and called from
`DownloadAsync`.

```
Failed ExportIsModelFreeTests.DownloadAsync_CallGraph_NeverLoadsTheModelOrExecutorFields
Failed ExportIsModelFreeTests.DownloadAsync_CallGraph_NeverReachesTheModelOrThePlanExecutor
Failed! - Failed: 2, Passed: 1
```

Then, with that hidden model call still in place, disabling only the new descent:

```
Passed! - Failed: 0, Passed: 3
```

— which is the finding reproduced exactly: the old walk certifies a model-calling export as
model-free. Descent restored → 2 red again; proof service and controller wiring removed → green.

**The artifact guard**, two independent reverts:

```
distribution branch disabled  → Failed: 1 (GroupedExport_CarriesTheSettledDistribution…)
row values replaced by "PROOF" → Failed: 3 (ListExport, ExportedMetadata, GroupedExport)
```

Canonical verification: `pwsh -NoLogo -NoProfile -File scripts/verify.ps1` — passed, 271 tests,
0 warnings, published smoke passed, audit clean.

## Coder dispute (if any)
The defect is admitted in full. The remedy for its second half differs from the reviewer's
proposal as recorded under Approach: the configurable output root is Slice 7's scope, so the
provenance claim is guarded at the serializer instead of the endpoint.

## Known gaps
`ExportSerializesTheSettledArtifactTests` drives `GenerateFileContent` rather than the endpoint,
so the cache-read-to-serializer segment of `DownloadAsync` has no positive byte-level guard —
only the call-graph coverage. Revisit when Slice 7 makes the artifact path drivable.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` — openreview Slice 4 round 1, inline
session-only tier (`codex-commercial.ps1`, `sandbox_mode=danger-full-access`).
Base `563d331d35fb8c1eafda28142d81d8ce8455b163`, head
`c205656cf31a00f03988e97eb5870781b3a34f4f`.
Verdict `findings` (2), envelope schema-valid, both SHAs matched the dispatch.
Comment (verbatim `better_approach`): "Add a runtime endpoint test using a temporary
configurable output root, a seeded settled result, and model/executor stubs that throw on
invocation. Assert the returned file contains the seeded artifact data; retain a smaller
structural check only as supplemental coverage."
