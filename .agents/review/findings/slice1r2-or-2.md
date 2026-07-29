# slice1r2-or-2: Escaped composite group keys leak into the headline and the follow-up context

**Severity**: MEDIUM — the headline shows the user an escaped transport string in place of the directory values, and the follow-up context sends those altered values to the model.

**Status**: Verified
**Branch**: — (fix on `master`)
**Commit**: `db6fb5b` (reviewed head), fix commit below

## Evidence

- `csharp/Services/GroupKey.cs:42` — composite components are escaped (`|` → `\|`, `\` → `\\`) and joined into a transport string.
- `csharp/Services/HeadlineClassifier.cs:100` (at `db6fb5b`) — `ExtractGroups` set `HeadlineGroup.Key` to that encoded key unchanged.
- `csharp/Services/FollowUpContextBuilder.cs:94` (at `db6fb5b`) — `BuildValueSlice` interpolated the same encoded key into the model-facing "Previous results" line.
- `csharp/wwwroot/js/app.js:616` — the browser renders the headline key raw, so nothing downstream corrects it.
- By contrast, the aggregation table (`app.js:683`) and the distribution export (`QueryController.BuildGroupedDistributionExport`) both decode correctly — the two paths disagreed about whether the key was text.

## Predicted observable failure

Grouping by `["department","city"]` with a department of `R&D|Labs` in `Boston`: the leading headline reads `R&D\|Labs|Boston` while the table immediately below it shows the same bucket correctly split across two columns. Values containing a backslash display it doubled. The follow-up context sends the model `Previous results: R&D\|Labs|Boston: 9`, so a genuine conversational answer can quote a department name that does not exist in the directory — the exact failure mode F04 is meant to eliminate.

## What

[slice1-or-2](slice1-or-2.md) made the composite key reversible but only wired the decode into the two consumers that split it into columns. The key is transport, not display text, and two consumers were still treating it as text. The invariant was implicit, so the split was easy to miss.

## Approach

Make the invariant explicit and give it one owner. `GroupKey.ToDisplay(key, fieldCount)` renders a key for a human or the model — escapes removed, components joined with ` / ` — and both leaking consumers now go through it. A single-field key is returned verbatim, matching `Compose`, so values containing `|` or `\` in the common single-field case are untouched.

The field count comes from the plan (`plan.Projection.Aggregation.GroupBy.Count`), which both call sites already have, defaulting to 1 when absent — the same source `Decompose` needs everywhere else.

The reviewer's primary remedy (structured field-value tuples throughout) remains the better end state and remains deferred to the Slice 7 artifact work for the reason recorded in [slice1-or-2](slice1-or-2.md); the reviewer's own explicit fallback is what shipped.

## Files changed

- `csharp/Services/GroupKey.cs` — `ToDisplay`.
- `csharp/Services/HeadlineClassifier.cs` — `ExtractGroups` takes the group-by field count and decodes the key.
- `csharp/Services/FollowUpContextBuilder.cs` — `BuildValueSlice` takes the field count and decodes each key.
- `tests/AdQueryOrchestrator.Tests/Unit/HeadlineClassifierTests.cs` — composite decode and single-field passthrough guards.
- `tests/AdQueryOrchestrator.Tests/Unit/FollowUpContextBuilderTests.cs` — model-facing decode guard.

## Guard proof

Reverting `GroupKey.ToDisplay` to a passthrough (`=> key`) turns both new guards red:

```
Failed HeadlineClassifierTests.MultiFieldGroupKey_IsDecodedForDisplay_NotShownAsRawTransport
Failed FollowUpContextBuilderTests.BuildFromPreviousTurn_DecodesCompositeGroupKeys
Failed! - Failed: 2, Passed: 16, Skipped: 0, Total: 18
```

Both pass with the decode restored. `SingleFieldGroupKey_IsShownVerbatim` stays green under the revert by construction — it guards the opposite direction, that decoding must not mangle an unescaped single-field value. Full `scripts/verify.ps1` green: 176 tests, 0 warnings, published smoke passed, audit clean.

## Coder dispute (if any)

None on the defect or the remedy.

## Known gaps

The ` / ` separator is a display choice with no escaping of its own: a directory value containing that exact sequence is indistinguishable from a field boundary *to a reader*. This is cosmetic — nothing parses `ToDisplay` output back into fields — but it means the rendered string is not itself reversible, and any future consumer must decode from the stored key rather than from displayed text.

## Reviewer comments

`Reviewer: codex / gpt-5.6-sol / xhigh / frontier (openreview, inline session-only, codex-commercial.ps1)` — round 2 over Slice 1.

- Harness: codex-cli 0.145.0 (`CODEX_HOME=C:\Users\mcoelho\.codex-commercial`, `-s danger-full-access`)
- Base SHA: `89708047a954d00bf21f860a8f13ecc63f7ca120`
- Head SHA: `db6fb5bdd83b8fc4e28267a7d3ead7869d52c744`
- Verdict: `findings` (2), envelope schema-valid, both SHAs matched the dispatch
- Timestamp: 2026-07-29T20:18Z
- Comment (verbatim `better_approach`): "Represent grouped values as structured field-value tuples throughout. If the string carrier must remain temporarily, decode it with the plan's group_by field count before constructing headline groups or follow-up context."
