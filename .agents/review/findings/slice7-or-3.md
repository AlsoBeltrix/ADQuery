# slice7-or-3: An interrupted artifact write leaves its temp file until the next restart

**Severity**: MEDIUM — a failed write leaks a full-size copy of the result on disk with no
in-process reclamation, and the only sweep is at startup, so a long-running service
accumulates them against the same volume the admission check is protecting.
**Status**: Open
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `<filled in after commit>`

## Evidence
`csharp/Services/JsonLinesResultArtifactStore.cs:78-103`. `WriteAsync` streams the header
and rows into `tempPath` (`.results.tmp`) and then `File.Move(tempPath, artifactPath,
overwrite: true)`. There is no `try`/`finally` around the write: any exception between
opening the stream and the move — an IO error, a full volume, a serialization fault — or a
cancellation at `:92` (`cancellationToken.ThrowIfCancellationRequested()`, reached once per
row) leaves the partial temp file behind and propagates.

`SweepOrphans` (`:204-245`) does delete temp files, but its only caller is
`ResultArtifactSweeper`, a hosted service that runs at startup. Nothing removes a temp file
while the process is up.

Trigger: cancelling a large query during its artifact write (the row loop is where a 40k-row
write spends its time), or any write failure. Cancellation is the reachable everyday case —
the UI's cancel button.

## Predicted observable failure
A user cancels a large query mid-write. A `.results.tmp` file the size of the result stays
in the artifact root indefinitely. Repeat across a deployment's uptime and the volume fills;
the admission check then starts refusing new queries with 507 over space nothing will
reclaim until a restart. A test that cancels a write and asserts the directory is clean
catches it.

## What
The atomic-write pattern is complete on the success path and unfinished on the failure path:
the temp file is created by the writer but disowned by it, and reclamation was delegated to a
sweep that only runs when no process is alive to have leaked one.

## Approach
The writer cleans up its own temp file. `WriteAsync` wraps the stream-and-move in
`try`/`catch`, deletes `tempPath` on any exception, and rethrows — the caller's contract is
unchanged, and `slice7-or-1` makes the rethrow fail the job. Deletion is best-effort and
swallows its own IO failure, because a cleanup error must not replace the real exception the
caller needs to see. The startup sweep stays: it still covers the case no in-process handler
can — a killed or crashed process — and its doc comment already says so.

## Files changed
- `csharp/Services/JsonLinesResultArtifactStore.cs:63-110` — failure cleanup around the
  temp-file write.

## Guard proof
- `tests/AdQueryOrchestrator.Tests/Unit/ResultArtifactStoreTests.cs` — a write cancelled
  mid-rows leaves no `.results.tmp` in the artifact root. Reverting the cleanup makes it
  FAIL.

## Coder dispute (if any)
None. Verified against the cited lines.

## Known gaps
None.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview Slice 7 r1, inline
session-only, `codex-commercial.ps1`). Harness `codex-cli 0.145.0`. Reviewed
`fc208cf2f51cedeff33c8462fcdaacf18972220d`, base
`6f32299d0986833d854608334914e61bd12c8af2` — both matched the dispatch.
`guard_confirmed: false` — the reviewer could not rerun the canonical verification (its
shell sandbox failed to initialize); it changed no files. Envelope recovered by one
re-emission-only re-prompt after a prose round — see `slice7-or-1` for the contract
record. Recorded verbatim:

> JsonLinesResultArtifactStore.cs:62 has no failure cleanup; orphan sweeping only occurs at
> startup. An interrupted artifact write can leave its temporary file indefinitely.
> Severity MEDIUM. Temporary files must be cleaned immediately.

Admitted at intake; remedy as the reviewer stated it.
