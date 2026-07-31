# slice7r2-or-1: The Translate prompt's worked example teaches the union reading the plan ruled out

**Severity**: HIGH — the prompt teaches the wrong answer on the exact case F04 calls its
load-bearing one. A three-turn thread returns Sanjay's titled reports *plus* a second set of
his China reports, where the ruled reading is the people who are both. The user gets a
larger, wrong result with a description that says so, on the ordinary follow-up path.
**Status**: Fixed
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `<filled in after commit>`

## Evidence
`csharp/Configuration/prompt_template.txt:38` and its mirror in the built-in fallback,
`csharp/Services/ClaudeService.cs:497`, both read:

> The third query means: people under Sanjay who have a title, PLUS people under Sanjay who
> are in China.

That is a union of two subject-scoped sets. The plan rules the opposite. Its case table
(`.agents/plans/F04-genuine-conversational-answers.md:58`) gives "add the users in China" as
`under-Sanjay AND title AND country = China`, and the prose at `:64` states the reading in
words: *"Sanjay's reports with titles who work in China"*. The plan's guard clause at `:225`
requires a final plan carrying **all three constraints conjunctively**.

The description guidance one line below repeated the same error, telling the model to write
"Sanjay's reports with titles, plus his reports in China" — so a model that followed the
example would also *state* the union, and the interpretation statement that exists to make a
misread visible would confirm the misread instead.

Trigger: any thread whose follow-up adds a constraint. No configuration required.

## Predicted observable failure
Three turns: "everyone under Sanjay", "only the ones with titles", "add the users in China".
The plan comes back as an `or` group — Sanjay's titled reports unioned with Sanjay's China
reports — instead of an `and` of all three. The count is larger than the truth, every person
in it satisfies only part of what was asked, and the answer text says "plus his reports in
China", so the surface reads as intended behaviour rather than a defect.

## What
Both copies of the Translate prompt carried a worked example whose stated conclusion
contradicted the plan it was implementing. The example's *purpose* is to stop the model
escaping to a directory-wide set, and it does that correctly; the error is in the reading it
substitutes, which splits the subject into two arms rather than intersecting the constraints.

## Approach
The example now states the conjunctive reading and rejects the union explicitly, because the
union is the plausible misreading of "add" and was what the text previously taught: "people
under Sanjay who have a title AND are in China — all three constraints at once", followed by
both negative cases (not the whole China directory, and not a separate second set). The
description guidance is corrected to the plan's own wording, "Sanjay's reports with titles
who work in China". The directory-wide-escape warning is unchanged.

The real fix is the guard. `SubjectScopingPromptTests` already held both prompt paths against
one phrase list since Slice 6c — and it passed throughout, because every phrase it checks was
present in text that reached the wrong conclusion. A presence check cannot see a wrong
conclusion. The list is now paired with a `RequiredConjunctiveReading` list asserting what the
example must *teach*, and a `RetiredUnionReading` list forbidding the union wording in both the
example and the description, in both paths.

## Files changed
- `csharp/Configuration/prompt_template.txt:38-39` — conjunctive reading; both negative cases;
  corrected description wording.
- `csharp/Services/ClaudeService.cs:497-498` — the same two lines in the built-in fallback.
- `tests/AdQueryOrchestrator.Tests/Unit/SubjectScopingPromptTests.cs:46-68` — the two new
  phrase lists, applied in both `CheckedInTemplate_CarriesSubjectScopingGuidance` and
  `BuiltInFallback_CarriesTheSameGuidance`.

## Guard proof
- `SubjectScopingPromptTests.CheckedInTemplate_CarriesSubjectScopingGuidance` and
  `BuiltInFallback_CarriesTheSameGuidance` — each asserts the conjunctive reading is taught
  and the union wording is absent.
- Reverting `prompt_template.txt` alone: **Failed 1, Passed 2** (the template test).
  Restored. Reverting `ClaudeService.cs` alone: **Failed 1, Passed 2** (the fallback test).
  Restored. Both restorations confirmed byte-identical by `git diff --stat`. The guard
  therefore binds each path independently, which is what the two-path contract requires.
- `pwsh -NoLogo -NoProfile -File scripts/verify.ps1` — passed. 357 tests, 0 warnings, both
  published smokes passed, vulnerability audit clean.

## Coder dispute (if any)
None. The reviewer cited the contradiction between `prompt_template.txt:38` and the plan's
`:225` guard clause, and both cited lines say what it reported.

## Known gaps
Nothing here proves the *model* reads the corrected example the way the plan intends — that
needs a live provider and is not automatable in this suite. The plan's own guard clause
(`:225`) anticipates this: it asks for a test that a three-turn thread yields all three
constraints conjunctively, which requires either a recorded provider fixture or a stub that
returns a plan. This guard covers what is deterministically checkable — that both prompt paths
carry the ruled reading and neither carries the retired one.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview Slice 7 r2, re-dispatched
2026-07-31, inline session-only, `codex-commercial.ps1`). Harness `codex-cli 0.146.0`.
Reviewed `daacd2a9bf250994051f408ef47c346368b96ae6`, base
`fc208cf2f51cedeff33c8462fcdaacf18972220d` — the first round of this sweep to actually read
its dispatched range, from a coder-created disposable worktree pinned at the head SHA.
`guard_confirmed: false` — NuGet was unreachable from the reviewer's environment, so it could
not rerun the canonical verification; it changed no files. Envelope failed (eighth
consecutive) and was recovered from the log tail under the verdict-contract recovery rule.
Recorded verbatim:

> High: follow-up semantics contradict the approved goal. The prompt produces a union, while
> the plan requires all three constraints conjunctively. prompt_template.txt:38, F04 plan:225

Admitted at intake: concrete evidence in both prompt paths, an observable wrong result on the
plan's own load-bearing case, and a severity justified by the defect sitting on the ordinary
follow-up path with no configuration required to reach it.
