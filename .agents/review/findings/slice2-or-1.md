# slice2-or-1: The evidence floor holds only for grouped results; count and record narrate factless

**Severity**: MEDIUM — the same fabricated-answer failure `slice7-or-4` closed, still open on
the two most common result shapes; and unlike that finding it needs no unusual configuration
value to be *shaped* wrongly, only a small one.
**Status**: Fixed
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `<this commit>`

## Evidence
`csharp/Services/AnswerReductionBuilder.cs:89-105`. The ladder is two rungs:

- `keepAll` = {question, description, distribution, headline}
- `dropHeadline` = {question, description, distribution}

`slice7-or-4` truncated it here on the reasoning that `dropHeadline` is "the last rung still
carrying result evidence" (the comment at `:92-97`). That reasoning holds only when
`distribution` is non-null. `DistributionSummarizer.Summarize`
(`csharp/Services/DistributionSummarizer.cs:27-33`) returns **null** for every result that is
not a grouped aggregation — explicitly, per its own summary at `:10-11`: "a scalar count or
single record has no distribution to describe". `Assemble` (`:115-119`) drops nulls silently,
so for a count or single-record result the surviving `dropHeadline` composition is exactly
`{question, plan description}` — the factless composition slice7-or-4 set out to eliminate.

`AnswerOptionsValidator` (`csharp/Configuration/AnswerOptionsValidator.cs:18-27`) still
rejects only `<= 0` and `> ReductionCeilingBytes`; there is no floor, so a cap that fits the
question and description but not the headline is admissible configuration.

## Predicted observable failure
Configure `Answer:MaxReductionBytes` just above the size of {question + description} and below
{question + description + headline}. Ask "how many contractors are in Bangalore?" The engine
answers 412. `keepAll` = {question, description, headline} exceeds the cap; `dropHeadline`
= {question, description} fits, because the distribution is null. Narrate receives a question
and the plan's own restatement of that question, and the invented count renders above a
correct headline reading 412. A test that builds a reduction for a `Count` headline with a
null distribution under such a cap and asserts `Build` returns null catches it.

## What
The fix for `slice7-or-4` reasoned about the *ladder* — which components a rung names — and
not about which components can actually be present at runtime. A rung that names the
distribution is not an evidence-bearing rung when the distribution is structurally absent, and
it is structurally absent for exactly the count and single-record shapes that make up the
ordinary case.

## Approach
Decide the floor from what the composition *contains*, not from which rung produced it: a
reduction is emitted only when it carries at least one component that describes the result
(`Headline` or `Distribution`). That is the invariant `slice7-or-4`'s comment already states;
this makes it true for every result shape rather than for grouped results only.

## Files changed
- `csharp/Services/AnswerReductionBuilder.cs` — the ladder is now
  `AnswerReductionComponents` values rather than `string?[]` positional arrays, so a rung can
  be inspected for what it carries. `CarriesResultEvidence` skips any candidate holding
  neither headline nor distribution; `Assemble` owns the fixed emission order that the arrays
  used to encode positionally. The type's doc comment records the contents-not-position rule.
- `tests/AdQueryOrchestrator.Tests/Unit/AnswerReductionTests.cs` — the guard.

## Guard proof
`NonGroupedResult_UnderACapThatExcludesTheHeadline_YieldsNoReduction`, a `[Theory]` over
`count` and `record`. Both cases build with a null distribution under a cap sized to exactly
`{question, plan description}` and assert `Build` returns null.

Temporarily removed the `CarriesResultEvidence` guard clause from the loop (the rest of the
refactor left in place, so the revert isolates the behavior and not the restructuring):
both cases fail — `Assert.Null() Failure: Value is not null / Actual: "QUESTION: how many
contractors are in Bangalore?\n"···`, i.e. the factless composition, reproduced. Restored:
11/11 in the class. `scripts/verify.ps1` green, 325 tests.

(Note: `Copy-Item` restoring the file preserved its older timestamp, so the first rerun
reused the stale build and looked red. Touching the file and rerunning gave the real green —
worth knowing for any future revert-restore proof on this repo.)

## Coder dispute (if any)
None. The finding is correct and it is a gap in my own `slice7-or-4` fix (`daacd2a`), not in
the original Slice 2 code: before that commit the ladder had lower rungs and this composition
was reachable by a different route. The reviewer found the residue.

## Known gaps
The derived startup floor raised as a known gap in `slice7-or-4` remains unbuilt, and this
finding strengthens the case for it: a floor computed from the largest *mandatory* component
would make the whole class unbootable rather than merely inert. Still a separate mechanism and
so a separate finding.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview Slice 2 r1, inline
session-only, `codex-commercial.ps1`). Harness `codex-cli 0.146.0`. Dispatched over base
`0ef62aaaee7d677d4b6138cd4735876ccc5036ba`, head `2cb251169c6cecd044b1b6ba3bc64f3408fb70f7`.
**Envelope contract FAILED** — the reviewer returned prose and wrote no
`--output-last-message` file, despite `--output-schema` being passed (the recorded LESSON
2026-07-30 mitigation, which therefore does not reliably prevent this). Findings extracted
under the verdict-contract recovery rule and verified against code before admission; the SHAs
could not be confirmed from a payload that does not exist, so the range is recorded from the
dispatch record. `guard_confirmed: false` — the reviewer reported its shell sandbox failed
during setup so it could not rerun `scripts/verify.ps1`; it changed no files. Recorded
verbatim:

> **Medium: narration can receive no result evidence.** valid low `Answer:MaxReductionBytes`
> select `dropHeadline`; count/record results, distribution null, leaving only question plan
> description. model may then invent answer. See AnswerReductionBuilder.cs:89 permissive lower
> bound in AnswerOptionsValidator.cs:17.
