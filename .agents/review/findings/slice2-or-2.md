# slice2-or-2: A job cancelled after the artifact is written leaks that artifact forever

**Severity**: MEDIUM — unbounded disk growth in the artifact directory, with no mechanism that
can ever reclaim it; the leaked files are the full result set, so they are also the largest
files the app writes.
**Status**: Fixed
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `<this commit>`

## Evidence
The artifact is written and recorded on the job *before* the job reaches a terminal status:
`csharp/Services/QueryJobManager.cs:403` assigns `job.ResultArtifactPath` (F04-D5: the
completion-time artifact of record must exist before `SetCompleted`), and Narrate runs after
that. Cancellation during Narrate lands in the `OperationCanceledException` catch at
`:427-432`, which sets `JobStatus.Cancelled` — with `ResultArtifactPath` already populated and
the file already on disk.

Neither reclamation path covers that state:

- Retention: `CleanupCompletedJobs` (`:658-689`) selects `_store.GetJobsByStatus(JobStatus.Completed)`
  only, so a `Cancelled` job is never considered for expiry.
- Orphan sweep: `ResultArtifactSweeper.LivePaths()` (`csharp/Services/ResultArtifactSweeper.cs:55-61`)
  enumerates **every** `JobStatus` and treats each job's `ResultArtifactPath` as live, so the
  cancelled job's artifact is protected from the sweep for as long as the job record exists.

The two mechanisms are complementary by design and the gap falls exactly between them: too
live for the sweeper, not `Completed` enough for retention.

## Predicted observable failure
Submit a query large enough to make Narrate take a moment, then cancel it once the LDAP phase
has finished. The job goes to `Cancelled`; the `.jsonl` artifact remains in the artifact
directory. Restart the app — the startup sweep leaves it, because the job (and its
`ResultArtifactPath`) is still in the store. Repeat: the directory grows without bound and
disk-admission 507s eventually start rejecting healthy queries. A test that drives a job to
`Cancelled` with a populated `ResultArtifactPath`, runs both `CleanupCompletedJobs` and the
sweeper, and asserts the file is gone catches it.

## What
F04-D5 replaced an auto-expiring `IMemoryCache` with files that never expire on their own, so
every terminal state now needs an explicit owner for its artifact. The design assigned
ownership for `Completed` (retention) and for files with no job at all (the sweeper), and left
`Cancelled` — and, by the same reasoning, `Failed` if it can be reached after `:403` — with no
owner.

## Approach
Reclaim on the transition, not on a later sweep: when a job that already has a
`ResultArtifactPath` moves to a non-`Completed` terminal status, delete the artifact and clear
the path. That keeps the sweeper's "every status is live" rule true rather than complicating
it, and keeps retention's scope unchanged.

## Files changed
- `csharp/Services/QueryJobManager.cs` — `ReleaseArtifactOfUncompletedJob` deletes the
  artifact and clears the path, called from both terminal catch handlers (`Cancelled` and
  `Failed`). It takes `_artifactLifecycleLock`, the same lock retention and the reuse claim
  take, and asks the same ownership question through a one-path overload of
  `IsArtifactStillOwned` — a reused path belongs to the ancestor that wrote it. Clearing the
  path unconditionally is what hands a shared file back to the sweeper. A throwing release is
  logged and swallowed: the caller is already handling the real outcome, and with the path
  cleared the file is orphaned and collectable by the startup sweep.
- `tests/AdQueryOrchestrator.Tests/Unit/ResultArtifactLifecycleTests.cs` — two guards, plus a
  `CancelOnNarrate` switch on the existing `StubClaude` that throws
  `OperationCanceledException` from `GenerateAnswerAsync`. That is the actual window: the
  artifact is on disk and the job is not yet `Completed`.

## Guard proof
Two tests, each proven against its own revert so neither rides on the other:

- `AJobCancelledAfterItsArtifactIsWritten_LeavesNothingOnDisk` — cancels during Narrate, then
  asserts the job is `Cancelled`, its path is null, and no `.results.jsonl` remains under the
  artifact root. Removing the `ReleaseArtifactOfUncompletedJob` call from the
  `OperationCanceledException` handler: **fails** (`Assert.Null() Failure: Value is not
  null`). Restored: green.
- `ACancelledTurnReusingAnAncestorsArtifact_LeavesThatArtifactAlone` — a reusing turn is
  cancelled; the ancestor's file and path must survive. Making the release delete
  unconditionally (dropping the `IsArtifactStillOwned` check): **fails**. Restored: green.

10/10 in the class. `scripts/verify.ps1` green, 327 tests.

## Coder dispute (if any)
None. Confirmed by reading all four cited sites.

## Known gaps
The fix covers any non-`Completed` terminal transition rather than special-casing
`Cancelled`, so whether `Failed` is reachable after `:403` no longer matters for correctness.
The guards exercise the cancellation path only; the `Failed` handler shares the one method
and is not separately covered.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview Slice 2 r1, inline
session-only, `codex-commercial.ps1`). Harness `codex-cli 0.146.0`. Dispatched over base
`0ef62aaaee7d677d4b6138cd4735876ccc5036ba`, head `2cb251169c6cecd044b1b6ba3bc64f3408fb70f7`.
**Envelope contract FAILED** — prose only, no `--output-last-message` file written despite
`--output-schema`; see `slice2-or-1` for the full provenance note. `guard_confirmed: false`.
Recorded verbatim:

> **Medium: cancelled-after-write jobs leak artifacts.** ResultArtifactPath set before
> narration (QueryJobManager.cs:403); cancellation marks Cancelled (QueryJobManager.cs:429);
> retention only sweeps Completed (QueryJobManager.cs:661) and the orphan sweeper treats all
> statuses as live (ResultArtifactSweeper.cs:56), so the file is never reclaimed.

Note: the reviewer reached this by reading the Slice 7 code, which is outside the Slice 2
range it was given. The finding is real regardless of which range it belongs to; it is filed
here because that is the round that produced it, and the fix commit will reference this record.
