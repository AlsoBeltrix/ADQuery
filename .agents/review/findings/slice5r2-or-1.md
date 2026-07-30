# slice5r2-or-1: the Narrate fallback prompt never learned the completeness rule

**Severity**: MEDIUM — on a deployment whose answer template file is missing, an incomplete
result is narrated as a complete one. That is exactly the defect `ci-or-1` was raised to close,
still reachable through the fallback path the repo maintains precisely so a missing file cannot
change behavior.
**Status**: Fixed
**Branch**: — (repo policy: commit on `master`, one finding per commit)
**Commit**: `<this commit>`

## Evidence
Narrate has two prompt sources and `BuildAnswerPrompt` picks between them at
`csharp/Services/ClaudeService.cs:620-624`: the external template when loaded
(`:88-101`, `Configuration/answer_prompt_template.txt`), the built-in `StringBuilder` block
otherwise (`:627-647`). Its own doc comment states the contract: the fallback "carries the same
rules so a missing file degrades the wording, never the bound or the no-invention constraint"
(`:615-618`).

`ci-or-1` (`bfbd31f`) added the completeness rule to the external template only —
`answer_prompt_template.txt` gained "When a COMPLETENESS line is present … Every figure is then
a floor: say 'at least' … Never give a capped count as the count." The commit's own file list
does not include `ClaudeService.cs`, and the fallback's RULES block at `:633-640` still has no
COMPLETENESS rule.

Meanwhile `AnswerReductionBuilder` emits `IncompleteLine` (`:94-96`) into *every* reduction of an
incomplete result, on both paths, and deliberately excludes it from the drop ladder. So the
fallback path receives the COMPLETENESS line and has been told nothing about what it means.

The `SubjectScopingPromptTests` class comment states the same two-path rule for the Translate
prompt and guards it in both paths (`:19-27`). The Narrate prompt has no equivalent guard, which
is why the divergence landed silently.

## Predicted observable failure
Deploy with `Claude:AnswerPromptTemplate` pointing at a path that does not exist — or ship a
publish that omits `Configuration/answer_prompt_template.txt`, which the fallback exists to
survive — and run a query that trips a node-limit stop over a large org. The reduction carries
`COMPLETENESS: partial — …`, the model is given no rule about it, and the narrated sentence can
report the truncated count as the count. The code-written caveat still appends beneath it, so the
user reads a confident total followed by a contradicting caveat.

A test asserting the fallback RULES block carries a completeness rule catches it.

## What
`ci-or-1` fixed the surface it was looking at. The Narrate prompt is maintained in two places by
design, and the fix touched one — the same class of divergence the Translate prompt is guarded
against and the Narrate prompt is not.

## Approach
Give the fallback the same rule, in the fallback's own compressed wording, and add the missing
two-path guard: a test class that asserts both Narrate prompt sources carry the rules Narrate
depends on. The guard is what stops the next single-path edit, so it covers the whole RULES
contract the doc comment claims, not just this one line.

## Files changed
- `csharp/Services/ClaudeService.cs` — the fallback RULES block gains the completeness rule.
- `csharp/Services/ClaudeService.cs` — `BuildAnswerPrompt`'s doc comment now states the two-path
  rule as a contract and names the test that holds it.
- `tests/AdQueryOrchestrator.Tests/Unit/NarratePromptPathsAgreeTests.cs` — new (3 tests). Asserts
  both paths carry each rule Narrate's contract names, driven by one shared list so a rule added
  to the list is checked in both, plus an over-removal sentinel that the fallback still carries
  the reduction.

## Guard proof
The fallback's completeness rule removed → 1 red of 3 in `NarratePromptPathsAgreeTests`
(`TheBuiltInFallback_CarriesTheSameRules`, "the built-in Narrate fallback must carry the 'a
floor, not the count' rule"). Restored, then the *template's* rule removed →
`TheCheckedInTemplate_CarriesTheRules` red ("the external answer template must carry the
'completeness' rule") while the fallback test passed — the guard binds both directions, not only
the path just fixed. Template restored byte-identical.

`scripts/verify.ps1` green with everything restored: exit 0, 353 tests, 0 failures, 0 warnings,
publish smoke and vulnerability audit passed.

## Coder dispute (if any)
The reviewer stated this as "prompt guidance exists in both a template and a fallback
implementation; centralize it to prevent semantic drift" — a general observation about the
duplication, without naming a divergence. **Admitted on the specific divergence found while
verifying it**, which is a live behavior defect; **the centralization remedy is declined.**

Collapsing the two paths into one would mean either deleting the fallback (a deployment with no
template file then cannot narrate at all — the fallback exists so a missing file degrades wording,
not function) or generating the template from code at build time (a config file the operator is
meant to be able to edit, then no longer editable). The duplication is deliberate; what was
missing is the guard that keeps the copies honest. That guard now exists for Narrate, matching
the one Translate already had.

## Known gaps
The guard checks that both paths carry each rule, not that the wordings are semantically
equivalent — two paraphrases that diverge in meaning while both containing the checked phrase
would pass. Phrase-level checking is the same limit every prompt guard in this repo carries.

`BuildSystemGuidance` and the Translate fallback carry further duplicated guidance with only the
`SubjectScopingPromptTests` phrases guarded; a full two-path audit of the Translate prompt is not
in this fix.

## Reviewer comments
`Reviewer: codex / gpt-5.6-sol / xhigh / frontier` (openreview Slice 5 round 2, inline
session-only, `codex-commercial.ps1`). Log header verified at the frontier pair:
`model: gpt-5.6-sol | reasoning effort: xhigh`. Dispatched over the Slice 5 range.

**Envelope contract FAILED** — exit 0, prose only, no `--output-last-message` file written
despite `--output-schema`. Fifth consecutive round to fail this way. Extracted under the recovery
rule and verified against code before admission. `guard_confirmed: false` — the log carries the
Windows sandbox helper failures again; the reviewer reported it "could not rerun the canonical
verifier because the known Windows sandbox helper failure prevented shell execution". It changed
no files.

**Range note:** as in the two preceding rounds, the reviewer answered the whole-change question
against the current tree rather than confining itself to the dispatched range. Admissible for the
same reason: the round's question is the whole-change one.

Recorded verbatim:

> Yes at the architectural level; not quite at the hardening level.
>
> Keep the current `Translate → deterministic execution → bounded reduction → Narrate` design.
> Re-planning the complete conversation is cleaner than maintaining model-generated plan patches,
> and the model never receives full result sets. That is the right shape for the goal.
>
> Before calling it the best implementation, I would address:
>
> - The narrator still receives instructions and untrusted AD values in one message. The code
>   acknowledges that a single-line directory value can resemble an instruction
>   (AnswerReductionBuilder.cs:279, ClaudeService.cs:632). Use a typed serialized reduction with
>   an explicit untrusted-data boundary and dedicated narrator instructions.
> - Prompt guidance exists in both a template and a fallback implementation. Centralize it to
>   prevent semantic drift.
> - `QueryController` and `QueryJobManager` remain structurally overloaded. Preserve behavior now,
>   then decompose under the existing P21 plan rather than redesigning F04 (P21:28).
> - Two review records still say "Open," but their fixes and regression guards are present (retry
>   ancestry QueryController.cs:1360, subject correction prompt_template.txt:36). The records need
>   a drift reconciliation.
>
> Static diagnostics are clean for the affected implementation and tests. I could not rerun the
> canonical verifier because the known Windows sandbox helper failure prevented shell execution.
>
> Verdict: retain the architecture; harden the narrator's trust boundary and later perform
> behavior-preserving decomposition. No rewrite is warranted.

### Intake dispositions for the other three items

**Item 1 (narrator trust boundary) — DECLINED as a finding; it is the recorded known gap of
[slice2-or-3](slice2-or-3.md), verbatim.** That record admitted the structural half (delimiter
forgery, fixed in `Clip`) and wrote the remainder into "Known gaps": "Collapsing newlines does not
stop a single-line value from reading like an instruction … Defending that needs an explicit
untrusted-content delimiter in the reduction format and a rule in the template … That is a format
change with its own review; it is not folded into this fix." The reviewer's own citations
(`AnswerReductionBuilder.cs:279`, `ClaudeService.cs:632`) are the comments recording that
decision. A known gap the repo already documents, restated without new evidence, is not a new
finding — and the remedy is a format change that needs a plan, not a review-round commit.

**Item 3 (`QueryController`/`QueryJobManager` overloaded) — DECLINED as a finding.** The reviewer
routes it to the existing `P21-behavior-preserving-component-decomposition.md` plan itself and
explicitly says not to redesign F04 for it. It names no defect and predicts no observable failure;
it is backlog, already recorded.

**Item 4 (two records say "Open") — ADMITTED, separately, as
[slice5r2-or-2](slice5r2-or-2.md).** Verified: `slice6-or-1` and `slice6-or-2` both read
`**Status**: Open` while their fixes are committed (`7e94872`, `f0b6df3`) and their guards are in
the suite.
