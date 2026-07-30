# slice7-or-2: Whole-plan reuse reads an ancestor artifact it has not claimed

**Severity**: MEDIUM — retention runs on a background loop with no lock against a running
job, so an ancestor artifact can be deleted between the reusing turn's read and its claim,
leaving a Completed job pointing at a file that is gone.
**Status**: Open
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `<filled in after commit>`

## Evidence
`csharp/Services/QueryJobManager.cs:443` reads the ancestor's artifact inside
`TryReuseThreadArtifact`; the reusing job does not point at that path until `:367`.
`csharp/Services/QueryJobManager.cs:592-598` deletes an artifact whose owner has expired
and no other job references, guarded by `IsArtifactStillOwned` (`:609-612`) — which can
only see a claim already written to `job.ResultArtifactPath`.

The two run concurrently by construction:
`csharp/Services/QueryJobExecutorHostedService.cs:66-91` executes each job in a
fire-and-forget `Task.Run`, while `:95` calls `CleanupCompletedJobs` from the loop thread
every second. `InMemoryQueryJobStore` is a bare `ConcurrentDictionary` (`:14`) — per-entry
atomicity only, no lock spanning read-then-write across the two paths.

Trigger: a follow-up turn whose plan is byte-identical to an ancestor's, arriving while
that ancestor crosses the `Jobs:CompletedJobRetentionHours` boundary (default 24h) and no
other job references its artifact.

## Predicted observable failure
The reusing turn completes normally — it holds the rows in memory, so its headline and
narrated answer are correct — but `job.ResultArtifactPath` names a deleted file. Preview
returns 404 "results expired" and download returns 400 on a job that just reported
Completed. A test that deletes the ancestor artifact between the reuse read and the claim
catches it.

## What
Reuse is a read followed by an unsynchronized claim. Nothing makes the pair atomic against
the retention sweep that is entitled to delete exactly the file being read, so the claim
can land on an artifact that no longer exists.

## Approach
Retention delete + `RemoveJob` and the reuse claim become mutually exclusive under one
private lock owned by `QueryJobManager`. Ordering, not just exclusion, is what makes it
sound: retention deletes the file and removes the job's metadata as one critical section,
so a reuse path holding the lock and finding the ancestor still in the store knows the file
has not been deleted — the store entry is the artifact's liveness token. Reuse therefore
re-reads the ancestor under the lock and writes `job.ResultArtifactPath` there, which is
what `IsArtifactStillOwned` consults; if the ancestor is gone, reuse is abandoned and the
turn traverses normally.

The artifact `Read` itself stays outside the lock — it can be a 40k-row file, and blocking
the executor loop on it would trade a rare race for a routine stall. Only the liveness
re-check and the claim are inside.

## Files changed
- `csharp/Services/QueryJobManager.cs` — `_artifactLifecycleLock`; `TryReuseThreadArtifact`
  claims under it; `CleanupCompletedJobs` deletes and removes under it.

## Guard proof
- `tests/AdQueryOrchestrator.Tests/Unit/ResultArtifactLifecycleTests.cs` — a reusing turn
  whose ancestor expires concurrently either reuses a live artifact or traverses, never
  completes pointing at a deleted one. Reverting the claim makes it FAIL.

## Coder dispute (if any)
None on the mechanism. Scope note: the race window is narrow — there is no `await` between
the artifact read and the claim, so it takes a preemption in a few instructions of
synchronous code coinciding with the retention boundary. It is admitted because the
consequence is a silently unreadable Completed job and the remedy is small, not because it
is likely.

## Known gaps
`TryReuseThreadArtifact` reads the ancestor artifact **unbounded** (`Read(path)` with no
`maxRows`), so a 40k-row reuse materializes the whole set — the residency this slice exists
to avoid, on the reuse path only. Out of scope here; it is a shape question about what a
reused turn needs in memory, not a lifecycle race.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview Slice 7 r1, inline
session-only, `codex-commercial.ps1`). Harness `codex-cli 0.145.0`. Reviewed
`fc208cf2f51cedeff33c8462fcdaacf18972220d`, base
`6f32299d0986833d854608334914e61bd12c8af2` — both matched the dispatch.
`guard_confirmed: false` — the reviewer could not rerun the canonical verification (its
shell sandbox failed to initialize); it changed no files. Envelope recovered by one
re-emission-only re-prompt after a prose round — see `slice7-or-1` for the contract
record. Recorded verbatim:

> Reuse reads an ancestor artifact at QueryJobManager.cs:442, but does not claim it until
> line 366. Concurrent retention cleanup can delete it meanwhile at line 594. Severity
> MEDIUM. Reuse needs an atomic claim/lease.

Admitted at intake; the remedy is the reviewer's atomic claim, implemented as a lock
ordering the claim against the delete.
