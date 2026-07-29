# F04 — Genuine Conversational Answers

**Status: Draft, openreview round 1 resolved (2026-07-29); all six owner decisions D1–D6 ruled (2026-07-29). Not approved; no code lands until the owner flips this status line to `Approved`.** The rulings are recorded as `F04-D1`…`F04-D6` in `.agents/decisions.md`; the "Open owner decisions" section below carries each one's status line and is now a record, not a queue.

**Review history.** openreview codex (gpt-5.6-sol @ xhigh, frontier, inline session-only) over `cb3568f..762bb0c`: three findings, all admitted and resolved in this document — `f04-or-1` (create the artifact and migrate all four cache readers before dropping the cache; the prior evidence bullet wrongly claimed a completion-time artifact existed), `f04-or-2` (reuse keys on the *complete* plan, not membership steps — the stored artifact is already projection-reduced), `f04-or-3` (Narrate needs a distribution summary; `HeadlineResult` alone cannot express near-uniqueness). Records: `.agents/review/findings/f04-or-{1,2,3}.md`.

**Design history (do not re-litigate).** Three architectures were explored and rejected by the owner before this one, each for a specific reason worth preserving:
1. *Second synthesis pass bolted onto the existing pipeline* — rejected as salvaging the old app ("that will not be approved"); a call that merely reformats another call's output is the wrong shape.
2. *Frozen membership + projection-only refinements* — rejected as brittle, and shown to be wrong: it cannot express legitimate membership changes ("add the users in China", "no, the Sanjay in Boston").
3. *Delta/patch operations against a thread plan* — rejected as more brittle still; a grammar of named edit operations is more rules, not fewer.

The through-line: each attempt tried to make misinterpretation *impossible* by encoding conversation semantics in code. This plan makes misinterpretation *visible and cheap to correct* instead, and holds no conversation-semantics rules in code at all.

This plan is self-contained. A cold, less-capable agent can implement it without the originating conversation. It builds on F01 (`.agents/plans/F01-conversational-query.md`, Implemented) and F02 (mockup UI, Done); read the F01 Design contract for the chat/main-window tokens — F04 does not restate them.

## Problem

The app was redirected to answer routine AD questions conversationally (F01 entry point, 2026-07-27), but it still behaves as a bulk-export tool. The root cause is architectural: **the model only ever produces a query plan. It never sees the results and never writes an answer.** Everything the user reads back is assembled by code from a fixed template. The app cannot *talk about* Active Directory — it can only translate one sentence into one spreadsheet.

Observed evidence (deployed logs read directly 2026-07-28/29, per-job `.log` and `.csv` under `E:\WWWOutput\mcoelho\`, framework logs under `D:\inetpub\adquery\logs`):

1. **"How many" produces a file, not a number.** Job `bc623ae0`, query *"how many users roll up to Sanjay Abhyankar"*: the model's own plan `description` reads *"Count all users … rolling up to Sanjay"* — it understood a count was asked. But the emitted plan carried row columns (Name/Title/Department) and **no aggregation**, so the engine produced a **77-row CSV** (`Records: 77`). The answer — 77 — exists in the log line and is never spoken; the user is handed a spreadsheet to count themselves. There is exactly one model call per query, `GenerateExecutionPlanAsync` (`csharp/Services/IClaudeService.cs:10`), and it emits only the plan; nothing consults the model after execution, so the chat bubble is filled by the code template `summariseJobForChat` (`csharp/wwwroot/js/app.js:1171`), e.g. `"42 matches. See the result panel."`.

2. **A guess-based transform silently mutates results.** Job `eed12d05` (Opus) / retry `173508149` (gpt-5.5), query *"what's the most common value in extensionAttribute1"*: both models produced a **correct** plan — search all Users, `aggregation: { group_by: [extensionAttribute1], count: true }`. The engine then discarded that aggregation and expanded to **26,625 data rows** via the heuristic at `csharp/Services/QueryJobManager.cs:354-400` ("if projection columns exactly match `group_by`, the user wants unique values as data"; `aggregation = null` at `:393`). Parsing the emitted CSV directly: sum of all bucket counts = 47,388 (every user); the real distribution is `(empty)` 7,150 · `Contractor` 6,100 · `Service` 4,322 · `CONFROOM` 1,478 · `other` 959 · `Other` 704 · … with **26,612 buckets of count 1** (near-unique numeric values). So the "most common value" question has no meaningful single answer, and the app answered it with a 26k-row dump. Two distinct defects surfaced here: the guess-transform (2) and case-sensitive grouping (see 4).

3. **Follow-ups crash on a technicality.** Job `3b3223c7`, refinement *"only users with titles"* following the Sanjay roll-up, failed with `Projection filter value is required`. The model correctly refined the prior query (added a `title not_equals ""` projection filter) but `PlanValidator.cs:229-233` rejects any empty filter `value`. A conversational refinement faulted the whole turn on a validator edge case.

4. **Grouping is case-sensitive.** `ComputeAggregation` keys buckets on `value?.ToString()` (`csharp/Services/QueryJobManager.cs:483`), so `Contractor` (6,100) / `contractor` (9) / `CONTRACTOR` (3) are three buckets, as are `other`/`Other`/`OTHER` and `CONFROOM`/`confroom`. Exact counts are individually correct but fragment what a human means by "one value," corrupting any "most common" answer.

The F01/F02 work delivered the chat *surface*, the plan-shape headline (`HeadlineClassifier`), and the follow-up plumbing (`previousJobId`, byte-capped last-turn context via `FollowUpContextEnforcer.Compose`). What is missing is the assistant itself.

## Architecture

The app is a **natural-language conversation wrapped around a deterministic AD query engine.** The LLM is only ever two things, never a third:

- a **translator** — the conversation so far → one complete structured query (this is why the app exists; only an LLM reads "just the ones in Seattle named Jane who started in December" as field/operator/value triples), and
- a **narrator** — a *bounded reduction* of the result → a sentence of text.

The LLM **never executes** a query, **never filters rows**, and **never holds the result set.** All row-level work — search, expansion, filtering, aggregation, counting — is deterministic code.

### The whole state is the conversation

There is **no frozen base set, no delta/patch language, no membership-vs-projection split enforced on the model, and no refine-vs-fresh classification.** Those are all attempts to encode conversation semantics in code, and every one of them adds a rule that can be wrong. Instead:

> **Every turn, the model re-plans the entire accumulated intent from scratch, given the conversation.**

The thread's state is the *conversation text* — the questions asked so far, which is small. Each turn the model emits one complete, self-contained `DirectoryQueryPlan` for the cumulative intent, exactly as it does today for a single question. Code executes that plan and narrates the result.

Every case this design was stress-tested against falls out with no special handling:

| Turn | Plan the model emits |
| --- | --- |
| "everyone under Sanjay" | under-Sanjay |
| "only with titles" | under-Sanjay AND title populated |
| "add the users in China" | under-Sanjay AND title AND country = China |
| "no, the Sanjay in Boston" | corrected seed, same shape |
| "forget Sanjay, everyone in China" | country = China (subject deliberately replaced) |

### Follow-ups are scoped to the conversation's subject

The "add the users in China" row above is the load-bearing case. A literal reading — `(under-Sanjay AND title) OR (all China users)` — is technically faithful to the words and **useless in practice**: it would return Sanjay's titled reports *plus the entire China directory*. A conversation has a **subject**, and once established, follow-ups are understood *within* it. "Add the users in China" means "bring China into what we are discussing," i.e. *Sanjay's reports with titles who work in China*.

So the default reading of any follow-up is: **scoped to the current subject** — narrowing or extending *within* it — never silently escaping to a directory-wide set. Escaping the subject requires the user to say so ("forget Sanjay…"). This is not a rule encoded in code; it is context supplied to the model in the Translate prompt, and it is the same natural-language interpretation job as mapping "started in December" to `whenCreated`.

### Ambiguity is made visible, not made impossible

Because every answer is text, **the app states the interpretation it used**: *"Sanjay's reports with titles who work in China: 12."* If that is not what the user meant, they see it immediately and correct it in one sentence, exactly as they would with a person.

This is the design's central robustness property, and it replaces the earlier drafts' machinery. Freezing membership and delta-patching were both attempts to make misinterpretation *impossible*, which is precisely what made them rigid and unable to express "add China" or "no, the other Sanjay." Making misinterpretation **visible and cheap to correct** costs nothing, needs no rules, and handles cases no rule set would anticipate.

### A turn has three phases

1. **Translate.** The model reads the conversation (prior questions + the new message) and emits **one complete plan** for the cumulative intent. It is never told "you may only change the projection" or "reuse these steps" — it always does the same simple thing.
2. **Execute + reduce.** The engine runs the plan; the **full result** is persisted as the on-disk job artifact (already happens — `OutputFile` under `QueryLogHelper.OutputRoot`); a **bounded reduction** is computed (the existing B1 `HeadlineResult` — a scalar count, ≤10 grouped buckets, or one record — plus a scalar distribution summary for grouped plans; see Slice 2).
3. **Narrate.** The model reads {the conversation + the bounded reduction} and writes the answer, stating the interpretation it used. It reasons over *real reduced data*, so extensionAttribute1 yields *"near-unique — 26,612 of ~27k values appear once and 7,150 are blank, so there is no meaningful most-common value; the largest real bucket is Contractor at 6,100"* instead of a dump.

### Drift, and the only optimization

Re-planning every turn means the count could in principle change between turns for an unchanged intent (the "77 quietly became 74" concern). Two things resolve it:

- **Correctness:** the plan changes only if the intent changed or the person-match is genuinely ambiguous. If a stable intent does *not* reproduce, that is a real ambiguity the conversation should surface — visible in the narrated answer — not something to freeze away.
- **Cost:** re-executing a 40k-node org traversal on every refinement is a *performance* problem, not a correctness one. It is solved by an optimization invisible to the design: **when a turn's complete plan is byte-identical to a prior turn's in the same thread, reuse that turn's artifact instead of re-traversing** (a cache keyed on exact whole-plan equality — steps *and* projection, filters, aggregation, limit, because the stored artifact is already reduced to one projection's shape; see Slice 7). A repeated question is free; a changed intent re-executes correctly. The model is never constrained by this — code simply skips redundant directory work.

### Why two model calls per turn is the right shape (not the rejected one)

An earlier draft's second call *reformatted* the first call's output — the owner correctly rejected that as "the wrong shape." This architecture's two calls are **different jobs**: Translate (conversation → structure) and Narrate (reduced data → words). Narrate is the *only* point at which the model sees what AD returned; without it the model must commit to answer wording before the data exists and cannot react when the data is degenerate (the extensionAttribute1 case). What crosses the wire on Narrate is the **bounded reduction** (a count, ≤10 buckets, or one record), never rows and never the full set — the same size and sensitivity DATA-D1 already permits for follow-up context. See D1.

## Relevant settled decisions (canonical in `.agents/decisions.md`; restated as constraints)

- **DATA-D1 (amended 2026-07-27)** — bounded AD values may be sent to any configured model route (primary or alternate); only a minimal slice (preview slice or aggregation summary), never the full result set, never 10k rows; full downloads stay server-side. **Both F04 model phases are value-bounded by DATA-D1**; Narrate sends only the B1-bounded reduction.
- **FOLLOWUP-D1 / FOLLOWUP-D2 (D2 amended 2026-07-29 by F04-D6)** — the follow-up bound is server-enforced and lives in its own knob (FOLLOWUP-D1, unchanged). FOLLOWUP-D2's "last turn only" now applies to **result material only**: the executed-plan summary and DATA-D1 value slice stay last-turn-bounded, while a turn carries the thread's accumulated **question text**, compacted oldest-first against a context budget derived from the route's model context window (conservative default where that window is not reliably known). See F04-D6; implemented in Slice 6.
- **HEADLINE-D1** — the plan-shape headline (`HeadlineClassifier`, F01 B1) is derived server-side from plan + result, not user-selected. It is the *core* of F04's "bounded reduction", but **not the whole of it**: `HeadlineResult` carries only a total count, one record, or the top ≤10 buckets (`csharp/Models/HeadlineResult.cs:14-23`, `HeadlineClassifier.cs:98-103`), which cannot express distribution shape. F04 sends the headline **plus a bounded distribution summary** (a handful of scalars — see Slice 2); both stay server-derived and DATA-D1-bounded.
- **MODEL-D1 / P02-D1** — two routes must both work: Claude Opus primary, gpt-5.5 alternate. Every prompt/parse path F04 adds must run through the existing route-neutral `LlmMessagesRequestBuilder`/`ClaudeService` machinery and be provider-agnostic (no Claude-only or GPT-only assumptions in prompt or response parsing).
- Logging is unrestricted on the app's own server (F01 GATE-3 resolution): F04's prompts and responses are logged like existing raw model material.

## Current-state evidence

Re-verify line numbers before editing; they are anchors, not contracts.

- **One model call per query.** `GenerateExecutionPlanAsync(userQuery, context, …)` (`csharp/Services/IClaudeService.cs:10`, implemented in `ClaudeService`) is the only LLM call in the query path; `QueryJobManager.ExecuteJobAsync` calls it once and then executes. Adding Narrate means a second call from the job path (Translate stays where it is).
- **Request/response machinery is route-neutral.** `LlmMessagesRequestBuilder.Build(effectiveModel, maxTokens, systemGuidance, userContent)` (`csharp/Services/LlmMessagesRequestBuilder.cs`) builds a provider-agnostic `LlmMessagesRequest` (system + one user message); sampling is applied only via an exact-match profile (P02-D1). F04's Narrate call reuses this builder; it does not open a second HTTP path.
- **The guess-transform.** `csharp/Services/QueryJobManager.cs:348-401`: aggregation computed (`:352`), then when `projectionColumns` set-equals `groupByFields` (`:355-359`) the code rebuilds `result.Data` from `grouped_counts` and nulls `aggregation` (`:389-393`). Delete (Slice 1 / D2).
- **Case-sensitive grouping.** `ComputeAggregation` (`csharp/Services/QueryJobManager.cs:468-494`) keys on `value?.ToString() ?? "(empty)"` (`:483`). Case-fold the key (Slice 1b / D4).
- **The prompt steers toward the transform.** `csharp/Configuration/prompt_template.txt:28` and the example at `:57` instruct the model to make projection columns match `group_by` "so the system returns unique values as data rows." Must be revised with the transform's removal (Slice 1 / D2) so the model stops emitting plans designed for a path that no longer exists.
- **Headline contract = the reduction.** `csharp/Models/HeadlineResult.cs` (`HeadlineKind` = `count | record | grouped | none`; `MaxHeadlineGroups = 10`) and `csharp/Services/HeadlineClassifier.cs` produce the bounded reduction server-side; returned on the async status result and read by the browser at `csharp/wwwroot/js/app.js:414`. The classifier's step-3 comment references the distinct-list transform; re-verify its `grouped`/`count` precedence still holds once the transform is gone (Slice 1).
- **Chat answer is code-templated.** `summariseJobForChat` (`csharp/wwwroot/js/app.js:1171`) → `resolveChatAnswer` (`:466`, `:1147-1152`); the pending bubble (`:1133`) is settled with the template. Slice 3 renders the model's real answer here.
- **Export is cache-backed and model-free.** `DownloadAsync` (`csharp/Controllers/QueryController.cs:1032`) reads `job.ResultsCacheKey` (`:1057`) and serializes (`GenerateFileContent`, `:1087`); no model call. `downloadResults` (`csharp/wwwroot/js/app.js:907`) hits `download-async/{jobId}`. Slice 4 locks this with a guard and adds `html`/`xlsx` alongside `csv`.
- **There is no artifact at job completion — the RAM cache is the only store.** A completed result is held in an `IMemoryCache` entry for 2h (`QueryJobManager.cs:344-346`, keyed `job_results_{jobId}`) and nothing else. The disk write at `QueryController.cs:1090` (`WriteAllBytes` to `QueryLogHelper.OutputRoot = E:\WWWOutput`) is the *only* result write in the codebase and it runs **inside `DownloadAsync`, after** that method has already read the cached result (`:1057-1062`) — the artifact is produced *by* a download, not before one. **Four readers depend on the cache today:** preview (`QueryController.cs:984-989`, 404s without it), single-record headline construction (`:1275-1282` → `HeadlineClassifier` falls back to `count` without it), download (`:1057-1062`), and F04's proposed cross-turn reuse. The in-RAM copy is what does not scale to 40k-row sets, so F04 must *create* a completion-time artifact and migrate all four readers before the cache can go (D5 / Slice 7).
- **Follow-up plumbing exists, but carries one turn.** Client sends only `previousJobId` (F01 C2); `QueryController.cs:855-869` resolves + ownership-checks it; `FollowUpContextBuilder.BuildFromPreviousTurn` + `FollowUpContextEnforcer.Compose` assemble the bounded last-turn context (prior question, plan summary, value slice — dropped in that reverse priority under the byte cap). F04 needs the accumulated **question text** across the thread, which this does not currently provide; extending it is the subject of D6 and Slice 6.
- **Validation rejects empty projection-filter values.** `PlanValidator.cs:229-233`. Fix for the negation-with-empty-value case (Slice 5 / D3).
- **Automated browser harness (F01 T1, TEST-D1).** Playwright.NET headless Chromium over static `csharp/wwwroot` with `/api` stubbed (`tests/AdQueryOrchestrator.Tests/Browser/`, `StaticSiteFixture`). Front-end slices are guarded by it, not manual notes.
- **Verification.** `pwsh -NoLogo -NoProfile -File scripts/verify.ps1` (locked-mode restore, format, build warnings-as-errors, full test suite, vuln audit). Every behavior-changing slice adds a red→green non-vacuous guard.

## Known risks

This design deliberately holds **no conversation-semantics rules in code**, so the earlier drafts' brittle edges (refine-vs-fresh classification, projection-only enforcement, delta grammars) no longer exist — there is nothing to misclassify. The remaining risks are inherent to interpretation, and the mitigation is the same one throughout: state the interpretation in the answer so a misread costs one turn.

- **Subject-scope misread.** The model may read a follow-up as directory-wide instead of subject-scoped (the "add China" failure), or vice-versa. Mitigation: subject-scoping guidance in the Translate prompt, plus the narrated interpretation making it visible. Guarded in Slice 6 by asserting the plan for a scoped follow-up carries the prior constraints, not a bare union.
- **Cumulative-intent decay over a long thread.** Re-planning from N prior questions may drop an early constraint. Mitigation: the conversation text is small and sent whole until D6's budget binds, and compaction then drops the oldest questions first; the narrated interpretation surfaces a dropped constraint immediately. A thread long enough to compact away a still-live early constraint remains a real failure mode, visible in the interpretation statement and correctable by restating the constraint.
- **Cost of re-planning + re-execution each turn.** Two model calls per turn (D1) plus a potential full re-traversal. Mitigated by the plan-step-equality artifact reuse (Slice 7); re-execution on genuinely changed intent is correct and intended.
- **Live-AD change between turns.** Re-executing a changed intent reads AD as it is now, so a count may legitimately differ from a prior turn. This is reality, not drift; it must not be hidden.
- **Narrate failure** must never fail the query — isolation is guarded in Slice 2.

## Open owner decisions

Each was presented in chat as a one-line plain-English ask, one at a time. **All six are ruled as of 2026-07-29** and recorded as `F04-D1`…`F04-D6` in `.agents/decisions.md` (canonical); the status lines below point there. No slice is blocked on a decision.

- **D1 — Two model calls per turn: Translate then Narrate.** The turn makes one call to translate words→structure and, after the engine runs and reduces the result, a second call so the model writes the answer from the *bounded reduction* (count / ≤10 buckets / one record — never rows, never the full set). This is not the rejected "reformat" pass; it is the only point the model sees real AD output, and it is what lets the app answer degenerate questions sensibly. Cost: one extra call's latency + tokens per turn; the bounded reduction (DATA-D1-sized) leaves on it. Status: **RULED — approved by the owner 2026-07-29 ("yes"). Recorded as F04-D1 in `.agents/decisions.md`.** Slice 2 is unblocked.
- **D2 — Delete the guess-transform; a grouped result's export is the value+count table.** Remove the data-mutating branch at `QueryJobManager.cs:354-401`, rewrite `prompt_template.txt:28,57`, **and** make a grouped result's exportable artifact the distribution table (value + count, count descending, first-class rows in every format) rather than a comment block under a row dump. The second half is not optional: deleting the transform alone turns the extensionAttribute1 export into 47,388 single-column rows with the counts relegated to CSV comments. Status: **RULED — approved by the owner 2026-07-29 ("y"). Recorded as F04-D2 in `.agents/decisions.md`.** Slice 1 is unblocked.
- **D3 — Fix the empty-value projection filter in F04.** Treat a negation operator with an empty value (`not_equals ""`) as "attribute is populated" instead of faulting the turn, keeping strict rejection for genuinely malformed filters. Status: **RULED — approved by the owner 2026-07-29 ("yes"). Recorded as F04-D3 in `.agents/decisions.md`.** Slice 5 is unblocked.
- **D4 — Case-insensitive grouping by default, with a model-settable case-sensitive mode.** Fold the group key case-insensitively so `Contractor`/`contractor`/`CONTRACTOR` count as one bucket, displayed under the most frequent original casing. A boolean on `AggregationDefinition` selects exact-case grouping, set by the model when the user asks for case variants (the owner's normalization-targets use case). The default view reports the distinct-spelling count per bucket so case inconsistency is discoverable without knowing the mode exists. Status: **RULED — approved by the owner 2026-07-29 ("default, yes … if we can have a case sensitive option without building any complex scaffolding" → "sure"). Recorded as F04-D4 in `.agents/decisions.md`.** Slice 1b is unblocked.
- **D5 — Completion-time disk artifact is the result of record; drop the mandatory 2h RAM cache of full results.** A canonical artifact is written atomically before the job is marked complete; preview, single-record headline construction, download, and cross-turn reuse all migrate to it *before* the cache is dropped, so a 40k-row thread does not pin memory. Status: **RULED — approved by the owner 2026-07-29 ("yes"). Recorded as F04-D5 in `.agents/decisions.md`.** Slice 7 is unblocked (it still depends on Slice 6 for sequencing).
- **D6 — Amend FOLLOWUP-D2 so a turn carries the thread's accumulated question text, compacted against a context budget.** F04 re-plans cumulative intent every turn, which requires the prior *questions* (small text), not just the immediately preceding one. The amendment carries **questions only** — never accumulated results, rows, or value slices, which stay last-turn-bounded under DATA-D1. The bound is a context budget sized to the job with **compaction before the route's model context window** (oldest questions first), not a fixed small cap; where the window is not reliably known, a conservative default applies. This requires deliberately widening the `QueryRequest.Context` `[StringLength(2000)]` transport guard together with `FollowUpOptions.ContextTransportCodeUnitLimit` and the validator ceiling. Status: **RULED — approved by the owner 2026-07-29 ("yes. chat context should be what is needed to do the job, with compaction before the model's context window size … if we don't reliably know that limit, set a safe default."). Recorded as F04-D6 in `.agents/decisions.md`.** Slice 6 is unblocked; all six F04 owner decisions are settled.

## Slices

Safest-first, one concern each, each its own commit with a provable red→green guard and `scripts/verify.ps1` before commit. A later slice does not start before the earlier one is committed. Depends-on decisions are named per slice. Slices 1, 1b, and 5 are self-contained bug fixes that stand on their own value even if the larger conversational model is deferred; they are sequenced first deliberately.

### Slice 1 — Delete the guess-transform and de-steer the prompt (D2)

Backend + prompt; pure removal of a data-mutating branch plus the guidance that steers toward it. No new model call.

- Remove the distinct-list transform at `csharp/Services/QueryJobManager.cs:354-401`: stop rebuilding `result.Data` from `grouped_counts` and stop nulling `aggregation`. A plan whose projection columns equal its `group_by` fields keeps its computed aggregation like any other grouped plan.
- Revise `prompt_template.txt:28` and the example at `:57`: drop "make projection columns match group_by so the system returns unique values as data rows"; instruct that unique-list / distinct-values / most-common queries use a normal `group_by` aggregation, presented as grouped counts.
- **Make a grouped result's export the distribution table (D2 second half).** When a settled result carries `grouped_counts`, the exportable artifact is value + count, ordered by count descending, as **first-class rows** in every format — not the comment block at `QueryController.cs:391-402` (CSV), the summary section at `:586-600` (HTML), or the separate sheet at `:691` (xlsx), which today sit beneath a dump of the underlying rows. Non-grouped results export their rows unchanged. This half is required, not optional: without it, deleting the transform turns the extensionAttribute1 export into 47,388 single-column rows with the counts in comments — strictly worse than what it replaces. If Slice 4's format work has not landed yet, apply this to the existing `csv` producer here and carry the same rule into `html`/`xlsx` when they are added.
- Re-verify `HeadlineClassifier.Classify` still yields `grouped` for these plans (it keys on surviving `grouped_counts`; the step-3 comment about the transform becomes stale — update the comment, adjust logic only if it actually relied on the cleared aggregation).
- **Guards:**
  - a backend test feeding the extensionAttribute1-shaped plan+result (projection column == single `group_by` field, near-unique values with a few large buckets) asserts the settled result **retains** grouped aggregation and does **not** expand to one-row-per-distinct-value. Prove it fails against the pre-removal tree (which yields N distinct rows and null aggregation);
  - the export of that same grouped result is the value+count table as data rows, not the underlying rows with counts in comments — prove it fails when the export falls back to `result.Data` plus the comment block.

### Slice 1b — Case-insensitive grouping by default, with a case-sensitive mode (D4)

Backend + prompt; isolated change to `ComputeAggregation` plus one plan field. Deliberately small: the two modes are the *same* grouping call with and without a comparer — do not build a second aggregation implementation.

- **Default fold.** In `csharp/Services/QueryJobManager.cs:476-488`, group the key case-insensitively; `:488`'s `.ToDictionary(g => g.Key, g => g.Count())` gains `StringComparer.OrdinalIgnoreCase`. Pick the displayed key deterministically: the most frequent original casing, ties broken ordinally. Keep `(empty)` for null/blank distinct from any real value.
- **Case-sensitive mode.** Add one boolean to `AggregationDefinition` (`csharp/Models/DirectoryQueryPlan.cs`, beside `group_by` / `count` / `include_level_metadata`), defaulting to case-insensitive. When set, the grouping call runs exactly as it does today. Teach the Translate prompt to set it when the user asks for case variants, exact-case counts, or normalization targets — this is a natural-language capability, not a config knob or UI control.
- **Multi-spelling reporting.** In the default mode, carry the count of distinct original spellings each folded bucket absorbed, so an answer can read "Contractor: 6,112 (3 spellings)". Computed from the same pass; no extra directory work. This is what makes case inconsistency discoverable to a user who does not know the case-sensitive mode exists.
- The singleton / blank / distinct-bucket counts that Slice 2's distribution summary needs are computed over these folded buckets.
- **Guards:**
  - grouping `["Contractor","contractor","CONTRACTOR", blank]` yields one `Contractor` bucket of 3 with a stable display key plus one `(empty)` bucket, and reports 3 spellings — prove it fails against the case-sensitive keying;
  - the same input with the case-sensitive flag set yields three separate buckets — prove it fails if the flag is ignored (the mode would silently not exist).

### Slice 5 — Empty-value projection filter no longer faults the turn (D3)

Backend-only; sequenced early because it is the exact crash the Sanjay follow-up hit and is independent of the conversational rework.

- In `PlanValidator.cs:229-233`, when a projection filter uses a negation operator with an empty/whitespace value (`not_equals ""`, `not_contains ""`, `not_starts_with ""`, `not_ends_with ""`), interpret it as "attribute is populated" rather than erroring. Keep the strict `Projection filter value is required` rejection for non-negation operators and genuinely malformed filters (missing attribute, unknown row step, non-allow-listed attribute, unsupported operator). Apply the same rule at the two sibling operator checks (`:277`, `:300`) so a filter's acceptance does not depend on where in the plan it sits.
- **Generalize the existing helper rather than adding a parallel one.** `DirectoryPlanExecutor.AllowsEmptyFilterValue` (`csharp/Services/DirectoryPlanExecutor.cs:1419-1434`) already encodes this concept, hardcoded to `AccountExpirationDate` with `equals`/`not_equals`. Widen it to any attribute under a negation operator and share one reading between validator and executor — a plan the validator accepts must be one the executor evaluates identically. Note the pre-existing `equals ""` case for `AccountExpirationDate` is a *positive* operator and stays as-is; do not widen positive operators.
- **Guard:** a validator/executor test feeding `title not_equals ""` over a set with some blank titles asserts the turn completes and filters to populated titles, not `Projection filter value is required`. Prove it fails against the current strict path. A second assertion keeps the strictness that remains: a non-negation operator with an empty value is still rejected.

### Slice 2 — Narrate: the model writes the answer from the bounded reduction (D1)

Backend-only; adds the second model call and its bounded input. No UI.

- After execution + `HeadlineClassifier` in the async job path, add a Narrate step: build a **bounded reduction context** from {user question, plan `description`, the `HeadlineResult`, the distribution summary below} and call the model a second time through the existing `LlmMessagesRequestBuilder`/`ClaudeService` machinery (route-neutral; primary route, same alternate-retry behavior as Translate is out of scope here). The model returns a short natural-language answer string.
- **Distribution summary (required — the headline alone is insufficient).** For a grouped plan, compute a deterministic summary of a handful of scalars: total rows, **distinct-bucket count**, **singleton-bucket count** (buckets of exactly 1), **blank count** (the `(empty)` bucket), and the top-N buckets case-folded per Slice 1b. Without these the model cannot tell a near-unique distribution from a concentrated one — `HeadlineResult` discards all but the ten largest buckets, so the extensionAttribute1 answer the Architecture section promises ("26,612 of ~27k values appear once") is not derivable, and the model would have to either fabricate it or present `Contractor` as the most common value. A fixed handful of integers adds no AD values beyond the buckets already permitted.
- **Bounding is authoritative and server-side.** The reduction is the already-bounded `HeadlineResult` (≤10 groups, one record, or a scalar) plus the scalar distribution summary, under a byte cap mirroring the `FollowUpContextEnforcer` mechanic (add `Answer:MaxReductionBytes` only if a distinct cap is needed; otherwise reuse the follow-up enforcer). Never send rows; never send the full set; never exceed the DATA-D1 ceiling.
- Add a **provider-agnostic** Narrate prompt template (new file under `csharp/Configuration/`, loaded like `prompt_template.txt`): answer the question directly in one–three sentences from the provided reduction; state plainly when there is no meaningful answer (e.g. the near-unique/mostly-blank case); never invent data beyond the reduction; no markdown tables (the table is rendered separately).
- Store the answer on the job (`QueryJob`) and expose it on the async status DTO alongside `headline`. Ship only the model-authored answer string — never raw rows or the raw model response — to the browser.
- **Failure isolation:** if Narrate fails, times out, or the route errors, the job still completes with headline + table + export; the answer field is absent. Narrate is additive and never a new failure mode for the query.
- **Guard:** a backend test with a stubbed model asserting (1) the Narrate input is the bounded reduction and byte-capped — never rows, never the full set; (2) a near-unique grouped result (the extensionAttribute1 shape) yields a Narrate input carrying the distinct/singleton/blank counts within the cap — prove it fails when the summary is omitted, since the input can then no longer distinguish near-unique from concentrated; and (3) a Narrate failure still yields a completed job with headline + rows (answer absent). Prove each fails when the bound / the summary / the isolation is removed.

### Slice 3 — Render the answer, answer-first (UI)

UI presentation; replaces the code-templated chat answer with the model's real answer and leads the main window with it. **Depends on Slice 2.**

- Replace `summariseJobForChat` (`csharp/wwwroot/js/app.js:1171`): render the model's answer string from the status DTO when present; fall back to the existing F01 headline template only when the answer is absent (Narrate failure / older job).
- Lead the main window with the answer text above the F01 headline block; headline, table, and export remain beneath as the authoritative detail. F01 Design contract tokens only (no new palette; FONT-D1 unchanged).
- **Guard:** the T1 harness stubs a completed job carrying a model answer and asserts the chat bubble and main window render that answer (not the "See the result panel" template), and that a job with no answer falls back to the F01 headline. Prove it fails when the answer-render branch is removed.

### Slice 4 — Export: permanent, unobtrusive, multi-format, model-free (invariant lock)

UI + backend + an invariant guard. **No decision dependency for the guard; format additions are additive.**

- Present export as a permanent, unobtrusive affordance on every response that is a **meaningful exportable artifact** (a set/table) — not on one-line answers or single-record results (owner rule 2026-07-28). Restyle the F01 `.ops` pill row to secondary; never the headline. What the artifact *is* depends on the answer's shape (F04-D2): a grouped answer exports the value+count distribution table; a list answer exports its rows; a scalar count or single record exports nothing.
- Offer **csv / html / xlsx**. `csv` exists (`GenerateFileContent` "csv" branch); add `html` and `xlsx` producers alongside it, reading the same settled artifact. (xlsx needs a library decision — see the note below; if undecided at implementation time, ship csv+html and defer xlsx to a follow-on commit rather than block the slice.)
- **Guard (invariant lock):** a backend test asserting `DownloadAsync` produces its file from the settled artifact with **zero model calls** (inject a model service that fails the test if invoked). Prove it fails if download is ever routed through a model/plan path. Encodes the owner's binding constraint: export must never risk a different result.

> **xlsx library note (implementation decision, not an owner gate):** pick a maintained, permissively-licensed, zero-native-dependency writer (e.g. a pure-managed OOXML library) that passes the `verify.ps1` vulnerability-audit gate; if none qualifies cleanly, ship csv+html and raise xlsx as a separate decision. Do not add a native/interop Excel dependency.

### Slice 6 — Conversation-scoped re-planning (the conversational core)

Backend-first. **Depends on Slices 1, 2, and decision D6 (ruled).** MAY be split into 6a (accumulated-question context plumbing + budget/compaction), 6b (transport-guard widening), and 6c (subject-scoping prompt guidance + interpretation statement) if that keeps each commit's guard focused. 6b MUST land before or with 6a — the current transport guard rejects the widened context outright.

- **Thread context.** Extend the follow-up path so a turn carries the thread's accumulated **question text**, oldest to newest (questions only — never accumulated rows or value slices; the DATA-D1 value slice and executed-plan summary stay last-turn-bounded). Reuse the existing resolution and ownership check (`QueryController.cs:855-869`) and the `FollowUpContextEnforcer.Compose`/`EnforceStored` enforcement point, extended to assemble a question list rather than a single prior question.
- **Budget and compaction (D6).** The bound is a context budget derived from the effective route's model context window, with **compaction before that window** rather than truncation at a fixed small cap. Compaction drops oldest questions first; the current turn's question, the last turn's plan summary, and the last turn's value slice are never dropped. Where the route's window is not reliably known — it is not discoverable from configuration today, and the two MODEL-D1 routes differ — apply a conservative default budget. **`LlmProviderOptions.MaxTokens` (`csharp/Configuration/LlmProviderOptions.cs:21`, `"4000"`) is the output cap and must not be used as the budget source.** Summarizing dropped questions instead of discarding them is permitted but not required; discarding is the floor.
- **Transport-guard widening (D6, load-bearing).** `FollowUpOptionsValidator.cs:20-28` hard-rejects `MaxContextBytes` above `FollowUpOptions.ContextTransportCodeUnitLimit` (`2000`), pointing at the `QueryRequest.Context` `[StringLength(2000)]` attribute as the thing that must move first. Widen the `[StringLength]`, the constant, the validator ceiling, and `FollowUpOptionsReconciliationTests` **together in one commit**; update the advisory client-side copy served at `QueryController.cs:243-250`. The server bound stays authoritative — the client is never trusted to have truncated (FOLLOWUP-D1, DATA-D1).
- **No turn classification.** There is no `turnKind`, no frozen steps, no projection-only constraint. Every turn the Translate call emits **one complete plan** for the cumulative intent. Code executes whatever plan comes back, exactly as it does for a single question today.
- **Subject-scoping guidance** in the Translate prompt: a follow-up is interpreted within the conversation's established subject — narrowing or extending *within* it — and never silently escapes to a directory-wide set; leaving the subject requires an explicit statement from the user ("forget Sanjay…"). Include the "add the users in China" case as a worked example of the scoped reading.
- **Interpretation statement.** The Narrate prompt (Slice 2) is extended so the answer states the interpretation used ("Sanjay's reports with titles who work in China: 12"), making a misread visible and one-turn correctable.
- **Guards (each red→green, non-vacuous):**
  - a three-turn thread (`under Sanjay` → `only with titles` → `add the users in China`) produces a final plan carrying **all three** constraints conjunctively — prove it fails if the accumulated-question context is not supplied (the third turn loses the title constraint) and fails if the plan is a bare directory-wide union for China;
  - a subject-replacing turn ("forget Sanjay, everyone in China") produces a plan **without** the Sanjay constraints — prove it fails if prior constraints are unconditionally carried;
  - a thread whose accumulated questions exceed the budget compacts **oldest-first** and stays within it, while retaining the current question and the last-turn plan summary/value slice — prove it fails with compaction disabled (the context exceeds the budget) and fails if compaction can evict the current turn;
  - the transport attribute, `ContextTransportCodeUnitLimit`, and the validator ceiling stay reconciled at the widened value, and a configured `MaxContextBytes` above it is still rejected — prove it fails if any one of the three is moved alone;
  - the context carries **no** accumulated result rows or value slices beyond the last-turn DATA-D1 bound — prove it fails if results accumulate.

### Slice 7 — Completion-time artifact of record; whole-plan reuse; unpin full results from RAM (D5)

Backend-only; **depends on Slice 6.** Sequenced last so the conversational path is proven before the storage model changes underneath it. **Order within the slice is load-bearing: create the artifact, migrate every reader, then drop the cache.** Removing the cache first breaks preview and single-record headlines (see the current-state evidence bullet on result persistence).

- **Create the artifact.** Persist a canonical full-result artifact **atomically, before the job is marked `Completed`** (write to a temp path in the user's `QueryLogHelper` directory, then move into place), and record its path on `QueryJob`. Today no artifact exists until someone downloads (`QueryController.cs:1090`), so this is new work, not a re-point.
- **Migrate all four readers** to the artifact: preview (`QueryController.cs:984-992`), single-record headline construction (`:1275-1282`), download (`:1057-1062`, Slice 4), and cross-turn reuse. Preview and headline need only the first N rows, so they stream/seek rather than materializing the set.
- **Only then** drop the mandatory 2h full-result `IMemoryCache` entry (`QueryJobManager.cs:344-346`), or reduce it to an optional bounded fast path that never holds a large set resident.
- **Whole-plan artifact reuse (the only optimization):** reuse a prior turn's artifact only when the **complete serialized plan** — steps *and* projection, filters, aggregation, and result limit — is byte-identical to the new turn's. Exact equality only; never fuzzy or semantic. Membership-step equality is **not** sufficient: `DirectoryPlanExecutor.Project` (`csharp/Services/DirectoryPlanExecutor.cs:219,572-604`) stores rows already filtered and reduced to that turn's columns, so "everyone under Sanjay" → "only those with titles" would otherwise reuse an artifact with no `Title` column and unfiltered rows. If membership-level reuse is later wanted, it requires persisting a separate membership-identity representation (resolved record identities, not projected rows) and deterministically re-applying projection, filters, aggregation, and limit every turn — that variant is permitted but not required.
- **Guards:**
  - a freshly completed job with the results cache absent/evicted: **preview, single-record headline (`record` kind preserved, not downgraded to `count`), download, and cross-turn reuse all still succeed** — prove it fails when any single reader is left on the cache;
  - a large (e.g. 40k-row) result exports from the artifact with no full-set in-RAM copy — prove it fails if export still requires the cached result;
  - two consecutive turns with an identical complete plan trigger exactly **one** directory traversal — prove it fails if reuse is removed;
  - two turns with identical membership steps but a **differing projection** both return their own correct shape (two traversals, or one traversal with the projection re-applied) — prove it fails if reuse keys on membership steps alone.

## Non-goals (F04)

- No accumulated **results** transcript to the model. The thread carries accumulated *question text* only (D6); result values stay last-turn-bounded under DATA-D1/FOLLOWUP-D1.
- No full result set, artifact, or download sent to the model — ever. Every model path is DATA-D1-bounded.
- No change to the Translate route policy or the retry-with-alternate-model feature; Narrate uses the same route-neutral machinery but its own alternate-retry behavior is out of scope.
- No agentic tool-calling loop; the model never live-queries AD in a loop (owner-directed: not agentic).
- No conversation-semantics machinery in code: no turn-kind classifier, no frozen membership, no delta/patch grammar. These were explored and rejected as brittle; do not reintroduce them.
- No new UI framework, web font, or palette (F01 Design contract + FONT-D1 govern).
- No token-by-token answer streaming (possible later enhancement; out of scope).
- No native/interop Excel dependency for xlsx.

## Verification

`pwsh -NoLogo -NoProfile -File scripts/verify.ps1` before every commit. Each behavior-changing slice ships a focused regression guard proven to fail when its targeted behavior is disabled (repo Verification rule). Front-end slices (3, 4-UI) are guarded by the F01 T1 Playwright harness, not manual smoke notes. Both model routes (MODEL-D1) must be exercisable by the Narrate/Translate paths without provider-specific branching.
