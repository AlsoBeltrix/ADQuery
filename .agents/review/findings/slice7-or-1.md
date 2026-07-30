# slice7-or-1: A failed artifact write still completes the job

**Severity**: HIGH — Slice 7 made the artifact the *only* place a completed result lives, so a
swallowed write leaves a job reporting Completed with a result that exists nowhere: preview
404s, the single-record headline degrades to a count, and the download is refused.
**Status**: Verified
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `469270b`

## Evidence
`csharp/Services/QueryJobManager.cs:488-492`. `WriteResultArtifactAsync` rethrows
`OperationCanceledException` but catches every other exception, logs it, and returns
`null`. `:367-368` assigns that `null` to `job.ResultArtifactPath`, and `:375-381`
then calls `_store.SetCompleted(...)` with it — the job is Completed carrying no path.

Its own doc comment (`:470-473`) states the justification: "A failed artifact write
degrades the job to the behavior it had before this slice — cache-backed, expiring."
**That fallback does not exist.** The same commit (`fc208cf`) removed the 2h
`IMemoryCache` entry and migrated all four readers to the artifact store; the guard
`CompletedResultReadersUseTheArtifactTests.EveryCompletedResultReader_ReachesTheArtifactStoreAndNeverTheMemoryCache`
asserts exactly that no reader can fall back. So the finding is stronger than the
reviewer stated: there is no degraded mode, only an empty one.

Trigger: any non-cancellation failure of `JsonLinesResultArtifactStore.WriteAsync` after
a successful traversal — a transient IO error, a permissions change on the artifact root,
the volume filling between the admission check and the write, a serialization fault on a
directory value.

## Predicted observable failure
A query traverses the directory successfully, the answer is narrated, and the job reports
`Completed` with its row count. Clicking preview returns 404 "results expired"; download
returns 400; a one-row result renders as a count instead of the record. Nothing anywhere
says the result was lost. A test that completes a job through a `WriteAsync` that throws,
then asserts the job's terminal status, catches it.

## What
The write-failure path was written for a world where a second copy of the result survived
in RAM. This slice deleted that copy but kept the swallow, so the failure now converts an
honest error into a silent wrong state: Completed means "the result is readable", and here
it is not.

## Approach
Persistence becomes part of completion rather than an optimization after it. A non-
cancellation write failure now fails the job — `_store.UpdateStatus(jobId, JobStatus.Failed, …)`
with a message naming the artifact write — before the aggregation, the Narrate call, and
`SetCompleted`. Narrating an answer for a result nobody can open would spend a model call
to describe something already lost. The exception rethrows from `WriteResultArtifactAsync`
and is handled by `ExecuteJobWithServicesAsync`'s existing outer `catch`, which is already
the one place that fails a job and writes the failure log line, so no second failure path
is introduced. Cancellation keeps propagating as before. The stale doc comment promising a
cache-backed fallback goes with it.

The reused-artifact branch (`:367`) is unaffected: reuse points at a file that already
exists, and its own lifecycle risk is `slice7-or-2`.

## Files changed
- `csharp/Services/QueryJobManager.cs:469-498` — `WriteResultArtifactAsync` returns
  `Task<string>` and rethrows instead of returning null; doc comment corrected.
- `csharp/Services/QueryJobManager.cs:358-372` — the call site records why.

## Guard proof
- `ResultArtifactLifecycleTests.AJobWhoseArtifactWriteFails_Fails_AndIsNeverNarrated` —
  a turn driven through a `FailingWriteArtifactStore` ends `Failed` with a null
  `ResultArtifactPath` and zero Narrate calls. Restoring the swallow (`return null!`)
  makes it **FAIL** (1 red); restored → 6/6 pass.
- Canonical verification: `pwsh -NoLogo -NoProfile -File scripts/verify.ps1` — passed,
  319 tests, 0 warnings, published smoke passed, audit clean.

## Coder dispute (if any)
None. Verified against the cited lines; the cited justification is falsified by the same
commit.

## Known gaps
The admission check (`HasRoomForAnotherResult`, 507 at the front door) already refuses the
common cause — a full volume — before the user waits for a traversal. This finding covers
what remains after that check passes.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview Slice 7 r1, inline
session-only, `codex-commercial.ps1`). Harness `codex-cli 0.145.0`. Reviewed
`fc208cf2f51cedeff33c8462fcdaacf18972220d`, base
`6f32299d0986833d854608334914e61bd12c8af2` — both matched the dispatch.
`guard_confirmed: false` — the reviewer reported its shell sandbox failed to initialize
and it could not rerun the canonical verification; it changed no files.

**Envelope: recovered.** The round first returned prose with no JSON verdict and never
wrote its `--output-last-message` file (exit 0). Per the verdict contract that is
fail-closed, not a pass; one re-emission-only re-prompt (no re-review, no repository
access, no judgement change) returned a schema-valid envelope: `verdict: "findings"`,
both SHAs matching, 4 findings. Recorded verbatim:

> Artifact-write failures are swallowed at QueryJobManager.cs:487, then the job is marked
> completed at line 374. Preview, export, and single-record rendering can subsequently
> lose their result. Severity HIGH. Persistence must succeed before completion.

Line numbers are one off throughout the envelope (`:487` for the catch at `:488-492`);
each cited construct exists adjacent to its cited line. Admitted at intake.
