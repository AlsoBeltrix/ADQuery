# F05 — A bare "how many" question answers with a number

**Status: Complete (2026-08-01)** — both slices landed (`0eedde9`, `4f5c3a2`), both
codereviewed (one MEDIUM already closed, one clean), deployed by the owner, and the live
acceptance check passed against production AD. Approved 2026-07-31, owner: *"assume I want a
working app, and make the app work. keep doing codereview codex with the default model and
effort for every slice. go."*

**Live acceptance evidence (job `96086288-aae0-431d-84d5-6a8d707d8ea5`, 2026-08-01).** The
question that failed on 2026-07-31 — *"how many enabled users are there?"* — now returns
**"There are 42,222 enabled users in the directory."** and nothing else. The executed plan
carries `"aggregation": { "group_by": [], "count": true }`
(`E:\WWWOutput\mcoelho\adquery_MCOELHO_20260801_025225782.log`), the headline kind is `count`,
`exportable` is `true`, and the advertised download served **HTTP 200, 824,464 bytes, 42,222
records**. All three acceptance criteria met, including the one F05-D1 added: a one-line answer
over many records offers its records.

No owner decisions are open. The export question this plan turns on is settled by **F05-D1**
(2026-07-31), which corrected F04-D2's wording rather than reinterpreting it.

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

### The second defect: a pure count wrongly withholds its export

Making the model emit a pure count exposes a second, independent defect in shipped code.
`ExportAffordance.HasExportableArtifact` (`csharp/Services/ExportAffordance.cs:46`) reads:

```csharp
HeadlineKind.Count => plan?.Projection?.Aggregation == null && totalRows > 1,
```

The presence of *any* aggregation object suppresses the download, whatever `totalRows` says.
So a pure-count plan offers no export even at 27,000 rows — the shape
`ExportAffordanceTests.PureCountAnswer_DoesNotExport` (`:76`) currently asserts as correct.

That is wrong, and **F05-D1 (2026-07-31) corrects it**: export turns on how many *records the
result holds*, never on how many lines the answer occupies. "How many managers in Thailand"
answers `43` and those 43 rows are exactly what the user wants next — the count is a summary
*of* an exportable set, not a substitute for one. "Who's the CEO" is the genuinely
non-exportable case: the answer on screen is the whole result.

The rows exist. `ComputeSettledAggregation` (`csharp/Services/QueryJobManager.cs:808-820`)
computes the aggregation *from* the rows and leaves the row set intact, and Slice 7 writes
every completed job's full result to its artifact regardless of plan shape. The export was
withheld by policy, not by absence of data.

| Plan shape for "how many enabled users" | Headline | Export, after F05 |
| --- | --- | --- |
| aggregation with **empty** `group_by` (pure count), many rows | `count` | Yes — the records are the artifact |
| single record, no aggregation | `record` | No — unchanged |
| single record, with an aggregation | `count`, `totalRows == 1` | No — unchanged |
| aggregation with `group_by: ["Enabled"]` (today, accidental) | `grouped` | Yes — unchanged |

Without this correction the prompt fix would make the product strictly worse: it would turn a
question that currently yields a table *and* a download into one number and no way to get the
list. The two changes ship together, as two slices.

## Scope

**In scope.** Two slices:

- **Slice 1** — the two Translate prompt paths and their guard.
- **Slice 2** — the `Count` arm of `ExportAffordance` and its guards (F05-D1).

They are independent and land in that order, one commit each. Slice 1 first, because it is
the observed defect; Slice 2 must not be skipped, since Slice 1 alone removes a download the
user has today.

**Out of scope, deliberately.** `HeadlineClassifier`, `QueryJobManager`, the Narrate prompt,
and the answer template are all correct here and must not be touched. No code may be added
that inspects a plan and rewrites its aggregation: that is the guess-transform F04-D2 deleted,
and reintroducing it in a new costume is forbidden. If the model emits a bad plan shape, the
fix is prompt guidance, never a code-side correction — F04's architecture holds no
conversation semantics in code. Within `ExportAffordance`, only the `Count` arm moves: the
`Grouped`, `Record`, and zero-row arms are correct and stay as they are.

## Approach — Slice 1: the prompt

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

## Approach — Slice 2: export follows the record count (F05-D1)

One line of production code. In `csharp/Services/ExportAffordance.cs`, the `Count` arm becomes:

```csharp
HeadlineKind.Count => totalRows > 1,
```

The aggregation test is deleted; `totalRows <= 0` is already handled by the guard clause above
the switch, and `totalRows == 1` remains non-exportable through the same expression, so the
single-record case F04-D2 got right is preserved by construction rather than by a second check.
Update the XML doc on `HasExportableArtifact`, whose `count` bullet currently explains the
aggregation test and would otherwise document behaviour that no longer exists.

**Guards.** In `ExportAffordanceTests`:

- Replace `PureCountAnswer_DoesNotExport` (`:76`) with `PureCountOverManyRecords_Exports` —
  `Decide(AggregationPlan(), 27000)` must now be true, with a comment naming F05-D1 and the
  "how many managers in Thailand" case that earned it.
- Add `PureCountOverASingleRecord_DoesNotExport` — `Decide(AggregationPlan(), 1)` stays false.
  This is the overshoot guard: without it, the correction could drift into offering a download
  for a one-record answer, which is the case F04-D2 got right.
- `SingleRecordAnswer_DoesNotExport`, `SingleRowWithoutTheRecordItself_DoesNotExport`,
  `EmptyAnswer_DoesNotExport`, `SingleBucketGroupedAnswer_StillExports`, and
  `MultiRowListAnswer_Exports_TheRowsAreTheArtifact` are unchanged and must stay green — they
  are the evidence that only the intended arm moved.

**Guard proof.** Both new assertions must be proven red before the fix: run them against the
current one-line expression and confirm `PureCountOverManyRecords_Exports` fails. Then apply
the fix and confirm both pass with the rest of the file still green.

Also re-check the browser-level `ExportAffordanceRenderingTests` and any status-DTO test that
pins `downloadUrl` for an aggregation plan; a fixture asserting today's suppression must be
updated with the same reasoning, not left to fail silently as an unrelated red.

### Verification

`pwsh -NoLogo -NoProfile -File scripts/verify.ps1` must pass for each slice — currently 357
tests, 0 warnings.

### Review

Per the owner's standing directive (2026-07-31), each slice gets a `codereview codex` round at
the harness's default model and effort. Findings are handled one per commit under the
`codereview` playbook.

### Live acceptance (manual; the suite structurally cannot cover it)

The suite proves both prompt paths carry the rule. It cannot prove the model *obeys* it: that
needs a live provider and real AD. After the fix deploys, re-run the smoke question
*"how many enabled users are there?"* against the deployed app and confirm the executed plan in
the per-job log under `E:\WWWOutput\<user>\` carries `"group_by": []`, the status payload's
`headline.kind` is `count`, and — after Slice 2 — `downloadUrl` **is present**, since the
result holds many records. Record the job id and the outcome in `.agents/state.md`. Until that
is done the plan is `Evidence pending`, not `Complete` — the same bar F04 was held to.

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
- **Landing Slice 1 without Slice 2.** This is the one sequencing hazard. Slice 1 alone
  converts a question that today returns a table *and* a download into one number with no way
  to reach the records — a regression in user-visible capability, arrived at by fixing a
  defect. If Slice 2 cannot land for any reason, Slice 1 must be reverted rather than left in
  place.
