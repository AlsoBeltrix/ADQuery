# slice5r2-or-2: two finding records still read "Open" after their fixes landed

**Severity**: LOW — a documentation defect, not a behavior one. Nothing a user can observe is
wrong; what is wrong is that the repo's own record of what is outstanding overstates it, which is
the thing a cold session reads first.
**Status**: Fixed
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `<this commit>`

## Evidence
`.agents/review/findings/slice6-or-1.md:4` and `.agents/review/findings/slice6-or-2.md:4` both
read `**Status**: Open`, and both carry `**Commit**: <filled in after commit>`.

Both fixes are landed:
- `slice6-or-1` — `7e94872` "fix(followup): keep a retried turn's place in the thread
  (slice6-or-1)". `QueryController.cs:1361` carries `PreviousJobId = originalJob.PreviousJobId`,
  the named remedy. The guard is in
  `tests/.../Unit/QueryControllerFollowUpProvenanceTests.cs` (+77 lines in that commit).
- `slice6-or-2` — `f0b6df3` "fix(prompt): scope the subject-reset exit to subject replacement
  (slice6-or-2)". `prompt_template.txt:36-37` and `ClaudeService.cs:459-460` both carry the
  corrected exit condition and the constraint-replacement counter-example; the bare
  `"instead of..."` trigger is gone from both. `SubjectScopingPromptTests.RequiredGuidance`
  (`:39-49`) checks the new phrases and asserts the retired trigger's absence in both paths.

`6f32299` recorded the round as closed in `.agents/state.md`, so the state file and the finding
records disagree about the same two items.

## Predicted observable failure
A session doing what `AGENTS.md` prescribes — reading the `.agents/` files before making changes —
reads two MEDIUM findings as outstanding. The specific waste is re-fixing them: both records
carry a fully written Approach section, so the plausible response to "Open" is to implement the
approach that is already implemented, then discover the code already matches. The rest of the
sweep's `Status: Fixed` records make the two "Open" ones read as genuinely outstanding rather than
as a stale convention.

## What
The Slice 6 round closed via the state file and the commits, and the two finding records were
never flipped. The `<filled in after commit>` placeholders show where the step was skipped: the
commit-time edit that sets both fields did not happen for these two.

## Approach
Flip both to `Fixed` and fill in the real commit SHAs, matching the convention every other closed
record in the directory uses. Documentation only — no code, no guard.

## Files changed
- `.agents/review/findings/slice6-or-1.md` — `Status: Fixed`, `Commit: 7e94872`.
- `.agents/review/findings/slice6-or-2.md` — `Status: Fixed`, `Commit: f0b6df3`.

## Guard proof
None, and none is possible in the usual sense: this is a docs-only change with no behavior to
guard. Per `AGENTS.md` Verification, code verification is not required for a docs-only change that
does not affect setup, commands, runtime behavior, or generated files.

The substantive verification is the evidence above — the fixes and their guards were confirmed
present in the tree before the status was flipped, so this does not mark as fixed anything that
is not.

## Coder dispute (if any)
None. The reviewer's claim was checked against both records and both commits and is exactly right.

## Known gaps
Nothing keeps a finding record's status honest automatically. Every other closed record in the
directory got there by hand too, and the two that were missed were missed by hand. A check that
each finding whose SHA is a placeholder is either genuinely open or reported would close this
class; it is not built here.

## Reviewer comments
Same round, dispatch, and envelope failure as [slice5r2-or-1](slice5r2-or-1.md) — see that record
for the harness and range provenance, and for the intake dispositions of the reviewer's other
items. Recorded verbatim:

> Two review records still say "Open," but their fixes and regression guards are present (retry
> ancestry QueryController.cs:1360, subject correction prompt_template.txt:36). The records need a
> drift reconciliation.

Admitted at intake: the evidence is checkable and checked, the predicted cost is a session acting
on a false outstanding item, and the remedy is the one implemented. Severity LOW rather than the
reviewer's implicit framing, because no behavior is affected — but admitted rather than deferred,
because `AGENTS.md` makes these files a session's first read and the repo is memory.
