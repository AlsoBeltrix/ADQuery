# F05 — A bare "how many" question answers with a number

**Status: Draft — implementation-blocked pending owner approval.** No owner decisions are
open: the export consequence this plan turns on was already ruled by the F04 export rule
(`.agents/decisions.md`, F04-D2 second half, 2026-07-28) and is applied here, not re-asked.

Found by the F04 post-deploy live smoke against production AD (2026-07-31), recorded in
`.agents/state.md`. This plan is self-contained; a cold agent can implement it without the
originating conversation. It extends F04 (`.agents/plans/F04-genuine-conversational-answers.md`,
Implemented) and changes no code outside the two Translate prompt paths and their guard.

## Problem

A bare count question makes the model emit an aggregation that groups on the same attribute
the plan has already pinned to a single value with a filter. The answer is then a two-bucket
distribution table instead of the number the user asked for.

Observed against live AD, job `86b348a6-54a9-46da-a271-c4d1e2d83de7`, query
*"how many enabled users are there?"*. The executed plan
(`E:\WWWOutput\mcoelho\adquery_MCOELHO_20260731_184858009.log`, `ExecutedPlanJson`) carried
both:

```json
"filters": [ { "attribute": "Enabled", "operator": "equals", "value": "true" } ],
"projection": { "aggregation": { "group_by": [ "Enabled" ], "count": true } }
```

Grouping by `Enabled` inside a result already filtered to `Enabled = true` can only produce
one meaningful bucket. It produced two, because three of the 42,218 matched records carry a
blank `Enabled`, so the delivered answer read:

> There are 42,215 enabled users in the directory. Note that among the 42,218 users grouped by
> their Enabled status, 3 had a blank value for this field and so are not counted as enabled.

Every number there is correct and the hedge is honest. The defect is the shape: a one-number
question was answered with a distribution and a caveat about the grouping attribute.

### Where the defect is not

`HeadlineClassifier.Classify` (`csharp/Services/HeadlineClassifier.cs:38`) is correct and
needs no change. Its precedence is fixed and total, and rung 3 — *"the plan requested
aggregation but no grouped payload reached here — a pure-count plan (empty group_by). The
scalar count is the answer"* (`:62-67`) — exists for exactly this question. The plan never
reaches rung 3 because rung 2 matches first: a non-empty `group_by` produced a grouped
payload, so `Kind = Grouped` (`:50-60`). `HeadlineClassifierTests.PureCount_EmptyGroupBy_IsCount`
(`:90`) already proves rung 3 works when the plan asks for it. The classifier is being fed a
plan that does not describe the question.

The defect is a **gap in the Translate prompt**. It lists six phrasings that mean *aggregate* —
`'summarize'`, `'group by'`, `'count by'`, `'unique list'`, `'distinct values'`, `'most
common'` (`csharp/Configuration/prompt_template.txt:28-29`) — and says nothing about the
commonest count question of all. Nothing tells the model that a bare "how many X" is a pure
count whose `group_by` must be empty, and nothing warns that grouping on an attribute a filter
has already pinned to one value buys nothing.

### The export consequence, already ruled

The fix must choose what the plan emits, and the two candidates differ in whether the user
still gets a download:

| Plan shape for "how many enabled users" | Headline | Export offered? |
| --- | --- | --- |
| aggregation with **empty** `group_by` (pure count) | `count` | **No** — `ExportAffordance.cs:46` returns false whenever `Projection.Aggregation != null` |
| **no aggregation**, rows returned | `count` | Yes — rows are the artifact, when `totalRows > 1` |
| aggregation with `group_by: ["Enabled"]` (today, accidental) | `grouped` | Yes — the distribution table is the artifact |

**This plan takes the pure count, and accepts that a bare count question offers no download.**
That is not a new decision: F04-D2's second half already ruled that *"a result whose answer is
a single scalar or one record has no meaningful export"*, and `ExportAffordance` and
`ExportAffordanceTests.PureCountAnswer_DoesNotExport` (`:76`) already implement and guard it.
Emitting no aggregation to preserve the download would contradict that ruling and would also
pull the whole matched set into the row path for a question that wants one integer. A user who
does want the list asks for it — "show me them" is one follow-up, and F04's whole-conversation
re-planning already handles it.

## Scope

**In scope.** Two Translate prompt paths and one guard. Nothing else.

**Out of scope, deliberately.** `HeadlineClassifier`, `ExportAffordance`, `QueryJobManager`,
the Narrate prompt, and the answer template are all correct here and must not be touched. No
code may be added that inspects a plan and rewrites its aggregation: that is the
guess-transform F04-D2 deleted, and reintroducing it in a new costume is forbidden. If the
model emits a bad plan shape, the fix is prompt guidance, never a code-side correction —
F04's architecture holds no conversation semantics in code.

## Approach

### The two-path contract

Both Translate prompt copies must carry the new guidance:

1. `csharp/Configuration/prompt_template.txt` — the external template, authoritative when the
   file loads.
2. `csharp/Services/ClaudeService.cs` — the built-in `StringBuilder` fallback used when the
   file is missing.

A missing template file must degrade *wording*, never a *rule* (the contract stated in
`BuildAnswerPrompt` and enforced for the sibling case by `NarratePromptPathsAgreeTests`). A
single-path edit here would ship a deployment where the rule silently does not exist. The
aggregation guidance currently lives at `prompt_template.txt:28-30` and mirrors at
`ClaudeService.cs:487-489`; the new lines belong immediately after, before the
`FOLLOW-UP QUERIES - CONVERSATION SUBJECT:` block.

### The guidance to add

Two rules, both stated positively and each with the counter-case that makes it decidable. Add
to both paths, wording identical apart from the C# string escaping:

- A bare "how many" question is a **pure count**: add the aggregation with `count: true` and an
  **empty** `group_by`, and the scalar total is the answer. "how many enabled users are there",
  "how many contractors do we have", "how many people report to X" are all pure counts. Only
  add `group_by` fields when the user asks for the breakdown *across* a value — "how many users
  in each department", "count by employee type", "how many of those are contractors vs
  employees". The test is whether the user asked for one number or a table of numbers.
- Never `group_by` an attribute that a filter in the same plan has already pinned to a single
  value. Grouping `Enabled` inside a plan filtered to `Enabled = true` cannot produce a
  distribution — it produces one bucket plus whatever blank-valued rows the directory happens
  to hold, and turns a one-number answer into a table with a caveat about the grouping
  attribute. If the filter constrains it, the count of it is the answer.

The second rule generalizes past the observed case: it also covers "how many contractors"
(filter `EmployeeType = CWK`, grouped by `EmployeeType`), "how many disabled users", and the
same shape arriving inside a follow-up, where the pinning filter came from an earlier turn.

### The guard

New `tests/AdQueryOrchestrator.Tests/Unit/BareCountPromptTests.cs`, modelled on
`SubjectScopingPromptTests` — which is the right model for a reason worth restating: that file
learned from `slice7r2-or-1` that a *presence* check cannot see a wrong conclusion. It now
pairs required phrases with a `Retired…` list asserting the wrong reading is absent. Follow
that structure:

- `CheckedInTemplate_CarriesBareCountGuidance` — reads
  `Configuration/prompt_template.txt` from `AppContext.BaseDirectory` and asserts the required
  phrases.
- `BuiltInFallback_CarriesTheSameGuidance` — drives `ClaudeService.GenerateExecutionPlanAsync`
  with no template file present, captures the outgoing prompt via the recording handler, and
  asserts the same list. Reuse `SubjectScopingPromptTests`' `RecordingHandler`,
  `CreateServiceWithoutTemplate`, and `PromptOf` helpers; extract them to a shared fixture only
  if that can be done without touching the existing tests' assertions.

Assert on phrases that carry the *rule*, not decorative wording — the empty-`group_by`
instruction, the one-number-versus-table test, and the already-pinned-attribute prohibition.
Include at least one negative assertion so a future edit cannot satisfy the guard while
teaching the opposite.

**Guard proof (required before the slice is claimed complete).** Revert each prompt path
separately and confirm exactly one test fails each time; restore both and confirm
`git diff --stat` reports them byte-identical. A guard that fails only when both paths are
reverted does not bind them independently, which is precisely the hole `slice5r2-or-1` found.

### Verification

`pwsh -NoLogo -NoProfile -File scripts/verify.ps1` must pass — currently 357 tests, 0 warnings.

### Live acceptance (manual; the suite structurally cannot cover it)

The suite proves both prompt paths carry the rule. It cannot prove the model *obeys* it: that
needs a live provider and real AD. After the fix deploys, re-run the smoke question
*"how many enabled users are there?"* against the deployed app and confirm the executed plan in
the per-job log under `E:\WWWOutput\<user>\` carries `"group_by": []`, the status payload's
`headline.kind` is `count`, and `downloadUrl` is absent. Record the job id and the outcome in
`.agents/state.md`. Until that is done the plan is `Evidence pending`, not `Complete` — the
same bar F04 was held to.

## Risks

- **The model may still group.** Prompt guidance is not a guarantee; this is the accepted cost
  of F04's architecture, which deliberately holds no conversation semantics in code. If the
  live acceptance check shows the model still emitting a pinned-attribute `group_by` after the
  wording lands, the next step is stronger wording or a worked example in the prompt — **not**
  a code-side plan rewrite, which F04-D2 forbids.
- **Over-correction.** Wording that pushes too hard toward pure counts could suppress a
  legitimate breakdown ("how many users in each department"). The guidance therefore states the
  positive case for `group_by` in the same breath, and the guard asserts that breakdown wording
  survives.
- **Scope creep into the classifier.** The temptation is to "fix" this in
  `HeadlineClassifier` by demoting a single-bucket grouped result to `count`. That would be
  wrong: `ExportAffordanceTests.SingleBucketGroupedAnswer_StillExports` (`:58`) shows a
  genuinely single-bucket distribution is a valid grouped answer, and demoting it would break
  a correct case to paper over a bad plan.
