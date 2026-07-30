# ci-or-1: A truncated traversal narrates as a complete answer

**Severity**: HIGH — the answer is confidently wrong about the one thing the user asked, on
exactly the large queries the safety limits exist for, and the surface that hides the
correction (the chat bubble) is the one F04 made primary.
**Status**: Open
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `<filled in after commit>`

## Evidence
The executor records incompleteness as free-text warnings and nothing else:

- `csharp/Services/DirectoryPlanExecutor.cs:239` — `"Result set truncated to {N} rows."`
  after `result.Data` is cut to `plan.ResultLimit`.
- `:453` — `"Stopped at {maxNodes} nodes (limit reached, {N} nodes truncated)"`.
- `:471` — `"Stopped at depth {maxDepth} (safety limit, {N} unexplored nodes)"`.

All three land in `result.Warnings` (`:242`). `PlanExecutionResult`
(`csharp/Services/IDirectoryPlanExecutor.cs:23-46`) carries no completeness field, so the
warning *string* is the only record that the set is partial.

Narrate never sees them. `QueryJobManager.NarrateAsync:598` takes `totalRows =
result.Data.Count` — the count *after* truncation — and passes it to
`HeadlineClassifier.Classify` and `DistributionSummarizer.Summarize`.
`AnswerReductionBuilder.Build` (`csharp/Services/AnswerReductionBuilder.cs:68-80`) has no
warnings parameter: the reduction is `{question, plan description, headline, distribution}`
and nothing else. So the model is handed a truncated count as if it were the answer, and the
template (`csharp/Configuration/answer_prompt_template.txt:8-10`) instructs it to state that
number directly.

The warnings do reach the browser (`QueryController.BuildCompletedResult:1074`) and render in
the results panel (`app.js:416`, `:808`, `renderWarnings:851`). They do **not** reach the chat
bubble: `summariseJobForChat:1301` returns `result.answer` verbatim and reads no other field.

## Predicted observable failure
Ask "how many people report up through Sanjay?" over an org subtree deeper or wider than the
configured safety limits. The traversal stops at the node or depth limit and warns. The chat
bubble reads "There are 4,000 people in Sanjay's organization." — a confident, specific, wrong
number with no qualification. The real figure is larger and unknown. The warning explaining
this sits in the results panel, which the F04 chat surface exists to let the user not read. A
test that runs a job whose executor returns rows plus a truncation warning and asserts the
reduction carries a completeness marker catches the reduction half; a T1 harness test that the
bubble shows a caveat catches the rendering half.

## What
Slice 2's reduction was specified as the four components that describe *the result*, and
completeness was not recognised as one of them — the design treated warnings as diagnostics
for the operator rather than as a fact about the answer. It is the second: whether a count is
the count or a floor changes what the sentence means.

Note the reduction cannot simply carry the warning text. Warnings are free-text, unbounded in
number, and would blow the byte accounting `AnswerOptions.ReductionCeilingBytes` derives from
fixed component maxima. The reduction needs a *bounded, server-derived* completeness fact, not
the strings.

## Approach
Two halves, both server-decided:

1. `PlanExecutionResult` gains an explicit completeness flag set where the executor already
   truncates (the three sites above), rather than inferred by parsing warning text back out.
   That flag rides into the reduction as one short bounded line, and the Narrate template
   gains a rule requiring the answer to say the figure is a floor when it is set.
2. The chat bubble appends a deterministic, code-written caveat when the job is incomplete —
   deterministic because the model must not be the only thing standing between a truncated
   count and a confident sentence.

## Files changed
- (to be filled in)

## Guard proof
- (to be filled in)

## Coder dispute (if any)
None on the defect. One scoping note: the reviewer described this as a gap in the F04 design
as a whole rather than in the CI-fix range it was dispatched over (`b4ed25f..5a86080`, which
only makes the artifact root configurable). That is correct — the round's question is the
whole-change one, and the finding is filed under the round that produced it.

## Known gaps
`plan.ResultLimit` truncation at `:239` is a *requested* limit as well as a safety one — a
user who asks for the top 10 is not getting an incomplete answer. The fix must distinguish
"you asked for fewer" from "we could not get them all", or it will caveat ordinary answers
into noise.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview CI-range round, inline
session-only, `codex-commercial.ps1`). Harness `codex-cli 0.146.0`. Log header verified at the
frontier pair: `model: gpt-5.6-sol | sandbox: workspace-write | reasoning effort: xhigh`.
Dispatched over base `b4ed25f`, head `5a86080`.
**Envelope contract FAILED** — exit 0, prose only, no `--output-last-message` file written
despite `--output-schema`. Second consecutive round to fail this way; the schema flag is not a
reliable mitigation. Finding extracted under the recovery rule and verified against code
before admission. `guard_confirmed: false` — the reviewer reported "I could not rerun
verification because local shell execution was unavailable"; the log carries 18
`windows sandbox: helper_unknown_error: setup refresh had errors`. It changed no files.
Recorded verbatim:

> **High:** Narration can present incomplete results as complete. Execution records truncation
> and safety-limit warnings in DirectoryPlanExecutor.cs:238, including unexplored nodes at
> lines 452 and 470. But AnswerReductionBuilder.cs:32 excludes warnings/completeness, while
> QueryJobManager.cs:598 narrates from the returned row count. The chat then returns that
> answer without warnings in app.js:1301. A safety-capped org traversal could therefore
> produce a confident but incomplete count.
>
> Recommended correction: add server-derived structured completeness (`complete`, `truncated`,
> `partial`) to the bounded reduction and always append a deterministic caveat in chat for
> incomplete results.

The reviewer also affirmed the architecture (translate → deterministic execution → bounded
narrate; re-planning cumulative intent; completion-time JSONL artifacts with exact-plan reuse)
and noted that F04 has never run against live AD, which remains an open owner matter recorded
in `.agents/state.md`.
