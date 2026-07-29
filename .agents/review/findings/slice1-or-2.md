# slice1-or-2: Composite group keys join and split on an unescaped `|`

**Severity**: MEDIUM — a grouped value containing `|` merges distinct buckets into one count and, on export, shifts every field after it by one column.

**Status**: Verified
**Branch**: — (fix on `master`, one commit)
**Commit**: `f261e50` (export half introduced), fix commit below

## Evidence

- `csharp/Services/QueryJobManager.cs:452` — the composite group key is `string.Join("|", keys)` over raw field values, with no escaping.
- `csharp/Controllers/QueryController.cs:358` — Slice 1's new export reverses it with `key.Split('|')` and assigns the parts positionally to `group_by` fields.
- `csharp/wwwroot/js/app.js:683` — the aggregation table in the UI splits the same way (`key.split('|')`), so the corruption is not export-only.
- `csharp/Security/PlanValidator.cs:396-404` — `group_by` fields are attribute-allow-listed, but nothing constrains the *values* those attributes hold; AD free-text attributes (`department`, `title`, `extensionAttribute*`, `description`) can contain any character.

## Predicted observable failure

Two distinct failures from one cause, both silent:

1. **Bucket collision.** Records with `department = "R&D|Labs"`, `city = "Boston"` and records with `department = "R&D"`, `city = "Labs|Boston"` produce the identical key `R&D|Labs|Boston` and merge into a single count. The distribution reports one bucket where two exist; the total still sums correctly, so nothing looks wrong.
2. **Column shift on export.** Grouping `["department","city"]` where a department is `"R&D|Labs"` yields the key `R&D|Labs|Boston`; `Split('|')` returns three parts against two fields, so the export writes department `R&D`, city `Labs`, and drops `Boston` entirely. Under the pre-Slice-1 shape this only corrupted a comment block beneath the real rows; now the shifted table **is** the exported answer.

## What

The composite key is a lossy encoding used as if it were reversible. Aggregation flattens an ordered tuple of field values into one delimiter-joined string, then three separate readers (export, UI, and the count itself) reconstruct the tuple by splitting on that delimiter. The encoding has no escape mechanism, so any value containing the delimiter is indistinguishable from a field boundary.

The reviewer's suggested fix — carry structured field-value tuples end to end — is the correct shape but not correctly scoped here: `grouped_counts` is `Dictionary<string,int>` in the runtime aggregation, the job store, the API contract (`QueryController.cs:1365`), the follow-up value slice, the headline classifier, and the browser. Rewriting that carrier is a cross-cutting change that would ripple into Slice 6's context work and Slice 7's artifact, and it is not what a defect fix should drag in.

## Approach

Keep the string carrier; make the encoding reversible. Escape the delimiter (and the escape character) when composing a component, and unescape when splitting. One encode helper beside the grouping call, one decode helper beside each reader, so a key round-trips exactly for any value. Single-field grouping — the overwhelmingly common case — is unaffected because no join happens; the escape applies only to composite keys, where the display path already treats the key as structured.

Splitting is corrected to yield exactly as many components as there are `group_by` fields, so a malformed or legacy key degrades to missing trailing values rather than a silent column shift.

Recorded as a deliberate scope boundary: a structured `grouped_counts` carrier is the better long-term shape and belongs to the Slice 7 artifact work, where the serialization contract is already being reopened. It is not attempted here.

## Files changed

- `csharp/Services/GroupKey.cs` (new) — `Compose`/`Decompose` for the delimiter-escaped composite key, the single owner of the encoding.
- `csharp/Services/QueryJobManager.cs` — grouping composes through `GroupKey.Compose`.
- `csharp/Controllers/QueryController.cs` — the distribution export decomposes through `GroupKey.Decompose`.
- `csharp/wwwroot/js/app.js` — the aggregation table decodes with the matching escape rule.
- `tests/AdQueryOrchestrator.Tests/Unit/GroupedResultSettlementTests.cs` — round-trip and collision guards.

## Guard proof

Reverting `GroupKey.Compose`/`Decompose` to the pre-fix plain join and split turns both guards red: `CompositeKeyWithDelimiterInAValue_DoesNotCollideOrShiftColumns` fails at the bucket count (`Expected: 2, Actual: 1` — the two distinct department/city combinations merge), and `GroupKeyRoundTrips_ForValuesContainingDelimiterAndEscape` fails on three of its four cases (`"R&D|Labs"` decodes as `"R&D"`, `"pipe|and\escape"` as `"pipe"`, and a lone `"|"` value decodes empty). Both pass with the escape restored. Full `scripts/verify.ps1` green: 162 tests, 0 warnings, audit clean.

## Coder dispute (if any)

Partial, on remedy scope only, not on the defect. The reviewer prescribed replacing the delimiter encoding with structured tuples throughout; that is the right end state but spans the job store, the public API shape, the follow-up builder, and the browser, and Slice 7 already reopens that serialization boundary. Fixing the encoding to be reversible closes the predicted failures completely at a fraction of the blast radius. The structural change is recorded above as deferred, not declined.

## Known gaps

Grouped keys persisted by earlier runs (in-memory jobs from before this commit) used the unescaped encoding; they decode identically unless a value contained `|` or the escape character, in which case they were already corrupt. Nothing migrates them — job state is process-lifetime only.

## Reviewer comments

`Reviewer: codex / gpt-5.6-sol / xhigh / frontier (inline, session-only)` — openreview over Slice 1, ChatGPT-subscription home via `codex-commercial.ps1`.

- Harness: codex-cli 0.145.0 (`CODEX_HOME=C:\Users\mcoelho\.codex-commercial`, `-s danger-full-access`)
- Base SHA: `89708047a954d00bf21f860a8f13ecc63f7ca120`
- Head SHA: `f261e50ae62f12edbfa17e82cf311880eb08782f`
- Verdict: `findings` (2), envelope schema-valid, both SHAs matched the dispatch
- Timestamp: 2026-07-29T15:48Z
- Comment (verbatim `better_approach`): "Represent composite group keys as structured field-value tuples throughout aggregation and export instead of encoding them into a delimiter-separated string."
