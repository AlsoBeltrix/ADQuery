# F04 — Genuine Conversational Answers

**Status: Draft (2026-07-28). Not approved; no code lands until the owner flips this status line to `Approved`.** Open owner decisions are listed under "Open owner decisions"; each must be ruled before the slice that depends on it starts.

This plan is self-contained. A cold, less-capable agent can implement it without the originating conversation. It builds on F01 (`.agents/plans/F01-conversational-query.md`, Implemented) and F02 (mockup UI, Done); read the F01 Design contract for the chat/main-window tokens — F04 does not restate them.

## Problem

The app was redirected to answer routine AD questions conversationally (F01 entry point, 2026-07-27), but it still behaves as a bulk-export tool. The root cause is architectural: **the model only ever produces a query plan. It never sees the results and never writes an answer.** Everything the user reads back is assembled by code.

Observed evidence (deployed logs, 2026-07-28, under `E:\WWWOutput\mcoelho\` per-job `.log` files and `D:\inetpub\adquery\logs`):

1. **The chat never speaks.** The chat answer bubble is filled by `summariseJobForChat` (`csharp/wwwroot/js/app.js:1171`), which returns a fixed template derived from the headline kind — e.g. `"42 matches. See the result panel."`. The model is not consulted after the plan runs. There is exactly one model call per query, `GenerateExecutionPlanAsync` (`csharp/Services/IClaudeService.cs:10`), and it emits only the plan.

2. **A guess-based transform silently mutates results.** For the query *"what's the most common value in extensionAttribute1"* (job `eed12d05`, Opus; retried as `52058fdd`, gpt-5.5 — identical outcome) both models produced a correct plan: search all Users, `aggregation: { group_by: [extensionAttribute1], count: true }`. The app then discarded that aggregation and expanded to **26,625 rows** because of the heuristic at `csharp/Services/QueryJobManager.cs:354-400`: "if projection columns exactly match `group_by`, the user wants unique values as data" (`aggregation = null` at `:393`). `extensionAttribute1` is ~unique per user, so "distinct values as data" is a full dump. The ranked count answer the question actually needed was computed and thrown away. Owner ruling: never transform the row set on a guess — delete the heuristic (see D2).

3. **Follow-ups re-run the export machine and can hard-crash.** The conversational refinement *"only users with titles"* (job `3b3223c7`, following the Sanjay roll-up) failed with `Projection filter value is required` because the model emitted a `not_equals ""` projection filter that `PlanValidator` rejects. A chat refinement should not fault the whole turn (see D3, scoped).

The F01/F02 work delivered the chat *surface*, the plan-shape headline, and the follow-up plumbing (`previousJobId`, byte-capped last-turn context). What is missing is the assistant itself: a model that reads the results and answers in words, and the removal of the code path that corrupts results on a guess.

## End state (owner-confirmed in chat, 2026-07-28)

- **Answer-first.** Every response leads with a plain-language answer written by the model.
- **Export is always available and unobtrusive** — a permanent link/affordance on every result, never the headline.
- **Export never triggers a new model call.** Confirmed already true today: `DownloadAsync` (`csharp/Controllers/QueryController.cs:1032`) reads the settled rows from the results cache via `job.ResultsCacheKey` (`:1057`) and serializes them (`:1087`); no `claude.*` call, no plan regeneration. This plan adds a guard that locks the invariant so it cannot regress.
- **No destructive result transforms.** Code never reshapes the row set on an inferred intent. The rows are what the plan produced; presentation (answer text, grouped view, table) sits on top and is always reversible to the underlying data + export.
- **The model owns answer shape**, because only it holds the question and the intent at once. A wrong answer shape is cosmetic and recoverable: the full result and export are always present regardless of what the model says.

## Relevant settled decisions (canonical in `.agents/decisions.md`; restated as constraints)

- **DATA-D1 (amended 2026-07-27)** — bounded AD values may be sent to any configured model route (primary or alternate); only a minimal slice (on-screen preview slice or aggregation summary), never the full result set, never 10k rows. Full downloads stay server-side. **F04's second model pass is a value-transmitting path and is bound by DATA-D1.**
- **FOLLOWUP-D1 / FOLLOWUP-D2** — follow-up context is byte-capped by `FollowUp:MaxContextBytes` and carries the last turn only; no accumulated transcript reaches the model or is retained server-side.
- **HEADLINE-D1** — the plan-shape headline (F01 Slice B1) is derived server-side from plan + result, not user-selected. F04's answer text is layered on top of this contract, not a replacement for it.
- Logging is unrestricted on the app's own server (F01 GATE-3 resolution): the answer-synthesis prompt and response are logged like existing raw model material.

## Current-state evidence

Re-verify line numbers before editing; they are anchors, not contracts.

- **One model call per query.** `GenerateExecutionPlanAsync(userQuery, context, ...)` (`csharp/Services/IClaudeService.cs:10`, implemented in `ClaudeService`) is the only LLM call in the query path. `QueryJobManager.ExecuteJobAsync` calls it once (`csharp/Services/QueryJobManager.cs:266`) and then executes the plan; nothing consults the model afterward.
- **The destructive heuristic.** `csharp/Services/QueryJobManager.cs:348-400`: aggregation is computed (`:352`) then, when `projectionColumns` set-equals `groupByFields` (`:355-359`), the code rebuilds `result.Data` from `grouped_counts` and sets `aggregation = null` (`:389-393`). This is the transform to delete (D2).
- **The prompt steers plans toward that heuristic.** `csharp/Configuration/prompt_template.txt:28` ("For 'unique list' or 'distinct values' queries, make projection columns exactly match aggregation group_by fields. The system will automatically return unique values with counts as data rows.") and the example at `:57`. Deleting the heuristic without revising this instruction leaves the model emitting plans designed for a transform that no longer exists — so the prompt change is part of the same slice (D2 scope).
- **Headline contract (F01 B1).** `csharp/Models/HeadlineResult.cs` / `HeadlineKind` — kinds `count | record | grouped | none`, DATA-D1-bounded, produced server-side and returned on the async job status result (`csharp/Controllers/QueryController.cs` `GetJobStatus` result block; the browser reads `job.result.headline` at `csharp/wwwroot/js/app.js:414`).
- **Chat answer is code-templated.** `summariseJobForChat` (`csharp/wwwroot/js/app.js:1171`) → `resolveChatAnswer` (`:466`, `:1147-1152`). The pending bot bubble (`.turn bot pending`, `:1133`) is settled with this template string. This is the surface that must render the model's real answer instead (Slice 3).
- **Export is cache-backed and model-free.** `DownloadAsync` (`csharp/Controllers/QueryController.cs:1032`) and the older sync `Download` (`:158`) both read a cached settled result and serialize; neither calls the model. `downloadResults` (`csharp/wwwroot/js/app.js:907`) hits `download-async/{jobId}?format=` — the completed job, not a re-query.
- **Follow-up plumbing exists.** Client sends only `previousJobId` (F01 C2); server resolves it ownership-checked into bounded last-turn context via the C1 `Compose` primitive. F04 reuses this; it does not rebuild it.
- **Validation rejects empty projection-filter values.** `PlanValidator` requires a projection filter `value` (the `Projection filter value is required` error path); a model-emitted `not_equals ""` faults the whole job (D3).
- **Automated browser harness (F01 T1, TEST-D1).** Playwright.NET headless Chromium over the static `csharp/wwwroot` with `/api` stubbed (`tests/AdQueryOrchestrator.Tests/Browser/`, `StaticSiteFixture`). Front-end slices here are guarded by it, not manual notes.
- **Verification.** `pwsh -NoLogo -NoProfile -File scripts/verify.ps1` (locked-mode restore, format, build warnings-as-errors, full test suite, vuln audit). Every behavior-changing slice adds a red→green non-vacuous guard.

## Open owner decisions

Each is presented to the owner in chat as a one-line y/n ask; the ruling is recorded here and in `.agents/decisions.md` before the dependent slice starts. No slice depending on an unruled decision begins.

- **D1 — Second model pass for the answer.** A genuine chat requires the model to read the results and write the answer, so F04 adds a **second model call per query** (after the plan executes), sending a DATA-D1-bounded result slice. Cost: one extra call's latency and token spend per query, and bounded AD values leave on that call. Status: **pending owner y/n.**
- **D2 — Delete the distinct-list heuristic and revise the prompt.** Remove the data-mutating transform at `QueryJobManager.cs:354-400` and rewrite `prompt_template.txt:28,57` so "unique list / distinct values" queries render from the surviving `grouped` aggregation (list + counts) rather than the deleted transform. Consequence: "unique list of departments" returns the grouped counts view, not bare distinct data rows. Status: **pending owner y/n.**
- **D3 — Scope of the follow-up-crash fix.** Whether to fix the projection-filter strictness (`not_equals ""` → treat as "attribute is populated" / drop the empty-value filter) inside F04, or leave it out of scope as a separate finding. Status: **pending owner y/n.**

## Slices

Safest-first, one concern each, each its own commit with a provable red→green guard and `scripts/verify.ps1` before commit. A later slice does not start before the earlier one is committed. Depends-on decisions are named per slice.

### Slice 1 — Delete the destructive result transform (D2)

Backend + prompt; pure removal of a data-mutating branch plus the prompt guidance that steers toward it. No new model call. **Depends on D2.**

- Remove the distinct-list transform at `csharp/Services/QueryJobManager.cs:354-400`: stop rebuilding `result.Data` from `grouped_counts` and stop clearing `aggregation`. A plan whose projection columns equal its `group_by` fields now keeps its computed aggregation, exactly like any other grouped plan.
- Revise `csharp/Configuration/prompt_template.txt:28` and the example at `:57`: drop the instruction to make projection columns match `group_by` "so the system returns unique values as data rows"; instead instruct that "unique list / distinct values / most common" queries use a normal `group_by` aggregation (the grouped headline/rendering presents the list with counts).
- Confirm the F01 B1 headline classifier still produces a correct `grouped` kind for these plans (it keys on `Projection.Aggregation != null` + non-empty `GroupBy`; the distinct-list branch that produced `count` is gone). Adjust the classifier if it relied on the transform.
- **Guard:** a backend test feeding the "most common value" plan+result (projection column == single `group_by` field, near-unique values) asserts the settled result retains the aggregation (grouped counts) and does **not** expand to one-row-per-distinct-value. Prove it fails against the pre-removal tree (which produces N distinct rows and null aggregation).

### Slice 2 — Answer synthesis contract (the model writes the answer)

Backend-only; adds the second model pass and its bounded input. No UI. **Depends on D1.**

- Add a synthesis step to the async job path, after execution and headline computation (`csharp/Services/QueryJobManager.cs`, after the aggregation/headline block): call the model a second time with (a) the user's question, (b) the plan `description`, and (c) a **DATA-D1-bounded** result slice — the headline payload plus the same bounded preview/aggregation the UI already receives, never the full set. The model returns a short natural-language answer string.
- **Bounding is authoritative and server-side.** Reuse the DATA-D1 bound and a byte cap (mirror the `FollowUp:MaxContextBytes` mechanic; add a sibling knob `Answer:MaxResultContextBytes` if a distinct cap is needed, exposed via `/config` only if the client needs it). Truncate deterministically; never send the full result set; never send more than the DATA-D1 row/category ceiling.
- Add a synthesis prompt template (new file under `csharp/Configuration/`, loaded like `prompt_template.txt`) that instructs: answer the question directly in one to three sentences from the provided results; state plainly when there is no meaningful answer (e.g. "extensionAttribute1 has 26,625 distinct values, so there is no single most common one"); never invent data beyond what is provided.
- Store the answer on the job and expose it on the async status result DTO (extend the `GetJobStatus` result block; do not ship raw results or the raw model response to the browser beyond the answer string, which is already model-authored text).
- **Failure isolation:** if synthesis fails or times out, the job still completes with the F01 headline + table + export; the answer field is absent and the client falls back to the F01 headline template. Synthesis is additive, never a new failure mode for the query itself.
- **Guard:** a backend test with a stubbed model that (1) asserts the synthesis input is DATA-D1-bounded and byte-capped (never the full result set), and (2) asserts a synthesis failure still yields a completed job with headline+rows (answer absent). Prove each fails when the bound / the isolation is removed.

### Slice 3 — Render the model's answer, answer-first (UI)

UI presentation; replaces the code-templated chat answer with the model's real answer and leads the main window with it. **Depends on Slice 2.**

- Replace `summariseJobForChat` (`csharp/wwwroot/js/app.js:1171`): the chat bot bubble renders the model's answer string from the status DTO when present; falls back to the existing F01 headline template only when the answer is absent (synthesis failure/older job).
- Lead the main window with the answer text above the F01 headline block; the headline, table, and export remain beneath as the authoritative detail. Apply the F01 Design contract tokens (no new palette).
- **Guard:** the F01 T1 browser harness stubs a completed job carrying a model answer and asserts the chat bubble and main window render that answer text (not the "See the result panel" template), and that a job with no answer field falls back to the F01 headline. Prove it fails when the answer-render branch is removed.

### Slice 4 — Export as a permanent unobtrusive affordance + no-model-call guard

UI presentation + an invariant guard; makes export always-present-but-quiet and locks the no-re-query invariant. **No decision dependency.**

- Present export as a permanent, unobtrusive link/affordance on every result (per the F01 `.ops` pill row already in `index.html`; restyle to secondary, never the headline). It is available whenever a completed job exists, not gated behind feedback or a mode.
- **Guard (invariant lock):** a backend test asserting `DownloadAsync` produces its file from the cached settled result with **zero model calls** (inject a model service that fails the test if invoked). Prove it fails if download is rewired through any model/plan path. This encodes the owner's binding constraint (export must never risk a different result).

### Slice 5 — Follow-up refinement robustness (D3, scoped)

Backend-only; only if D3 rules "fix in F04." **Depends on D3 = fix.**

- Handle the empty-projection-filter case that faulted the Sanjay follow-up: when the model emits a projection filter with an empty `value` under a negation operator (e.g. `not_equals ""`), interpret it as "attribute is populated" (or drop the degenerate filter) rather than faulting the job in `PlanValidator`. Keep the strict rejection for genuinely malformed filters.
- **Guard:** a validator/executor test feeding the `not_equals ""` projection filter asserts the job completes (filtering to populated values) instead of `Projection filter value is required`. Prove it fails against the current strict path.

## Non-goals (F04)

- No multi-turn transcript to the model beyond the last turn (FOLLOWUP-D2 unchanged); the second pass sees only the current turn's question + bounded results.
- No full result set or download sent to the model; the answer-synthesis slice is DATA-D1-bounded like every other model path.
- No change to the plan-generation model call, routes, or the retry-with-alternate-model feature.
- No new UI framework, web font, or palette (F01 Design contract governs).
- No streaming of the answer token-by-token (a later enhancement if wanted; out of scope here).

## Verification

`pwsh -NoLogo -NoProfile -File scripts/verify.ps1` before every commit. Each behavior-changing slice ships a focused regression guard proven to fail when its targeted behavior is disabled (repo Verification rule). Front-end slices (3, 4-UI) are guarded by the F01 T1 Playwright harness, not manual smoke notes.
