# slice6-or-2: The prompt treats any "instead of" correction as abandoning the conversation subject

**Severity**: MEDIUM — the scoping block's own exit condition licenses the escape it exists to prevent, for a phrasing users reach for constantly.
**Status**: Fixed
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `f0b6df3` (status recorded late — see [slice5r2-or-2](slice5r2-or-2.md))

## Evidence
`csharp/Configuration/prompt_template.txt:36` and the built-in fallback at
`csharp/Services/ClaudeService.cs:461` both read:

> Leave the subject ONLY when the user says so explicitly ("forget Sanjay,
> everyone in China", "start over", "instead of..."). Then drop the prior
> constraints and plan the new subject alone.

Bare `"instead of..."` is listed beside two genuine resets. "Instead of" is the
ordinary way to replace *a constraint* ("instead of titles, only people in
China"), not the subject, and the sentence then instructs the model to drop
**all** prior constraints.

## Predicted observable failure
Thread "everyone under Sanjay" → "only with titles" → "instead of titles, only
people in China". The prompt's own exit list makes a directory-wide plan the
licensed reading, so the answer covers every user in China. Indistinguishable to
the user from the pre-Slice-6 behavior the block was added to fix.

## What
The exit condition is stated by phrase-matching rather than by what is being
replaced, so a constraint replacement reads as a subject replacement.

## Approach
Restate the exit condition in terms of *what the user replaces*: leaving the
subject requires replacing the subject itself, and replacing a constraint keeps
it. Keep "forget Sanjay, everyone in China" and "start over" as genuine resets;
qualify the third to a subject-level form ("instead of Sanjay's org, everyone in
China"), and add the constraint-replacement counter-example so the two readings
are distinguished by example, not by wording alone. Both prompt paths change
together, as the slice requires.

## Files changed
- `csharp/Configuration/prompt_template.txt:36` — the exit condition and its counter-example.
- `csharp/Services/ClaudeService.cs:461` — the same wording in the built-in fallback.

## Guard proof
- `tests/AdQueryOrchestrator.Tests/Unit/SubjectScopingPromptTests.cs` —
  `RequiredGuidance` gains the constraint-replacement phrasing and asserts the bare
  `"instead of..."` reset trigger is gone, checked in both prompt paths. Reverting
  either path's wording makes its test FAIL.

## Coder dispute (if any)
None. The wording is mine, from `aed870e`; the reviewer is right that it
undercuts the rule it sits under.

## Known gaps
Prompt guidance is not deterministically testable end to end — the guard proves
both paths carry the corrected wording, not that a given model obeys it. That is
the same limit every prompt guard in this repo carries.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview, inline
session-only, `codex-commercial.ps1`). Harness `codex-cli 0.145.0`. Reviewed
`aed870e193c02726eedeb037f8a6969c430c29b4`, base
`00f0efbbdf8d96be8762759757ca1b4141285e57` — both matched the dispatch; envelope
schema-valid. Verdict `findings` (2 of 2), 2026-07-30 UTC.

> Keep the established subject unless the user explicitly replaces that subject,
> such as "instead of Sanjay's organization, search everyone." Remove bare
> "instead of..." as a reset trigger and add examples distinguishing constraint
> replacement from subject replacement in both prompt paths.

Admitted at intake: evidence cites both prompt copies, the predicted failure is
the slice's own target defect, and the remedy is the one implemented.
