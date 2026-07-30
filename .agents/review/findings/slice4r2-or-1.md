# slice4r2-or-1: whole-plan reuse reads every row of every candidate before comparing plans

**Severity**: MEDIUM — a long thread over large results re-reads and deserializes several
40k-row artifacts to discover none of them match, on the turn the user is waiting on. It is a
performance defect, not a correctness one: the answer is right, it just costs the full read of
every ancestor to reject them.
**Status**: Fixed
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `<this commit>`

## Evidence
`QueryJobManager.TryReuseThreadArtifact` walks the thread and, for each eligible ancestor,
reads the artifact before it knows whether the plan matches:

- `QueryJobManager.cs:488` — `var artifact = _resultArtifacts.Read(ancestor.ResultArtifactPath);`
  with no `maxRows`.
- `:490` — only then `string.Equals(artifact.PlanJson, executedPlanJson, ...)`.
- On a mismatch the loop continues to the next ancestor and reads that one whole too.

`JsonLinesResultArtifactStore.Read` (`:154`) loops `while (maxRows is null || rows.Count <
maxRows.Value)`, so a null bound deserializes every line — one `JsonSerializer.Deserialize<ArtifactRow>`
per row. The rows are discarded on a mismatch.

The format was designed to make this unnecessary. The store's own class comment says the header
line carries the plan "so … whole-plan reuse can compare plans without reading rows at all"
(`JsonLinesResultArtifactStore.cs:23-24`). `PlanJson` is on the header (`:93`, `:290-296`),
one `ReadLine` into the file. Nothing consumed that affordance.

`FollowUpOptions.MaxPriorQuestions` is 19, so a thread can hold up to twenty turns; the walk
visits every ancestor until it finds a match or exhausts the chain.

## Predicted observable failure
Run "everyone under Sanjay" over a 40k-row org, then ask three different follow-ups so the
thread holds four turns with four distinct plans. Ask a fifth question with yet another plan.
The reuse walk reads and deserializes four 40k-row artifacts — roughly 160k row
deserializations — and reuses none of them, before the turn proceeds to traverse the directory
anyway. The user waits for all of it.

A test that counts rows deserialized during a reuse walk over non-matching ancestors, and
asserts the count is zero, catches it and fails if the read is put back before the comparison.

## What
Slice 7 chose JSON Lines with a header specifically so a reader that wants part of a result
does not pay for the whole one, and it wired that affordance into three of the four readers via
`maxRows`. The reuse path is the fourth reader and it needs *less* than a bounded row read: it
needs the header alone, and only for the ancestors it rejects. The `IResultArtifactStore`
surface offered no way to express "header only" — `Read(path, maxRows: 0)` would work by
accident of the loop condition but reads as a mistake — so the caller took the only method
available.

## Approach
Add an explicit header-only read to the store contract and use it as the reuse walk's filter.
The full read then happens exactly once, for the ancestor whose plan already matched, which is
the read reuse actually needs. Nothing about the claim, the lock ordering, or the byte-identical
plan comparison changes.

Guard by counting deserialized rows across the walk: a thread of non-matching ancestors must
deserialize none.

## Files changed
- `csharp/Services/IResultArtifactStore.cs` — new `ReadHeader(path)`, documented as the
  reuse walk's rejection read.
- `csharp/Services/JsonLinesResultArtifactStore.cs` — `ReadHeader` is `Read(path, maxRows: 0)`,
  named so the call site does not read as a mistake.
- `csharp/Services/QueryJobManager.cs` — `TryReuseThreadArtifact` rejects on the header and reads
  rows only after a match. A full read that comes back null between the header read and the
  claim now releases the claim (`ReleaseClaimedAncestorArtifact`, under the same lock) before
  traversing, so no job completes naming a path it gave up on.
- `tests/.../Unit/ResultArtifactLifecycleTests.cs` — new `RowCountingArtifactStore`, plus
  `AReuseWalkOverNonMatchingAncestors_DeserializesNoRows` and its over-removal sentinel
  `AReuseWalkThatMatches_StillReadsTheRows`.
- `tests/.../Unit/NoResultArtifactStore.cs`, `Unit/ArtifactStorageAdmissionAndSweepTests.cs` —
  the three other stubs implement the new member.

## Guard proof
`ReadHeader` reverted to the unbounded `Read` at the rejection site → 2 red of 12 in
`ResultArtifactLifecycleTests` (`AReuseWalkOverNonMatchingAncestors_DeserializesNoRows`, and
`AReuseWalkThatMatches_StillReadsTheRows` — the matching walk then reads its rows twice, 400
instead of 200, which is the same defect seen from the other side).

`scripts/verify.ps1` green with everything restored: exit 0, 350 tests, 0 failures, 0 warnings,
publish smoke and vulnerability audit passed.

## Coder dispute (if any)
None. The reviewer's own recommended correction is what is implemented: header-only plan
comparison first, full read only for the matching candidate, plus a regression test that
mismatched ancestors are never read unbounded.

The reviewer also cited `.agents/state.md:19` for "327 passing tests". That count was accurate
when written and has since moved (348 as of this round); the state file's volatile-count
convention is `drift`'s to maintain and is not a finding.

## Known gaps
The matching ancestor is still read whole, which is the point of reuse — the rows are the
result. A future reader wanting only a preview of a reused set would want `maxRows` here too,
but no caller does today.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview Slice 4 round 2, inline
session-only, `codex-commercial.ps1`). Log header verified at the frontier pair:
`model: gpt-5.6-sol | reasoning effort: xhigh`. Dispatched over base `c205656c`, head
`175b9caa`.

**Envelope contract FAILED** — exit 0, prose only, no `--output-last-message` file written
despite `--output-schema`. Fourth consecutive round to fail this way. Finding extracted under
the recovery rule and verified against code before admission. `guard_confirmed: false` — the log
carries 30 `windows sandbox: helper_unknown_error`; the reviewer reported it "could not rerun
current verification … because command sandbox failed during setup". It changed no files.

**Range note:** as in Slice 3 round 2, the reviewer answered the whole-change question against
the current tree rather than confining itself to the dispatched Slice 4 range. Admissible for the
same reason: the round's question is the whole-change one.

Recorded verbatim:

> No—not quite. The architecture is right, but one medium performance flaw prevents calling the
> implementation the best version.
>
> - Translate → deterministic execute/reduce → Narrate pipeline is appropriate. It preserves AD
>   safety, bounds model-visible data, and isolates narration failure.
> - Server-built conversation context and exact-plan artifact reuse are safer than
>   client-supplied history and incremental plan mutation.
> - However, QueryJobManager.cs:487 fully reads each eligible ancestor artifact before comparing
>   plans. JsonLinesResultArtifactStore.cs:153 consequently deserializes every row when no limit
>   is supplied—even though the header is designed for row-free plan comparison. A long
>   conversation containing 40k-row results can therefore reread several large artifacts merely
>   to discover none match. The smallest correction is header-only or in-memory plan comparison,
>   followed by a full artifact read only for the matching candidate, and a regression test
>   asserting mismatched ancestors are never read unbounded.
>
> Verdict: keep the overall design; fix the reuse path rather than redesigning the feature.
