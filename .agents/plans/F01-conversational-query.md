# F01 — Conversational Query Experience

**Status: Approved (owner, 2026-07-27).** Approved whole after one advisory review round and resolution of all three would-be gates. Implement safest-first (A, B1, B2, C1, C2, C3) plus the SYNC-D1 endpoint retirement, each a separate commit with a red→green guard and `scripts/verify.ps1`, `/codereview` per slice. First *feature* plan (the `F` namespace), distinct from the `P01`–`P21` remediation plans from the 2026-07-21 codebase review. Captures the owner's redirect: make the app answer the tedious, routine AD questions colleagues currently bring to the team in person, without weakening the compliance/safety posture the `P` plans established. Implementation is blocked until the owner flips this status line to `Approved`; no code lands before that.

This plan is self-contained. It carries its own design tokens and DTO contracts so a cold, less-capable agent can implement it without the originating conversation or the git-ignored mockup.

## Problem

Colleagues interrupt the team for questions trivially derivable from Active Directory ("who's in group X", "how many contractors in Dublin", "is account Y disabled"). The app already answers such questions but presents as a bulk-export tool:

1. The UI leads with two co-equal modes — natural-language query and CSV upload. CSV is niche, heavier, and distracts from the common case.
2. Every result is a table plus a download offer. A "how many X" asker must read a grid to extract one number. There is no headline answer.
3. Refining means retyping the whole question. The follow-up plumbing (`context`) exists end-to-end but the browser only ever sends a trivial "first N" hint.

## Settled owner decisions

Recorded in `.agents/decisions.md` (canonical); restated here only as constraints this plan implements.

- **DATA-D1** — bounded AD values may be sent to the model, but only via the Portkey/Bedrock cleared route, only a minimal slice (on-screen preview slice or aggregation summary), never the full result set, never 10k rows. Full downloads stay server-side.
- **FOLLOWUP-D1** — follow-up context is byte-capped by its own knob (`FollowUp:MaxContextBytes`), separate from the preview-row cap.
- **FOLLOWUP-D2** — follow-up carries the last turn only; no accumulated multi-turn transcript reaches the model or is retained server-side for follow-up.
- **HEADLINE-D1** — the headline is derived from plan shape, not user-selected.
- **FONT-D1** — the UI uses Candara (Windows-installed); no web-font hosting/CDN.

## Resolved decisions that shaped this plan

The advisory review surfaced three would-be gates. All three were ruled by the owner on 2026-07-27 and are settled; they are recorded here as context and canonically in `.agents/decisions.md`.

- **Model-route clearance (was GATE-1) — resolved.** DATA-D1's "only the Portkey/Bedrock route is cleared" restriction is dropped (DATA-D1 amendment, 2026-07-27). The model needs the context to answer reliably and the routes are being updated anyway. Bounded AD values may be sent as steering context to whatever configured route answers — primary or alternate (the Azure OpenAI alternate, `csharp/appsettings.json:45`, included). No route-clearance guard is added; the "retry with alternate" path copying context (`csharp/Controllers/QueryController.cs:1320-1321`) is fine as-is.
- **Sync `execute` endpoint (was GATE-2) — resolved: retire it.** SYNC-D1 (2026-07-27). `POST api/query/execute` (`csharp/Controllers/QueryController.cs:93`) is unused by the shipped browser (async-only), so it is removed rather than fixed. There is therefore no sync `QueryResponse` aggregation gap to close; Slice B targets the async path only. Removal is a code change with its own guard/verification (either the first F01 code slice or an independent cleanup slice), gated on confirming no caller.
- **Logging AD values (was GATE-3) — resolved: log everything.** On-disk logging on the application's own locked-down server is unrelated to model-transmission minimization; it is unrestricted. `QueryJobManager`/`QueryLogHelper` continue to write context and raw model material as they do today (`csharp/Services/QueryJobManager.cs:167`, `csharp/Services/QueryLogHelper.cs:74`, `:89`). No redaction, no retention cap, no code change.

## Design contract (tracked; source-of-truth for the UI)

Ported from the approved mockup so this plan is self-contained. The mockup `artifacts/mockups/qa-ui.html` is git-ignored (`.gitignore:22`) and is a disposable reference only — these tokens, not that file, are binding.

### Fonts (FONT-D1)

- `--sans: "Candara", "Optima", "Segoe UI", system-ui, sans-serif;`
- `--mono: "Consolas", "Cascadia Mono", ui-monospace, monospace;`
- Light theme increases body weight to 500 (thin strokes wash out on the oat background otherwise).

### Palette — dark (default, OLED)

`--bg:#000000; --panel:#101215; --panel-2:#171a1e; --rule:#23272d; --rule-strong:#333840; --text:#e7eaee; --dim:#8b929c; --dimmer:#565d67; --accent:#7fb2ad; --accent-soft:rgba(127,178,173,0.12); --pop:#e7eaee; --pop-mark:#a6c4c0; --cyan:#90a7c0; --field:#0b0d10;`

### Palette — light (warm oat)

`--bg:#d4d2ca; --panel:#eae8e1; --panel-2:#e1dfd7; --rule:#cbc9bf; --rule-strong:#adaba0; --text:#16181b; --dim:#4c5056; --dimmer:#86867e; --accent:#326b64; --accent-soft:rgba(50,107,100,0.12); --pop:#16181b; --pop-mark:#326b64; --cyan:#3c556f; --field:#f3f1ea;`

Values are emphasized by size/weight, never color; `--pop-mark` is only a faint tint on the single hero number.

### Layout geometry

- **Main window:** `max-width:980px; margin:0 auto; padding:3rem 2.5rem 9rem;`. Result blocks are panels: `background:var(--panel); border:1px solid var(--rule); border-radius:16px; padding:1.6rem 1.8rem; margin-bottom:1.4rem;`. Block label: `.82rem/600/var(--dim)`. Value hero number and count: `2.6rem/700/var(--pop); letter-spacing:-0.02em`. Person record name: `1.9rem/700`; `.kv` rows `border-bottom:1px solid var(--rule)`. Data table: full-width, `border-collapse:collapse`, header `border-bottom:2px solid var(--rule-strong)`, cells `border-bottom:1px solid var(--rule)`.
- **Floating chat:** `position:fixed; right:20px; bottom:20px; width:380px; height:480px; min-width:300px; min-height:260px; max-width:50vw; max-height:100vh; background:var(--panel); border:1px solid var(--rule-strong); border-radius:18px;`. **No box-shadow** (it muddied text underneath). Resize handle `.rz` at top-left (`18×18px`, `cursor:nwse-resize`); drag clamps width to `50vw` and height to `100vh`.
- **Exchanges:** each Q+A is a bordered card (`border:1px solid var(--rule); border-radius:14px`); the current exchange is full-opacity with `--rule-strong` border, past exchanges dimmed (~0.62 opacity); a `1px` `--rule` hairline (`.qa-rule`) separates question from answer. User bubble right-aligned (`border-radius:14px 14px 4px 14px`), model answer left-aligned (`4px 14px 14px 14px`, `background:var(--panel-2)`), both `max-width:86%`.
- **Theme toggle:** flips `html[data-theme]` between `dark` (default) and `light`.

## Current-state evidence

Re-verified against the working tree at the branch this plan is implemented from; line numbers are anchors to confirm before editing, not contracts.

### Query paths
- **Async is the shipped path.** `csharp/wwwroot/js/app.js:620` posts to `./api/query/execute-async` → `QueryController.ExecuteQueryAsync` (`csharp/Controllers/QueryController.cs:995`), which passes `request.Context` on (`:1005`, `:1012`); actual job storage of context is in `QueryJobManager` (`csharp/Services/QueryJobManager.cs:65`). The browser polls `GET api/query/jobs/{jobId}`.
- **Sync path exists and is mapped.** `POST api/query/execute` → `ExecuteQuery` (`csharp/Controllers/QueryController.cs:93`) passes context at `:119`. See GATE-2.
- **`context` reaches the model.** `IClaudeService.GenerateExecutionPlanAsync(userQuery, context, ...)` (`csharp/Services/IClaudeService.cs:10`).
- **Browser sends only a trivial hint.** `buildContextHint(query)` (`csharp/wwwroot/js/app.js:937`, used at `:617`) returns a "Limit results to ~N" string only from a `first|top N` regex; it never carries the prior question, plan, or values.
- **`runQuery` clears state before building context.** `csharp/wwwroot/js/app.js:598-644`; the current job/count are cleared each run — a separate last-completed-turn store is required for follow-up (see Slice C).
- **Script bootstrap depends on `#queryForm`.** `csharp/wwwroot/js/app.js:2` early-exits if `#queryForm` is absent; any markup restructure must preserve or deliberately replace this bootstrap contract.

### Aggregation and plan shape
- **Async job status omits the plan.** `GetJobStatus` returns `jobId/status/query/modelUsed/...` and, when completed, `{ totalRows, aggregation, warnings, downloadUrl }` (`csharp/Controllers/QueryController.cs:1050`, result block `:1072-1078`). It does **not** expose `job.Plan`. Preview (`GET jobs/{jobId}/preview`, `:1088`) returns only `{ rows, totalRows, hasMore }` (`:1118-1123`). A browser classifier therefore has no plan shape today (see Slice B DTO).
- **`BuildAggregationSummary` field provenance.** `csharp/Controllers/QueryController.cs:1788`: `grouped_counts` and `level_metadata` come from `job.Aggregation` (the runtime dictionary), `group_by_fields` from `job.Plan.Projection.Aggregation.GroupBy` (`:1797-1804`). No current producer emits `level_metadata` (`csharp/Services/DirectoryPlanExecutor.cs:530` context).
- **Aggregation is conditional.** Async: computed only when `Projection.Aggregation != null && result.Data.Any()` (`csharp/Services/QueryJobManager.cs:310`); a **distinct-list** query (projection columns exactly equal `group_by`) is transformed into data rows and its `aggregation` is then **cleared** (`:314-353`). Sync: executor computes `result.Aggregation` under the same non-empty guard (`csharp/Services/DirectoryPlanExecutor.cs:221-222`). **Consequence:** zero-row jobs and distinct-list jobs carry no aggregation payload; the headline classifier must handle those explicitly.
- **Plan shape fields.** `DirectoryQueryPlan` has `ResultLimit` (a *cap*, not a count), `Steps` (each with `SizeLimit`, `Operation`, `Recursive`), and `Projection.Aggregation` (`GroupBy`, `Count`) — `csharp/Models/DirectoryQueryPlan.cs`. Pure-count plans pass validation with an empty `GroupBy` (`csharp/Security/PlanValidator.cs:381` context). `ResultLimit`/`SizeLimit` do **not** by themselves prove single-subject: `SizeLimit=1` can seed a multi-row expansion (`csharp/Models/DirectoryQueryPlan.cs:57`).

### Config, validation, and rendering
- **`/config` does not expose a follow-up cap.** `GetConfig` returns `previewRowCount/summaryRowCount/...` (`csharp/Controllers/QueryController.cs:397-408`); `loadConfig` in the browser reads that set (`csharp/wwwroot/js/app.js:451`). A new client-consumed knob must be added there.
- **`QueryRequest.Context` guard.** `[StringLength(2000)]` (`csharp/Controllers/QueryController.cs:1837`) measures UTF-16 code units and, under `[ApiController]` (`:23`), is enforced during model binding — i.e. **before** any custom handler runs. A UTF-8 byte cap is a different measure and must be reconciled (see Slice C).
- **`PreviewRowCount` is unbounded config.** Used directly by `Take` (`csharp/Controllers/QueryController.cs:165`, `:1115`, `:1463`); DATA-D1's "≤10 rows" is not enforced by a hard ceiling today.
- **Aggregation UI shows a bounded subset.** `csharp/wwwroot/js/app.js:828` context renders a capped view even though the status payload carries the full grouped dictionary — relevant to DATA-D1 bounding of what follow-up sends.
- **CSV UI surfaces.** Mode toggle `csharp/wwwroot/index.html:28-37`; `#csvForm` `:63-91`; CSV JS handlers around `csharp/wwwroot/js/app.js:64` and the `csv-enrich` fetch ~`:290`; CSV-specific CSS `csharp/wwwroot/css/styles.css:626`. Server endpoint `POST api/query/csv-enrich` (`csharp/Controllers/QueryController.cs:1368`) with P04/P05 hardening stays intact.
- **No DOM/JS test harness exists.** `tests/AdQueryOrchestrator.Tests/AdQueryOrchestrator.Tests.csproj` (context `:14`) is a .NET test project only; CSV controller tests call the action directly (`tests/.../CsvEnrichmentControllerTests.cs:37`) and would not detect loss of an HTTP route attribute. Guards must be chosen accordingly (see slices).

## Slices

Six slices, safest-first, one concern each, each its own commit with a provable red→green guard and `scripts/verify.ps1` before commit. A later slice does not start before the earlier one is committed. Backend contracts, client behavior, and UI presentation are split so each is independently guardable.

### Slice A — Park CSV enrichment in the UI

Front-end only; the server path is untouched and must keep passing its tests.

- Remove from `index.html`: the mode toggle (`:28-37`) and `#csvForm` (`:63-91`). Remove from `app.js`: the file-input handlers (~`:64`), the `csv-enrich` fetch (~`:290`), and CSV-only result chrome (`#csvStats`, headers hint). Remove dead CSV-only CSS (`styles.css:626`), preserving any shared selectors.
- **Keep the server path intact:** do not touch `CsvEnrich`, `CsvEnrichmentService`, validators, or their tests. Natural-language query becomes the sole visible entry point.
- **Guard (two parts, both provable without a DOM harness):** (1) a text/markup assertion over `index.html`/`app.js` that the CSV mode controls, `#csvForm`, the file-input handlers, and the `csv-enrich` fetch callsite are absent; (2) a controller/routing assertion that `POST api/query/csv-enrich` is still mapped (the CSV controller unit tests continue to pass unchanged, and a route-presence check confirms the endpoint attribute survives). Prove part 1 fails against the pre-removal tree.

### Slice B1 — Backend headline-shape contract

Backend-only; establishes the classifier and DTO before any UI consumes it. No new AD values leave the server.

- Define a deterministic **headline kind** with fixed precedence, computed server-side from the completed job's plan + result:
  1. **empty** — zero result rows (regardless of plan): kind=`none`, no value payload.
  2. **grouped** — `Projection.Aggregation != null`, non-empty `GroupBy`, aggregation present: kind=`grouped`, payload = the bounded grouped counts (bounded per DATA-D1, see B-note).
  3. **distinct-list** — the distinct-list transform fired (projection columns == `group_by`, aggregation cleared, `csharp/Services/QueryJobManager.cs:314-353`): kind=`count`, payload = row count (the rows *are* the answer).
  4. **pure-count** — aggregation requested with empty `GroupBy` (or an explicit count plan): kind=`count`, payload = total count.
  5. **single-record** — exactly one result row **and** the plan is not an expansion/aggregation (no recursive/expand step, no aggregation): kind=`record`, payload = that row's projected fields.
  6. **multi-row** — otherwise: kind=`count`, payload = total row count (table shown beneath).
- Expose this via a **new explicit DTO** on the async status (or a dedicated `jobs/{jobId}/headline` endpoint), never by shipping raw `job.Plan` to the browser: `{ kind, count?, record?, groups? }` where `groups` is already bounded. `record`/`groups` values are subject to the DATA-D1 bound (B-note).
- **B-note (DATA-D1 bounding):** the payload must apply an independent server-enforced ceiling on rows/categories with deterministic ordering, not rely on the unbounded `PreviewRowCount`. Fold the DATA-D1 ≤10-row ceiling into a hard server cap here.
- Scope is the **async path only** (the sync `execute` endpoint is retired per SYNC-D1; there is no sync `QueryResponse` aggregation gap to close).
- **Guard:** table-driven test feeding one plan+result per kind (including empty and distinct-list) asserting the exact kind and payload; prove it fails when the classifier is forced to a single branch.

### Slice B2 — Main-window headline rendering

UI presentation of the B1 contract; the three layouts + theme + fonts from the design contract.

- Render the headline block in the main window per kind: value hero (`count`), person record `.kv` grid (`record`), grouped list, or count-plus-table (`multi-row`/`count` with rows). Table and download offer remain beneath; the headline never replaces the authoritative download.
- Apply the tracked design contract: palette (both themes), Candara fonts (FONT-D1), block geometry, theme toggle on `html[data-theme]`.
- **Guard:** DOM-free assertion is not possible for rendering; add a lightweight rendering check appropriate to the harness (or, if none is introduced, a documented manual smoke checklist covering each kind + both themes) and state explicitly that automated coverage is manual for this slice.

### Slice C1 — Byte cap knob and server-side enforcement

Backend-only; the authoritative minimal-leakage bound. Must land before any value-bearing follow-up UI.

- Add `FollowUp:MaxContextBytes` with startup validation (finite, positive, ≤ the UTF-16 transport guard's byte-equivalent). Expose it via `/config` (`GetConfig`, `csharp/Controllers/QueryController.cs:397`) so the client can pre-truncate for UX; the server value is authoritative.
- Enforce the cap **before persistence, logging, or model transmission** on the async path via `QueryJobManager` before it stores/logs context (`csharp/Services/QueryJobManager.cs:60-65`, `:167`). (The sync path is retired per SYNC-D1, so there is one enforcement point, not two.)
- **Cap mechanics (must be specified, not left open):** measure UTF-8 bytes; on over-cap, **truncate deterministically** by dropping whole context components in a fixed order (values first, then plan summary, then prior question) and never split a UTF-8 code point; if even the minimal component exceeds the cap, drop context entirely rather than send a fragment. Reconcile with `[StringLength(2000)]`: the byte cap must be ≤ the byte-equivalent the string length permits so binding-time rejection cannot pre-empt the byte handler for in-bounds input; if a larger context is ever needed, widen the attribute deliberately with rationale.
- The cap is the *only* bound on follow-up context. There is no route-clearance gate (DATA-D1 amendment: context may go to any configured route) and no log-redaction requirement (logging is unrestricted; values are logged as normal).
- **Guard:** tests proving over-cap context is bounded to `FollowUp:MaxContextBytes` server-side regardless of client input, with deterministic component-drop order and no split code points; prove it fails when enforcement is disabled.

### Slice C2 — Last-turn context construction (client + provenance)

Client behavior plus the authoritative previous-turn source; no new UI chrome.

- Replace `buildContextHint` with a builder that, for a follow-up, assembles **last-turn-only** material (FOLLOWUP-D2): prior question, prior executed-plan summary, and the DATA-D1 minimal value slice — then pre-truncates to the `/config` byte cap.
- The "prior executed-plan summary" has no current source (status/preview expose none, `QueryRequest` has no prior-job id — `csharp/Controllers/QueryController.cs:1050`, `:1824`). Define an **ownership-checked previous-turn contract**: either a safe summary DTO returned with the completed job, or a `previousJobId` on the follow-up request that the server resolves (ownership-checked like existing job endpoints) into a bounded summary, with a defined expiry/not-found outcome. Last-turn provenance must be server-verifiable, not client-asserted.
- Introduce a dedicated **last-completed-turn** client store separate from the in-flight state `runQuery` clears (`csharp/wwwroot/js/app.js:609`).
- **Guard:** tests asserting only last-turn material is included, provenance is server-verified (a forged/foreign previousJobId is rejected), and construction respects the byte cap; prove each fails when the relevant check is removed.

### Slice C3 — Floating chat UI

UI presentation of the follow-up flow; the resizable chat + exchange delineation from the design contract.

- Build the floating resizable chat (geometry, resize clamp, no shadow) driving initial query and follow-ups; render exchanges with the delineation in the design contract.
- **Display history vs model context:** the chat may keep **display-only** past exchanges visible on the client (the design contract requires past exchanges to remain visible); this does not violate FOLLOWUP-D2, which forbids accumulated context reaching the model/server. Define reset behavior explicitly: display history is client-only, ephemeral, never sent, cleared on reload; only last-turn material (C2) is ever transmitted.
- Preserve or deliberately replace the `#queryForm` bootstrap contract (`csharp/wwwroot/js/app.js:2`).
- **Guard:** as B2 — lightweight rendering/interaction check if the harness allows, else a documented manual smoke checklist (resize clamp to 50vw×100vh, exchange delineation, display-history-not-transmitted); state coverage explicitly.

## Non-goals (F01)

- No multi-turn transcript reaching the model or retained server-side beyond the last turn (display-only client history excepted, C3).
- No full result set or download sent to the model; downloads stay server-side (DATA-D1).
- No removal or behavioral change of the CSV enrichment server path — parked in the UI only.
- No web-font hosting/CDN; Candara only (FONT-D1).
- No new model route (existing configured routes carry values per the DATA-D1 amendment; the cap is the only bound).

## Open items to confirm at implementation time

- Exact `FollowUp:MaxContextBytes` default — sized from the real preview+plan-summary payload, not an assumed "typical" turn.
- Whether to introduce a JS/DOM test harness (none exists) or accept documented manual smoke coverage for B2/C3 rendering slices — decide before B2.
