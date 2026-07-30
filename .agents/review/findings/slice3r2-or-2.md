# slice3r2-or-2: the caveat asserts the total is higher when the code knows only that it may be

**Severity**: LOW — the caveat's direction is right and it fires in the right cases; the
overstatement is one clause, and it errs toward telling the user the answer is smaller than
reality rather than larger.
**Status**: Fixed
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `<this commit>`

## Evidence
`ci-or-1` deliberately triggers the incompleteness flag at `>=` rather than `>`
(`DirectoryPlanExecutor`, the system-cap arm), because `EnsurePlanLimit` pushes the ceiling onto
the row step's `size_limit`, so the directory can return exactly the ceiling with the executor
truncating nothing. The finding's own "Known gaps" section records the consequence: "a set whose
real size lands exactly on the ceiling is caveated although it is in fact whole."

The wording on both surfaces does not carry that hedge. `app.js:1305-1307`:

> "This stopped at a system limit before reading every match, so the real total is higher. See
> the warnings in the result panel."

The same string is appended by `renderAnswer` (`app.js:497`) and by `withIncompleteCaveat`
(`:1310`), so both the main answer block and the chat bubble assert *is higher*.
`answer_prompt_template.txt` is already correct — "the real total is larger and unknown" — so
the model-written sentence and the code-written sentence disagree about what is known.

## Predicted observable failure
Configure a ceiling of 5000 and query a set of exactly 5000 people. The flag trips at `>=`, the
answer reads "…at least 5,000… This stopped at a system limit before reading every match, so the
real total is higher." The real total is 5,000. The user is told a true count is an undercount
and reruns or escalates a query that already answered their question.

A rendering test asserting the caveat does not claim the total is higher catches it.

## What
The flag answers "is this figure a floor?", which is the question that matters and is answerable.
The caveat's wording answers a stronger question — "is the real total strictly greater?" — which
at the exact boundary is not answerable without asking the directory for one more row than it
will return. The template got this right and the JavaScript did not; that is a wording
inconsistency introduced in the same commit, not a design disagreement.

## Approach
Restate the caveat to claim only what the flag knows: the figure is a floor and the true total is
unknown and may be higher. One shared constant already feeds both surfaces, so one edit covers
both.

## Files changed
- `csharp/wwwroot/js/app.js` — `INCOMPLETE_CAVEAT` now reads "…so that figure is a floor — the
  real total is unknown and may be higher." One constant, both surfaces.
- `tests/.../Browser/AnswerRenderingTests.cs` — new
  `TheCaveat_ClaimsAFloorRatherThanAKnownUndercount` (7 → 8 tests); the existing fallback test's
  assertion follows the wording.

## Guard proof
The overstating sentence restored → 2 red of 8 in `AnswerRenderingTests`
(`TheCaveat_ClaimsAFloorRatherThanAKnownUndercount`,
`AnIncompleteResultWithNoAnswer_StillCaveatsTheFallbackSummary`). The new test's negative
assertion is what makes it non-vacuous in the other direction: it fails on the old wording
rather than merely passing on the new one.

`scripts/verify.ps1` green with everything restored: exit 0, 348 tests, 0 failures, 0 warnings,
publish smoke and vulnerability audit passed.

## Coder dispute (if any)
The reviewer's third suggestion — fetch one sentinel row beyond a system cap to distinguish
"exactly N" from "more than N" — is **declined for this finding**, not rejected on merit. It
changes what the executor asks the directory for and would make the `>=` trigger unnecessary;
that is a behavioral change to the query path, outside a wording correction, and it does not
help the node-limit and depth-limit arms at all (a traversal stopped at a node budget cannot be
resolved by one more row). Recorded here so it is not lost.

Severity downgraded from the reviewer's MEDIUM to LOW at intake: the caveat fires correctly in
every incomplete case, the overstatement is confined to one exact set size, and it pushes the
user toward re-querying rather than toward trusting a wrong number.

## Known gaps
None beyond the boundary case this fix stops misdescribing.

## Reviewer comments
Same round, dispatch, and envelope failure as [slice3r2-or-1](slice3r2-or-1.md) — see that record
for the harness and range provenance. Recorded verbatim:

> **Medium:** the caveat overstates what is known. The code deliberately treats an exact-ceiling
> result as unknowable, but the UI says the real total "is higher" (app.js:1304). At an exact
> ceiling—or after an incomplete traversal—the safe claim is: "at least N; the exact total is
> unknown and may be higher."
