# slice6r2-or-1: a template file missing its content placeholder silently sends a prompt with no payload

**Severity**: MEDIUM — a misedited template file produces a Narrate prompt carrying the RULES
block and no reduction. The model is told to answer a question it was never given, from evidence
it was never shown, and forbidden to say it is guessing. Nothing logs, and the answer renders
above a correct table.
**Status**: Fixed
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `<this commit>`

## Evidence
Both prompt templates are substituted by blind `string.Replace`, which is a no-op when the token
is absent:

- `csharp/Services/ClaudeService.cs:631` — `_answerPromptTemplate.Replace("{{REDUCTION}}", reduction)`.
  No `{{REDUCTION}}` in the file means the reduction is dropped and the template's fixed text is
  sent alone.
- `:610` — `prompt.Replace("{{USER_QUERY}}", userQuery)` on the Translate path, with the same
  property: a template missing that token sends plan-generation guidance and no user query.

The load path (`:74-101`) checks only `File.Exists` and that the read did not throw. A file that
loads is authoritative from then on: `BuildAnswerPrompt` (`:629`) and `BuildExecutionPlanPrompt`
(`:416`) both branch on `!string.IsNullOrWhiteSpace`, so a structurally useless template
suppresses the built-in fallback that would have worked.

The failure is silent by construction. `AnswerReductionBuilder` returning null is the case Narrate
already handles — the job completes with headline, table and export, and no answer. This is the
opposite: a non-empty reduction is built, the byte cap is respected, the call is made, tokens are
spent, and the payload is discarded between the builder and the wire, where nothing looks at it.

The `{{RESULT_LIMIT_GUIDANCE}}` replacement at `:608` is deliberately tolerant of an absent token
(slice3r2-or-1) and stays that way: it substitutes the empty string, so its absence changes
nothing. The distinction this fix draws is exactly that — a placeholder whose absence loses the
payload versus one whose absence loses nothing.

## Predicted observable failure
Edit `Configuration/answer_prompt_template.txt` to reword the closing line and delete the
`{{REDUCTION}}` line along with it — a plausible single-line edit, since it sits alone between
`REDUCTION:` and `Write only the answer text.`. Restart. Every query still returns a table, and
every answer sentence above it is invented: the model has the rules, the "never invent" line, and
no numbers. Logs show a successful Narrate call. The operator sees confident wrong prose over
correct data, with nothing indicating a bad template.

A test loading a template without the placeholder and asserting the reduction still reaches the
model catches it.

## What
Template loading validated the file's *presence* and not its *usefulness*, and the "use the file
when present" branch then treats a useless file as authoritative. The two properties the code
needs from a template are that it read and that it carries the slot for the payload; only the
first was checked.

## Approach
Validate at load, and on failure leave the field null so the built-in fallback is used — the
reviewer's "fall back safely" branch rather than the startup-throw branch. Falling back is the
better failure here: the fallback is fully functional and carries the same rules (held by
`NarratePromptPathsAgreeTests` and `SubjectScopingPromptTests`), so the app keeps working while
the operator's mistake is logged loudly, rather than refusing to start over a file the app does
not need.

Applied to both templates in the one place they are loaded, because it is one defect: the Translate
path has the same blind replace on `{{USER_QUERY}}` and the same "file wins over fallback" branch.

## Files changed
- `csharp/Services/ClaudeService.cs` — one `LoadTemplate` helper does existence, read, and
  required-placeholder validation for both templates; a file missing its placeholder is logged at
  error and rejected, so the fallback runs.
- `tests/AdQueryOrchestrator.Tests/Unit/TemplatesWithoutTheirPayloadSlotAreRejectedTests.cs` — new
  (4 tests).

## Guard proof
The placeholder validation removed (back to accepting any readable file) → 2 red of 4:
`AnAnswerTemplateWithoutItsPlaceholder_DoesNotSwallowTheReduction` and
`ATranslateTemplateWithoutItsPlaceholder_DoesNotSwallowTheQuery`. The other two are over-removal
sentinels — a valid template must still win over the fallback on both paths — and they stay green
under the revert, so the guard is not satisfiable by rejecting every template.

`scripts/verify.ps1` green with everything restored: exit 0, 357 tests, 0 failures, 0 warnings,
publish smoke and vulnerability audit passed.

## Coder dispute (if any)
None on the finding. One scope note: the reviewer named only the answer template; the Translate
template is fixed in the same commit because it is the same defect at the same load site, and
splitting it would leave a known instance of an admitted defect deliberately unfixed.

## Known gaps
Validation is placeholder presence, not template sanity. A template that keeps `{{REDUCTION}}` and
deletes every rule around it still loads — that is the operator's editorial choice, and the
two-path rule guards only the checked-in copy. A template carrying the placeholder twice
substitutes twice, which is harmless.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview Slice 6 round 2, inline
session-only, `codex-commercial.ps1`). Log header verified at the frontier pair:
`model: gpt-5.6-sol | reasoning effort: xhigh`.

**Envelope contract FAILED** — exit 0, prose only, no `--output-last-message` file written despite
`--output-schema`. Sixth consecutive round. Extracted under the recovery rule and verified against
code before admission. `guard_confirmed: false` — the reviewer reported "fresh verification and
local/remote freshness checks were blocked by the shell sandbox failure; review used local indexed
sources and recorded test evidence". It changed no files.

Recorded verbatim:

> Mostly yes—the architecture is the best fit for F04: deterministic translation/execution,
> bounded evidence, then optional narration. It preserves trustworthy results and exports while
> degrading safely if narration fails.
>
> Two hardening gaps remain:
>
> - Untrusted AD values share the narration prompt with instructions. Control characters are
>   flattened, but same-line prompt injection remains; the repo already acknowledges this residual
>   at .agents/review/findings/slice2-or-3.md:89. Add an explicit data boundary and adversarial
>   test.
> - A custom answer template without `{{REDUCTION}}` silently omits all evidence at
>   ClaudeService.cs:628. Validate the placeholder at startup or fall back safely.
>
> Verdict: keep the design; harden those seams. A single-call agent loop or deterministic prose
> templates would be worse for the stated goal.
>
> No code changed. Fresh verification and local/remote freshness checks were blocked by the shell
> sandbox failure; review used local indexed sources and recorded test evidence.

### Intake disposition for the other item

**Item 1 (narrator trust boundary) — DECLINED, third time.** The reviewer cites
`slice2-or-3.md:89` — the repo's own recorded known gap — as its evidence, which is the same
ground as [slice5r2-or-1](slice5r2-or-1.md)'s declined item 1 and the original
[slice2-or-3](slice2-or-3.md) intake. A gap the repo already documents, cited from that
documentation, is not a new finding. The remedy (an explicit untrusted-content delimiter in the
reduction format plus a matching template rule) is a format change touching the builder, both
Narrate prompt paths, and the cap accounting; `slice2-or-3` ruled it needs its own review, and
that ruling stands. Three consecutive rounds raising it is evidence the gap is worth a plan, which
is recorded in `.agents/state.md` — not evidence that a review-round commit should absorb it.
