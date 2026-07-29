# F04 — Genuine Conversational Answers

**Status: Draft (2026-07-29). Not approved; no code lands until the owner flips this status line to `Approved`.** Open owner decisions are listed under "Open owner decisions"; each must be ruled before the slice that depends on it starts. This draft replaces an earlier F04 draft that bolted a synthesis pass onto the existing pipeline; the owner rejected that shape ("you appear to be trying to salvage something of the old app … that will not be approved") and directed a from-scratch model. This is that model.

This plan is self-contained. A cold, less-capable agent can implement it without the originating conversation. It builds on F01 (`.agents/plans/F01-conversational-query.md`, Implemented) and F02 (mockup UI, Done); read the F01 Design contract for the chat/main-window tokens — F04 does not restate them.

## Problem

The app was redirected to answer routine AD questions conversationally (F01 entry point, 2026-07-27), but it still behaves as a bulk-export tool. The root cause is architectural: **the model only ever produces a query plan. It never sees the results and never writes an answer.** Everything the user reads back is assembled by code from a fixed template. The app cannot *talk about* Active Directory — it can only translate one sentence into one spreadsheet.

Observed evidence (deployed logs read directly 2026-07-28/29, per-job `.log` and `.csv` under `E:\WWWOutput\mcoelho\`, framework logs under `D:\inetpub\adquery\logs`):

1. **"How many" produces a file, not a number.** Job `bc623ae0`, query *"how many users roll up to Sanjay Abhyankar"*: the model's own plan `description` reads *"Count all users … rolling up to Sanjay"* — it understood a count was asked. But the emitted plan carried row columns (Name/Title/Department) and **no aggregation**, so the engine produced a **77-row CSV** (`Records: 77`). The answer — 77 — exists in the log line and is never spoken; the user is handed a spreadsheet to count themselves. There is exactly one model call per query, `GenerateExecutionPlanAsync` (`csharp/Services/IClaudeService.cs:10`), and it emits only the plan; nothing consults the model after execution, so the chat bubble is filled by the code template `summariseJobForChat` (`csharp/wwwroot/js/app.js:1171`), e.g. `"42 matches. See the result panel."`.

2. **A guess-based transform silently mutates results.** Job `eed12d05` (Opus) / retry `173508149` (gpt-5.5), query *"what's the most common value in extensionAttribute1"*: both models produced a **correct** plan — search all Users, `aggregation: { group_by: [extensionAttribute1], count: true }`. The engine then discarded that aggregation and expanded to **26,625 data rows** via the heuristic at `csharp/Services/QueryJobManager.cs:354-400` ("if projection columns exactly match `group_by`, the user wants unique values as data"; `aggregation = null` at `:393`). Parsing the emitted CSV directly: sum of all bucket counts = 47,388 (every user); the real distribution is `(empty)` 7,150 · `Contractor` 6,100 · `Service` 4,322 · `CONFROOM` 1,478 · `other` 959 · `Other` 704 · … with **26,612 buckets of count 1** (near-unique numeric values). So the "most common value" question has no meaningful single answer, and the app answered it with a 26k-row dump. Two distinct defects surfaced here: the guess-transform (2) and case-sensitive grouping (see 4).

3. **Follow-ups crash on a technicality.** Job `3b3223c7`, refinement *"only users with titles"* following the Sanjay roll-up, failed with `Projection filter value is required`. The model correctly refined the prior query (added a `title not_equals ""` projection filter) but `PlanValidator.cs:229-233` rejects any empty filter `value`. A conversational refinement faulted the whole turn on a validator edge case.

4. **Grouping is case-sensitive.** `ComputeAggregation` keys buckets on `value?.ToString()` (`csharp/Services/QueryJobManager.cs:483`), so `Contractor` (6,100) / `contractor` (9) / `CONTRACTOR` (3) are three buckets, as are `other`/`Other`/`OTHER` and `CONFROOM`/`confroom`. Exact counts are individually correct but fragment what a human means by "one value," corrupting any "most common" answer.

The F01/F02 work delivered the chat *surface*, the plan-shape headline (`HeadlineClassifier`), and the follow-up plumbing (`previousJobId`, byte-capped last-turn context via `FollowUpContextEnforcer.Compose`). What is missing is the assistant itself.

## Architecture (the model the owner directed)

The app is a **natural-language conversation wrapped around a deterministic AD query engine.** The LLM is only ever two things, never a third:

- a **translator** — your words → a structured query (this is why the app exists; only an LLM parses "just the ones in Seattle named Jane who started in December" into field/operator/value triples), and
- a **narrator** — a *reduced* result → a sentence.

The LLM **never executes** a query, **never filters rows**, and **never holds the result set.** All row-level work — search, expansion, filtering, aggregation, counting — is deterministic code. This split is what makes answers both smart (the model reasons over what AD actually returned) and safe (the full set never leaves the server, and the model cannot re-decide membership on a whim).

### A plan has two layers with different lifetimes

- `steps[]` = **membership** — search, `expand_reports`, `expand_members`, `lookup`. This decides *who is in the set*.
- `projection` = **shaping** — columns, projection filters, aggregation. This decides *how the set is presented/narrowed*.

### A turn has three phases

1. **Translate.** The model reads the thread state + the user message and emits structure.
   - A **fresh question** → a full plan (`steps` + `projection`).
   - A **refinement** of an existing thread → **projection changes only** (add/remove a filter, change aggregation, sort). The model is handed the frozen thread's plan as context and is constrained to reshaping — it does **not** re-emit `steps[]`.
2. **Execute + reduce.** The engine runs the plan; the **full result** is persisted as the on-disk job artifact (already happens — `OutputFile` under `QueryLogHelper.OutputRoot`); a **bounded reduction** is computed (the existing B1 `HeadlineResult`: a scalar count, ≤10 grouped buckets, or one record).
3. **Narrate.** The model reads {user question + the bounded reduction} and writes the answer. It reasons over *real reduced data*, so extensionAttribute1 yields *"near-unique — 26,612 of ~27k values appear once and 7,150 are blank, so there's no meaningful most-common value; the largest real bucket is Contractor at 6,100"* instead of a dump.

### Threads freeze the membership, not rows-in-RAM

A thread's identity is its **frozen `steps[]`** plus the on-disk artifact of the last result. A refinement:

- reuses the frozen `steps[]` **verbatim** — the model cannot re-derive membership, so "the 77" stay exactly 77 across "only the ones with titles"; and
- applies its projection change over the existing result. If every needed attribute is already in the artifact → filter/aggregate the artifact in code (no AD hit). If the refinement references an attribute the artifact does not carry (e.g. `city` for "only the ones in Seattle") → the engine **re-runs the frozen `steps[]` with that one attribute added** — a single directory search over the same membership, never a per-DN fan-out and never a model re-derivation.

This is the crux the owner drove out: freezing *rows* alone fails (a missing column forces a re-query, which under model variation drifts membership); freezing the *plan's membership steps* guarantees the set is stable while still allowing attribute enrichment via one deterministic re-run.

### Why two model calls per turn is the right shape (not the rejected one)

The earlier draft's second call *reformatted* the first call's output — the owner correctly rejected that as "the wrong shape." This architecture's two calls are **different jobs**: Translate (words → structure) and Narrate (reduced data → words). Narrate is the *only* point at which the model sees what AD returned; without it the model must commit to answer wording before the data exists and cannot react when the data is degenerate (the extensionAttribute1 case). The value that crosses the wire on Narrate is the **bounded reduction** (a count, ≤10 buckets, or one record), never rows and never the full set — identical in size and sensitivity to what DATA-D1 already permits for follow-up context. See D1.

## Relevant settled decisions (canonical in `.agents/decisions.md`; restated as constraints)

- **DATA-D1 (amended 2026-07-27)** — bounded AD values may be sent to any configured model route (primary or alternate); only a minimal slice (preview slice or aggregation summary), never the full result set, never 10k rows; full downloads stay server-side. **Both F04 model phases are value-bounded by DATA-D1**; Narrate sends only the B1-bounded reduction.
- **FOLLOWUP-D1 / FOLLOWUP-D2** — follow-up context is byte-capped by `FollowUp:MaxContextBytes` and carries the last turn only; no accumulated transcript reaches the model or is retained server-side. F04's thread state is exactly one frozen prior turn, consistent with this.
- **HEADLINE-D1** — the plan-shape headline (`HeadlineClassifier`, F01 B1) is derived server-side from plan + result, not user-selected. It **is** F04's "bounded reduction"; F04 reuses it as the Narrate input and layers answer text on top.
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
- **Full result persistence.** The completed result is written to disk (`OutputFile`, `QueryLogHelper.OutputRoot = E:\WWWOutput`, `QueryController.cs:1089-1104`) **and** held in an `IMemoryCache` entry for 2h (`QueryJobManager.cs:344-346`, keyed `job_results_{jobId}`). The in-RAM copy is what does not scale to 40k-row sets; F04 leans on the disk artifact as the frozen set of record and treats the cache as an optional fast path (see D5 / Slice 6).
- **Follow-up plumbing exists.** Client sends only `previousJobId` (F01 C2); `QueryController.cs:855-869` resolves + ownership-checks it; `FollowUpContextBuilder.BuildFromPreviousTurn` + `FollowUpContextEnforcer.Compose` assemble the bounded last-turn context. F04's refinement path builds on this — it must additionally carry the frozen `steps[]` forward, which today's builder does not (it sends a plan *summary* string, not the executable steps).
- **Validation rejects empty projection-filter values.** `PlanValidator.cs:229-233`. Fix for the negation-with-empty-value case (Slice 5 / D3).
- **Automated browser harness (F01 T1, TEST-D1).** Playwright.NET headless Chromium over static `csharp/wwwroot` with `/api` stubbed (`tests/AdQueryOrchestrator.Tests/Browser/`, `StaticSiteFixture`). Front-end slices are guarded by it, not manual notes.
- **Verification.** `pwsh -NoLogo -NoProfile -File scripts/verify.ps1` (locked-mode restore, format, build warnings-as-errors, full test suite, vuln audit). Every behavior-changing slice adds a red→green non-vacuous guard.

## Known brittle edges (must carry guards, not just prompt hope)

Named so the plan builds defenses rather than discovering them in production:

- **Refine-vs-fresh misclassification.** If "show me the *other* VP's org" is treated as a refinement, projection changes apply to the wrong frozen membership. Mitigation: the model emits an explicit turn-kind signal; code enforces the consequence (a fresh plan resets the thread and its frozen steps). A refinement that arrives with no active thread is treated as fresh. Guarded in Slice 6.
- **Refinement smuggling membership change.** The "projection-only" constraint on refinements cannot rest on prompt discipline. Code must **reject or ignore** any `steps[]` a refinement emits and reuse the frozen steps; a refinement can only change `projection`. Guarded in Slice 6.
- **Fuzzy attribute mapping + enrichment re-run drift.** "started in December" → `whenCreated`; a missing-attribute refinement re-runs the frozen steps, opening a live-AD delta window between the two runs. This is genuine AD change, not model drift, and is acceptable; it must be surfaced (the answer is over the re-run set), not hidden. Guarded in Slice 6.
- **Two calls per turn** — accepted cost (D1); Narrate is isolated so its failure never fails the query (Slice 2).

## Open owner decisions

Each is presented in chat as a one-line plain-English ask, one at a time; the ruling is recorded here and in `.agents/decisions.md` before the dependent slice starts. No slice depending on an unruled decision begins.

- **D1 — Two model calls per turn: Translate then Narrate.** The turn makes one call to translate words→structure and, after the engine runs and reduces the result, a second call so the model writes the answer from the *bounded reduction* (count / ≤10 buckets / one record — never rows, never the full set). This is not the rejected "reformat" pass; it is the only point the model sees real AD output, and it is what lets the app answer degenerate questions sensibly. Cost: one extra call's latency + tokens per turn; the bounded reduction (DATA-D1-sized) leaves on it. Status: **pending owner y/n. Recommended: yes** — without it the app stays a template engine.
- **D2 — Delete the guess-transform and revise the prompt.** Remove the data-mutating branch at `QueryJobManager.cs:354-401` and rewrite `prompt_template.txt:28,57` so "unique list / distinct values / most common" queries use a normal `group_by` aggregation, presented as the grouped reduction. Consequence: such queries return grouped counts, never a bare distinct-row dump. Status: **pending owner y/n. Recommended: yes.**
- **D3 — Fix the empty-value projection filter in F04.** Treat a negation operator with an empty value (`not_equals ""`) as "attribute is populated" (or drop the degenerate filter) instead of faulting the turn, keeping strict rejection for genuinely malformed filters. Status: **pending owner y/n. Recommended: yes** (it is the exact crash the Sanjay follow-up hit).
- **D4 — Case-insensitive grouping.** Fold the aggregation group key to a case-insensitive form so `Contractor`/`contractor`/`CONTRACTOR` count as one bucket. Consequence: "most common value" reflects human intent; a display form (e.g. most-frequent original casing) is chosen deterministically. Status: **pending owner y/n. Recommended: yes.**
- **D5 — On-disk artifact is the frozen set of record; drop the mandatory 2h RAM cache of full results.** Refinement and export read the disk artifact (streamed), not an in-RAM copy, so a 40k-row thread does not pin memory. The memory cache becomes an optional bounded fast path or is removed. Status: **pending owner y/n. Recommended: yes** (the owner raised the 40k-in-RAM objection directly).

## Slices

Safest-first, one concern each, each its own commit with a provable red→green guard and `scripts/verify.ps1` before commit. A later slice does not start before the earlier one is committed. Depends-on decisions are named per slice. Slices 1, 1b, and 5 are self-contained bug fixes that stand on their own value even if the larger conversational model is deferred; they are sequenced first deliberately.

### Slice 1 — Delete the guess-transform and de-steer the prompt (D2)

Backend + prompt; pure removal of a data-mutating branch plus the guidance that steers toward it. No new model call.

- Remove the distinct-list transform at `csharp/Services/QueryJobManager.cs:354-401`: stop rebuilding `result.Data` from `grouped_counts` and stop nulling `aggregation`. A plan whose projection columns equal its `group_by` fields keeps its computed aggregation like any other grouped plan.
- Revise `prompt_template.txt:28` and the example at `:57`: drop "make projection columns match group_by so the system returns unique values as data rows"; instruct that unique-list / distinct-values / most-common queries use a normal `group_by` aggregation, presented as grouped counts.
- Re-verify `HeadlineClassifier.Classify` still yields `grouped` for these plans (it keys on surviving `grouped_counts`; the step-3 comment about the transform becomes stale — update the comment, adjust logic only if it actually relied on the cleared aggregation).
- **Guard:** a backend test feeding the extensionAttribute1-shaped plan+result (projection column == single `group_by` field, near-unique values with a few large buckets) asserts the settled result **retains** grouped aggregation and does **not** expand to one-row-per-distinct-value. Prove it fails against the pre-removal tree (which yields N distinct rows and null aggregation).

### Slice 1b — Case-insensitive grouping (D4)

Backend-only; isolated fix to `ComputeAggregation`.

- Fold the group key case-insensitively in `csharp/Services/QueryJobManager.cs:476-488`; pick the displayed key form deterministically (e.g. the most frequent original casing, ties broken by ordinal). Keep `(empty)` for null/blank distinct from any real value.
- **Guard:** a test grouping `["Contractor","contractor","CONTRACTOR","(blank)"]` asserts one `Contractor` bucket of 3 plus one `(empty)` bucket, with a stable display key. Prove it fails against the case-sensitive keying.

### Slice 5 — Empty-value projection filter no longer faults the turn (D3)

Backend-only; sequenced early because it is the exact crash the Sanjay follow-up hit and is independent of the conversational rework.

- In `PlanValidator.cs:229-233`, when a projection filter uses a negation operator with an empty/whitespace value (`not_equals ""`, `not_contains ""`), interpret it as "attribute is populated" (or drop the degenerate filter) rather than erroring. Keep the strict `Projection filter value is required` rejection for non-negation operators and genuinely malformed filters.
- **Guard:** a validator/executor test feeding `title not_equals ""` over a set with some blank titles asserts the turn completes and filters to populated titles, not `Projection filter value is required`. Prove it fails against the current strict path.

### Slice 2 — Narrate: the model writes the answer from the bounded reduction (D1)

Backend-only; adds the second model call and its bounded input. No UI.

- After execution + `HeadlineClassifier` in the async job path, add a Narrate step: build a **bounded reduction context** from {user question, plan `description`, the `HeadlineResult`} and call the model a second time through the existing `LlmMessagesRequestBuilder`/`ClaudeService` machinery (route-neutral; primary route, same alternate-retry behavior as Translate is out of scope here). The model returns a short natural-language answer string.
- **Bounding is authoritative and server-side.** The reduction is the already-bounded `HeadlineResult` (≤10 groups, one record, or a scalar) plus a byte cap mirroring the `FollowUpContextEnforcer` mechanic (add `Answer:MaxReductionBytes` only if a distinct cap is needed; otherwise reuse the follow-up enforcer). Never send rows; never send the full set; never exceed the DATA-D1 ceiling.
- Add a **provider-agnostic** Narrate prompt template (new file under `csharp/Configuration/`, loaded like `prompt_template.txt`): answer the question directly in one–three sentences from the provided reduction; state plainly when there is no meaningful answer (e.g. the near-unique/mostly-blank case); never invent data beyond the reduction; no markdown tables (the table is rendered separately).
- Store the answer on the job (`QueryJob`) and expose it on the async status DTO alongside `headline`. Ship only the model-authored answer string — never raw rows or the raw model response — to the browser.
- **Failure isolation:** if Narrate fails, times out, or the route errors, the job still completes with headline + table + export; the answer field is absent. Narrate is additive and never a new failure mode for the query.
- **Guard:** a backend test with a stubbed model asserting (1) the Narrate input is the bounded reduction and byte-capped — never rows, never the full set; and (2) a Narrate failure still yields a completed job with headline + rows (answer absent). Prove each fails when the bound / the isolation is removed.

### Slice 3 — Render the answer, answer-first (UI)

UI presentation; replaces the code-templated chat answer with the model's real answer and leads the main window with it. **Depends on Slice 2.**

- Replace `summariseJobForChat` (`csharp/wwwroot/js/app.js:1171`): render the model's answer string from the status DTO when present; fall back to the existing F01 headline template only when the answer is absent (Narrate failure / older job).
- Lead the main window with the answer text above the F01 headline block; headline, table, and export remain beneath as the authoritative detail. F01 Design contract tokens only (no new palette; FONT-D1 unchanged).
- **Guard:** the T1 harness stubs a completed job carrying a model answer and asserts the chat bubble and main window render that answer (not the "See the result panel" template), and that a job with no answer falls back to the F01 headline. Prove it fails when the answer-render branch is removed.

### Slice 4 — Export: permanent, unobtrusive, multi-format, model-free (invariant lock)

UI + backend + an invariant guard. **No decision dependency for the guard; format additions are additive.**

- Present export as a permanent, unobtrusive affordance on every response that is a **meaningful exportable artifact** (a set/table) — not on one-line answers or single-record results (owner rule 2026-07-28). Restyle the F01 `.ops` pill row to secondary; never the headline.
- Offer **csv / html / xlsx**. `csv` exists (`GenerateFileContent` "csv" branch); add `html` and `xlsx` producers alongside it, reading the same settled artifact. (xlsx needs a library decision — see the note below; if undecided at implementation time, ship csv+html and defer xlsx to a follow-on commit rather than block the slice.)
- **Guard (invariant lock):** a backend test asserting `DownloadAsync` produces its file from the settled artifact with **zero model calls** (inject a model service that fails the test if invoked). Prove it fails if download is ever routed through a model/plan path. Encodes the owner's binding constraint: export must never risk a different result.

> **xlsx library note (implementation decision, not an owner gate):** pick a maintained, permissively-licensed, zero-native-dependency writer (e.g. a pure-managed OOXML library) that passes the `verify.ps1` vulnerability-audit gate; if none qualifies cleanly, ship csv+html and raise xlsx as a separate decision. Do not add a native/interop Excel dependency.

### Slice 6 — Threaded refinement over frozen membership

Backend-first; the conversational core. **Depends on Slices 1, 2. This is the largest slice and MAY be split** into 6a (turn-kind classification + frozen-steps carry-forward + membership-lock guard) and 6b (attribute-enrichment re-run) if that keeps each commit's guard focused.

- **Thread state.** Extend the follow-up path so a refinement carries forward the prior turn's **executable frozen `steps[]`** (not just today's plan-summary string in `FollowUpContextBuilder`). Source of truth for the frozen set is the prior job resolved and ownership-checked exactly as F01 C2 does (`QueryController.cs:855-869`); no new client trust.
- **Turn-kind.** The Translate call emits an explicit `turnKind` (`fresh` | `refine`). Code enforces the consequence: `fresh` builds a new plan and resets the thread's frozen steps; `refine` reuses the frozen steps verbatim and accepts only `projection` changes. A `refine` with no active thread is treated as `fresh`.
- **Membership lock (guard-critical).** Code **ignores/rejects** any `steps[]` a `refine` turn emits; membership comes only from the frozen steps. A refine turn can change projection filters, aggregation, columns, sort — nothing that alters who is in the set.
- **Apply path.** If the refinement's referenced attributes are all present in the artifact → filter/aggregate the artifact in code, no AD hit. If an attribute is missing → re-run the **frozen steps** with that attribute added to the relevant step's `attributes`, one directory search, re-freeze the artifact. Never per-DN fan-out.
- **Guards (each red→green, non-vacuous):**
  - a `refine` turn that emits altered `steps[]` (different seed person, added membership filter) executes against the **frozen** membership and returns the frozen count, not a re-derived one — prove it fails if the frozen-steps lock is removed;
  - "only the ones with titles" over a frozen 77-with-some-blank-titles set returns the in-code-filtered subset with **no second directory search** — prove it fails if refinement re-queries membership;
  - a refinement referencing an attribute absent from the artifact triggers exactly **one** re-run of the frozen steps (+the attribute), same membership count — prove it fails if the path fans out per-DN or re-derives membership;
  - a `fresh` turn after a thread resets the frozen steps — prove it fails if a fresh turn reuses stale frozen steps.

### Slice 7 — Frozen artifact as the set of record; unpin full results from RAM (D5)

Backend-only; **depends on Slice 6** (which establishes artifact-as-source-of-truth). Sequenced last so the conversational path is proven before the memory model changes underneath it.

- Make refinement (Slice 6) and export (Slice 4) read the on-disk artifact; drop the mandatory 2h full-result `IMemoryCache` entry (`QueryJobManager.cs:344-346`) or reduce it to an optional bounded fast path that never holds a large set resident.
- **Guard:** a test with a large (e.g. 40k-row) result asserts export and refinement succeed reading the artifact while the full-set memory cache is absent/evicted — prove it fails if either path still requires the in-RAM full result.

## Non-goals (F04)

- No accumulated multi-turn transcript to the model (FOLLOWUP-D2 unchanged); thread state is one frozen prior turn + its artifact. Narrate sees only the current turn's question + bounded reduction.
- No full result set, artifact, or download sent to the model — ever. Every model path is DATA-D1-bounded.
- No change to the Translate route policy or the retry-with-alternate-model feature; Narrate uses the same route-neutral machinery but its own alternate-retry behavior is out of scope.
- No agentic tool-calling loop; the model never live-queries AD in a loop (owner-directed: not agentic).
- No new UI framework, web font, or palette (F01 Design contract + FONT-D1 govern).
- No token-by-token answer streaming (possible later enhancement; out of scope).
- No native/interop Excel dependency for xlsx.

## Verification

`pwsh -NoLogo -NoProfile -File scripts/verify.ps1` before every commit. Each behavior-changing slice ships a focused regression guard proven to fail when its targeted behavior is disabled (repo Verification rule). Front-end slices (3, 4-UI) are guarded by the F01 T1 Playwright harness, not manual smoke notes. Both model routes (MODEL-D1) must be exercisable by the Narrate/Translate paths without provider-specific branching.
