# slice6-or-1: Alternate-model retries discard the accumulated thread ancestry

**Severity**: MEDIUM — a retried turn becomes a thread root, so the next follow-up is re-planned without the subject the conversation established; the failure is a silently wrong result set, not an error.
**Status**: Fixed
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `7e94872` (status recorded late — see [slice5r2-or-2](slice5r2-or-2.md))

## Evidence
`csharp/Controllers/QueryController.cs:1329-1338` builds the replacement job for
`retry-with-alternate-model` by copying `Query`, `Context`, and
`RequestedResultLimit` from the original, but not the `PreviousJobId` that F04
Slice 6a (`0c1df79`) added. `csharp/wwwroot/js/app.js:411` then makes the
replacement job `state.lastCompletedJobId`, and `:265-267` sends that id as the
next turn's `previousJobId`. `FollowUpContextBuilder.BuildThreadQuestions`
(`csharp/Services/FollowUpContextBuilder.cs:109-111`) stops walking the moment
`PreviousJobId` is blank, so the thread it renders holds only the retried
question.

Trigger: a thread of two or more turns, where the user retries the latest turn
with the alternate model and then asks another follow-up.

## Predicted observable failure
Thread "everyone under Sanjay" → "only with titles"; retry the second turn; then
ask "add the users in China". The context sent to Translate carries only "only
with titles" — "everyone under Sanjay" is gone — so the model can legitimately
plan a directory-wide query. Same class of defect the whole slice exists to
prevent, reachable through an ordinary UI affordance.

## What
The retry path clones a logical turn but drops the field that records the turn's
place in the thread, so retrying silently truncates the conversation.

## Approach
Copy `originalJob.PreviousJobId` onto the replacement job. The replacement *is*
the same logical turn, so it inherits the same ancestor; pointing it at
`originalJob` instead would duplicate the retried question in the rendered
thread. The retry path never reaches `CreateJobAsync` (it calls
`EnqueueJobAsync` with a hand-built job), which is why the Slice 6a wiring
missed it.

## Files changed
- `csharp/Controllers/QueryController.cs:1329-1338` — the clone carries `PreviousJobId`.

## Guard proof
- `tests/AdQueryOrchestrator.Tests/Unit/QueryControllerFollowUpProvenanceTests.cs` —
  a retry of a mid-thread turn produces a replacement job whose thread walk still
  reaches the thread's first question. Reverting the one-line clone fix makes it FAIL.

## Coder dispute (if any)
None. Verified against the cited lines.

## Known gaps
None.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview, inline
session-only, `codex-commercial.ps1`). Harness `codex-cli 0.145.0`. Reviewed
`aed870e193c02726eedeb037f8a6969c430c29b4`, base
`00f0efbbdf8d96be8762759757ca1b4141285e57` — both matched the dispatch; envelope
schema-valid. Verdict `findings` (2 of 2), 2026-07-30 UTC.

> When cloning a logical turn for retry, copy originalJob.PreviousJobId to the
> replacement job. Do not point it at originalJob itself, which would duplicate
> the same question. Add a regression test covering multi-turn conversation →
> retry → subsequent follow-up.

Admitted at intake: evidence cites real lines, the predicted failure is
observable, and the remedy is the one implemented.
