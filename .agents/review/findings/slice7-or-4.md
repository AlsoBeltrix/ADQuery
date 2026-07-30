# slice7-or-4: The reduction ladder can narrate a question with no result evidence

**Severity**: LOW — reachable only through a deliberately small `Answer:MaxReductionBytes`,
which the checked-in default avoids; but when reached, Narrate is asked to answer a question
it was given no facts about, and the failure is a confident fabricated answer rather than an
error.
**Status**: Open
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `<filled in after commit>`

## Evidence
`csharp/Services/AnswerReductionBuilder.cs:82-94`. The ladder tries four compositions in
order and returns the first that fits the byte cap:

- `keepAll` = {question, description, distribution, headline}
- `dropHeadline` = {question, description, distribution}
- `dropDistribution` = {question, description}
- `dropDescription` = {question}

The last two carry **no result evidence at all**: the plan description states what was
asked of the directory, never what came back. A cap small enough to select either sends
the model a question and nothing else, and `QueryJobManager.NarrateAsync`
(`csharp/Services/QueryJobManager.cs:526-534`) treats any non-blank reduction as
narratable.

`AnswerOptionsValidator` (`csharp/Configuration/AnswerOptionsValidator.cs:18-27`) rejects
`<= 0` and anything above `ReductionCeilingBytes` (13,652) but imposes **no floor**, so
`Answer:MaxReductionBytes = 200` is admissible configuration.

Trigger: `Answer:MaxReductionBytes` configured below the composed size of
{question + description + distribution}. Pre-existing since F04 Slice 2 (`2cb2511`); the
Slice 7 diff does not touch this file.

## Predicted observable failure
Deploy with a small `Answer:MaxReductionBytes`. A user asks "how many contractors are in
Bangalore?"; the directory answers 412. The reduction sent to Narrate is `QUESTION: how
many contractors are in Bangalore?` — no count, no distribution — and the answer rendered
above the headline is whatever the model invents. The headline and table beneath it are
correct, so the surface contradicts itself. A test that builds a reduction under a tiny cap
and asserts no factless string is returned catches it.

## What
The drop ladder was designed as a leakage ordering — the headline carries the AD values, so
it is shed first — and the bottom two rungs continue shedding past the point where anything
about the *result* survives. Emitting them satisfies the byte cap by giving up the reason
the call is made.

## Approach
The ladder stops at the last rung that still carries result evidence. `dropDistribution`
and `dropDescription` are removed: when neither the headline nor the distribution scalars
fit, `Build` returns `null`, Narrate is skipped, and the job completes with headline, table,
and export exactly as F04 Slice 2 already specifies for a null reduction — a supported,
tested path, not a new failure mode. The leakage ordering is unchanged: the headline is
still the first thing dropped, so the minimal-leakage intent that produced the order is
preserved while the factless rungs go.

`dropHeadline` is kept: the distribution is result evidence (row totals, distinct buckets,
singletons, blanks) and carries no AD values, which makes it the correct floor for both
concerns at once.

## Files changed
- `csharp/Services/AnswerReductionBuilder.cs:82-94` — the ladder ends at `dropHeadline`.
- `csharp/Services/AnswerReductionBuilder.cs:11-23` — the components record documents the
  evidence floor alongside the drop order.

## Guard proof
- `tests/AdQueryOrchestrator.Tests/Unit/AnswerReductionBuilderTests.cs` — under a cap too
  small for any evidence-bearing composition, `Build` returns null rather than a
  question-only reduction. Restoring the two rungs makes it FAIL.

## Coder dispute (if any)
Partial, on framing rather than on the defect. The reviewer describes this as evidence being
dropped "before the question"; the ordering itself is deliberate and documented
(`AnswerReductionComponents`, `:11-23`) — the headline goes first *because* it carries the AD
values. The defect is not the order but that the ladder continues below the last
evidence-bearing rung. The remedy is the reviewer's stated one — preserve result evidence
when applying the byte limit — implemented by truncating the ladder rather than reordering it.

## Known gaps
A derived startup floor on `Answer:MaxReductionBytes` — the symmetric treatment
`FollowUpOptionsValidator:49-59` already gives `FollowUp:MaxContextBytes` under F04-D6, where
the byte cap is a backstop and never the shaper — would make the small-cap configuration
unbootable rather than merely harmless. Not done here: it is a second mechanism for the same
guarantee, and one finding takes one commit. Worth raising as its own finding if the
asymmetry between the two validators is considered a defect in itself.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview Slice 7 r1, inline
session-only, `codex-commercial.ps1`). Harness `codex-cli 0.145.0`. Reviewed
`fc208cf2f51cedeff33c8462fcdaacf18972220d`, base
`6f32299d0986833d854608334914e61bd12c8af2` — both matched the dispatch.
`guard_confirmed: false` — the reviewer could not rerun the canonical verification (its
shell sandbox failed to initialize); it changed no files. Envelope recovered by one
re-emission-only re-prompt after a prose round — see `slice7-or-1` for the contract
record. Recorded verbatim:

> A reduced Answer:MaxReductionBytes configuration drops result evidence before the question
> at AnswerReductionBuilder.cs:81. The checked-in default avoids this. Narration can be
> invoked without facts. Severity LOW. Preserve result evidence when applying the reduction
> byte limit.

Admitted at intake despite falling outside the reviewed diff (Slice 2 code): the evidence
cites real lines, the predicted failure is observable, and the repo has already admitted a
pre-existing finding surfaced by an openreview round on a later slice (`slice5-or-2`).
