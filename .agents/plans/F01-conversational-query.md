# F01 — Conversational Query Experience

**Status: Draft (not approved for implementation).** This is the first *feature* plan (the `F` namespace), distinct from the `P01`–`P21` remediation plans that came out of the 2026-07-21 codebase review. It captures the owner's redirect: make the AD query app answer the tedious, basic questions people currently bring to the team in person, without breaking the compliance and safety posture the `P` plans established.

Implementation is blocked until the owner flips this status line to `Approved`. No code change lands before that (governance: no code change without an approved plan).

## Problem

Colleagues interrupt the team to answer questions that are trivially derivable from Active Directory ("who's in group X", "how many contractors in Dublin", "is account Y disabled"). The app already answers such questions, but three gaps make it feel like a bulk-export tool rather than an answer service:

1. The UI leads with two co-equal modes — natural-language query and CSV file upload. The CSV path is niche, heavier, and distracts from the common case.
2. The result is always a table plus a download offer. A user who asked "how many X" has to read a grid to extract a single number. There is no headline answer.
3. Refining an answer means retyping the whole question. There is no cheap "no, just the contractors" follow-up. The plumbing for follow-up (`context`) exists end-to-end but the browser only ever sends a trivial hint.

## Settled owner decisions

Recorded in `.agents/decisions.md`. Restated here only as the constraints this plan implements; the decisions log is canonical.

- **DATA-D1** — bounded AD attribute values may be sent to the model, but only via the Portkey/Bedrock cleared route, only a minimal slice (the on-screen preview slice or the aggregation summary), never the full result set, never 10k rows. Full downloads stay server-side.
- **FOLLOWUP-D1** — the follow-up context sent back to the model is capped by bytes, and the cap is its own configuration knob (`FollowUp:MaxContextBytes`), not a reuse of the preview-row cap. Rationale: the preview cap governs on-screen rows; this cap governs bytes shipped to the model — coupling them would make tuning one silently move the other.
- **FOLLOWUP-D2** — follow-up carries the last turn only. No accumulated multi-turn transcript is sent to the model or held server-side for this purpose.
- **HEADLINE-D1** — the headline answer is *derived from the plan shape*, not chosen by the user from a selector. An aggregation/count plan yields a number headline; a single-record plan yields a record headline; a multi-row plan yields a count-plus-table headline.
- **FONT-D1** — the UI uses Candara, a font that ships with every supported Windows client, so no web-font hosting or CDN is introduced. Cross-platform/self-hosted fonts are explicitly out of scope for F01 and remain a later decision if a non-Windows client ever matters.

## Reference design

The approved visual direction is captured in the throwaway mockup `artifacts/mockups/qa-ui.html` (git-ignored; a design reference, not shipping code — do not commit it and do not treat its markup as the implementation). Its binding characteristics:

- **Main window** renders the answer as a "block" in whichever of three layouts fits the plan shape: a value hero (single number/value), a person record (key/value grid), or a count-plus-table. Max content width ~980px so terse answers do not sprawl.
- **Floating chat** docked bottom-right drives queries. Resizable by dragging the top-left handle, clamped to 50vw × 100vh, min ~300×260. No box-shadow (it muddied the text underneath).
- **Exchange delineation** — each question+answer pair is a bordered card; the current exchange is full-opacity with a stronger border, past exchanges dimmed; a hairline rule separates a question from its answer. User messages right-aligned, model answers left-aligned.
- **Theme** — OLED-black dark theme and a warm-oat (not stark-white) light theme, muted-teal single accent; values are emphasized by size and weight, not color. Exact custom-property values are in the mockup's `:root` / `html[data-theme="light"]` blocks; treat those as the source palette to port into `css/styles.css`.

## Current-state evidence

Re-verified against the working tree on the branch this plan is implemented from; line numbers are anchors, not contracts — confirm before editing.

- **Live query path is async.** `csharp/wwwroot/js/app.js:620` posts to `./api/query/execute-async`; the controller action is `QueryController.ExecuteQueryAsync` (`csharp/Controllers/QueryController.cs:994`). It stores `request.Context` on the job (`:1005`, `:1012`) and returns a `jobId`; the browser polls `GET /api/query/jobs/{jobId}`.
- **`context` already flows to the model.** `QueryRequest.Context` (`csharp/Controllers/QueryController.cs:1836`, `[StringLength(2000)]`) → job → `IClaudeService.GenerateExecutionPlanAsync(userQuery, context, ...)` (`csharp/Services/IClaudeService.cs:10`). The synchronous `ExecuteQuery` path passes it at `csharp/Controllers/QueryController.cs:121`.
- **The browser sends only a trivial hint.** `app.js` `buildContextHint(query)` (used at `:617`) constructs context from a "first/top N" pattern only; it never carries the prior turn's question, plan, or values.
- **Aggregation is surfaced on the async path already.** The job-status result exposes `aggregation = BuildAggregationSummary(job)` (`csharp/Controllers/QueryController.cs:1075`); `BuildAggregationSummary` (`:1788`) returns `grouped_counts`, `level_metadata`, and `group_by_fields` from `job.Plan.Projection.Aggregation` (shape defined in `csharp/Models/DirectoryQueryPlan.cs:210`). The preview endpoint `GET jobs/{jobId}/preview` (`:1088`) returns `rows`/`totalRows`/`hasMore`.
- **The synchronous `QueryResponse` drops aggregation.** `QueryResponse` (`csharp/Controllers/QueryController.cs:1848`) has `Data`, `RecordCount`, timing, and token fields but no aggregation member; `ExecuteQuery` never computes one (`:168`). This is a latent gap: any future consumer of the sync path gets no summary. The live UI does not hit this path today.
- **Plan shape signals are available.** `DirectoryQueryPlan` carries `ResultLimit`, `Steps`, and `Projection.Aggregation` (`group_by`, `count`) — enough to classify a plan as count/aggregation vs single-record vs multi-row without a user selector.
- **CSV UI is a co-equal mode.** `csharp/wwwroot/index.html:28-37` is the natural/CSV mode toggle; `:63-91` is `#csvForm`. The server endpoint `POST api/query/csv-enrich` (`csharp/Controllers/QueryController.cs:1368`) and its P04/P05 hardening stay intact.
- **Preview cap knob.** `QueryDefaults:PreviewRowCount` (default 10) governs on-screen preview rows (`csharp/Controllers/QueryController.cs:165`, `:1115`, `:1463`).

## Scope order

Three independent threads, shipped safest-first, each its own slice(s) and its own commit(s) with a red→green regression guard per the repo verification rule. A later slice must not be started before the earlier one is committed.

### Slice A — Park CSV enrichment in the UI

Smallest, lowest-risk: pure front-end removal, no model or data-path change.

- Remove the natural/CSV mode toggle (`index.html:28-37`) and the `#csvForm` block (`:63-91`) from the shipped UI, plus the CSV-specific JS branches in `app.js` (file input handling, `csv-enrich` POST, CSV stats rendering) and the CSV-only result chrome (`#csvStats`, CSV headers hint).
- **Keep the server path intact.** Do not delete `CsvEnrich`, `CsvEnrichmentService`, validators, or their tests. The endpoint remains callable; it is only unlinked from the UI. Rationale: DATA-D1/P04/P05 invested heavily in that path; parking is a UI decision, not a teardown.
- Natural-language query becomes the sole visible entry point.
- **Guard:** a UI/markup assertion (or DOM test if the harness has one) that the CSV mode controls are absent, plus confirmation the CSV endpoint and its unit tests still build and pass unchanged.

### Slice B — Derived headline answer

Medium: reuses the aggregation summary and preview already on the async path; no new data leaves the server.

- Classify the completed job's plan shape (HEADLINE-D1) into: **count/aggregation** (plan has `Projection.Aggregation` with `count`/`group_by`, or the query is a pure count) → headline is the number / the grouped counts; **single-record** (result is one row, or plan/limit implies a single subject) → headline is that record rendered as the person-record layout; **multi-row** → headline is "N results" above the table.
- Render the headline in the main-window block using the three mockup layouts. The table and the download offer remain available below the headline; the headline never replaces the authoritative download.
- Source the headline from data the async path already returns (`aggregation` on job status, `rows`/`totalRows` on preview). No new attribute values are sent to the model by this slice.
- **Close the sync-path aggregation gap** as part of this slice *only if* the sync `ExecuteQuery` path is retained and reachable; otherwise record it as a known latent gap and leave it. Decide by checking whether any shipped caller hits `ExecuteQuery` — if none, do not add speculative surface. (Flag for the owner at implementation time; do not expand scope silently.)
- **Guard:** given a completed job/plan of each shape, assert the correct headline classification and payload; prove it fails if the classifier is forced to a single branch.

### Slice C — Byte-capped, last-turn-only follow-up

Largest: new context construction, the cap knob, and the chat UI.

- **Client:** replace `buildContextHint` with a builder that, for a follow-up turn, assembles last-turn-only context (FOLLOWUP-D2): the prior question, the prior executed plan summary, and the minimal preview/aggregation slice permitted by DATA-D1 — then truncates to `FollowUp:MaxContextBytes` before sending. The chat UI holds only the last turn's state; it does not accumulate a transcript for the model.
- **Server:** add the `FollowUp:MaxContextBytes` configuration knob (FOLLOWUP-D1) and enforce the byte cap server-side as the authoritative bound — never trust the client to have truncated. Reject or truncate over-cap context deterministically with a structured outcome; do not silently forward an oversized body. Each follow-up turn re-validates through the existing P04 security policy and plan validation exactly as a fresh query does; a follow-up is not a privileged path.
- Keep `QueryRequest.Context`'s existing `[StringLength(2000)]` reconciled with the new byte cap — the byte cap is the authoritative minimal-leakage bound; the string-length attribute is a coarse transport guard. Confirm the two do not contradict (byte cap ≤ what the string length allows, or the attribute is widened deliberately with rationale).
- **Chat UI:** the floating resizable chat from the reference design drives both the initial query and follow-ups; render exchanges with the delineation described above.
- **Guard:** assert that over-cap context is bounded server-side to `FollowUp:MaxContextBytes` regardless of client input, that only last-turn material is included, and that a follow-up still passes through plan validation. Prove each fails when its enforcement is disabled.

## Non-goals (F01)

- No multi-turn transcript memory (server- or model-side) beyond the last turn.
- No sending of the full result set or downloads to the model; downloads stay server-side (DATA-D1).
- No removal or behavioral change of the CSV enrichment server path — it is parked in the UI only.
- No web-font hosting / CDN; Candara only (FONT-D1).
- No new model route; only the already-cleared Portkey/Bedrock route carries values (DATA-D1).

## Open items to confirm at implementation time

- Exact `FollowUp:MaxContextBytes` default value (evidence-derived, not guessed — size it from the actual preview+plan-summary payload, not an assumed "typical" turn).
- Whether the synchronous `ExecuteQuery` path is still reachable (governs whether Slice B closes its aggregation gap or records it).
- Whether an existing DOM/markup test harness exists for Slice A's guard, or one must be introduced.
