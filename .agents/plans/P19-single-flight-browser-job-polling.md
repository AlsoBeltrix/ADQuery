# P19 — Single-Flight Browser Job Polling

Status: **Draft — implementation is not authorized**

Owner approval: Pending

Implementation dependencies: P01 must provide the canonical verification entry point before P19 adds browser tests. P03 must approve the exact Node/npm verification-tool versions if P19 introduces them. P06, P07, P13, P14, and P17 must land their reviewed execution-budget, artifact, failure, versioned-job, and feedback contracts first. P19 is a browser consumer of those contracts and must not recreate their authority.

Review status: Accepted in advisory round 3; a later independent consistency audit found and repaired three contradictions. Those post-round-3 repairs were not independently re-reviewed because the three-round limit was reached.

## Problem

The browser monitors an accepted background job with `setInterval(async () => ..., 2000)`. An interval tick does not wait for the prior asynchronous callback, so slow status requests overlap. Clearing the interval prevents future ticks but neither aborts nor invalidates requests already in flight. A late response from an old job can therefore update progress, stop a newer poller, render the wrong terminal result, or overwrite a newer error.

The same loop stops permanently on the first non-OK response or network error. It has no request timeout, retry classification, server `Retry-After` handling, exponential backoff, jitter, consecutive-failure budget, maximum automatic-monitoring session, offline/visibility behavior, or manual resume state. Every unchanged response transfers and reparses a full snapshot because the client ignores P14's version and ETag contract.

Browser teardown is also incomplete. There is no owned status-request abort controller, no `pagehide`/`pageshow` or visibility lifecycle, and no functional cancel control even though a cancel endpoint exists. Aborting a fetch, navigating, explicitly cancelling a job, receiving a cancellation-requested snapshot, and observing a terminal cancellation are distinct events but the current UI has no state model that preserves those distinctions.

Terminal rendering and feedback/retry operations share mutable global job state. Completed handling starts an unawaited preview request, feedback falls back to whichever `currentJobId` is newest, and alternate-model retry changes that global ID before all parent-attempt work is isolated. These races conflict with P14's immutable attempt/version model and P17's requirement that feedback target one exact terminal attempt after retry admission is resolved.

## Repository evidence

- `csharp/wwwroot/js/app.js:28-44` stores one mutable `currentJobId` and one interval handle but no session generation, in-flight request, ETag, last version, abort controller, timer owner, failure budget, or terminal latch.
- `csharp/wwwroot/js/app.js:655-743` uses `setInterval` with an async callback. `stopPolling` clears only future interval ticks; it cannot abort or invalidate an already-started request.
- `csharp/wwwroot/js/app.js:672-676` and `737-742` stop monitoring on the first HTTP or network error and discard all P13 retry metadata and `Retry-After` hints.
- `csharp/wwwroot/js/app.js:667-680` sends no `If-None-Match`, accepts an unbounded JSON response directly, and does not validate schema version, job identity, or monotonic snapshot version.
- `csharp/wwwroot/js/app.js:681-736` handles only queued, running, completed, failed, and cancelled. It has no branch for P14's active `CancellationRequested` or terminal `Interrupted`, and unknown status silently keeps the interval alive.
- `csharp/wwwroot/js/app.js:716-720` stops and invokes the async `displayJobResults` without awaiting it or passing a generation/abort signal.
- `csharp/wwwroot/js/app.js:774-817` performs a preview fetch whose response can render after the job was superseded or the page was suspended. It also consumes query/model/timing fields that P14 intentionally removes from the public status DTO.
- `csharp/wwwroot/js/app.js:1251-1275` clears the interval and global IDs when hiding results, but an already-running callback can repopulate the cleared UI.
- `csharp/wwwroot/js/app.js:1292-1473` binds feedback and retry to mutable global state, submits negative feedback before knowing whether retry admission succeeded, can submit a second negative record with a later comment, and falls back between feedback/current job IDs.
- `csharp/Controllers/QueryController.cs:1026-1074` currently returns an unversioned full object with raw query/model/error fields and no ETag. P14 owns replacing this route with the minimized versioned snapshot and conditional response.
- `csharp/Controllers/QueryController.cs:1120-1142` exposes cancellation, but `index.html` and `app.js` provide no job-cancel action.
- `csharp/Controllers/QueryController.cs:1271-1350` currently implements a non-idempotent alternate retry and returns a legacy shape. P14 owns replacing it with the authenticated, idempotent child-attempt command.
- `csharp/wwwroot/index.html:94-99` has a visual results/progress area without a live-region role, atomic announcement behavior, a cancel/resume control, or a focus target for terminal state.
- No JavaScript package manifest, browser-test directory, DOM test harness, or browser stage exists. P01 explicitly reserves P18–P19 as the plans that may add pinned JavaScript verification tooling.

## Admitted findings and one-commit ownership

Each admitted finding has evidence, an observable failure, and exactly one implementation commit. A slice may use seams landed by an earlier slice, but it must not absorb another finding or split its own finding across commits.

| ID | Severity and observable impact | Evidence | Owning commit |
|---|---|---|---|
| P19-F1 | **High** — overlapping and stale status callbacks can render the wrong job or execute terminal handling more than once, with no deterministic race guard. | `app.js:655-743`, `1251-1275`; no browser tests | Slice 1, `refactor: make browser job polling single-flight` |
| P19-F2 | **High** — fetch/timer/navigation/cancel teardown is ambiguous; old work survives replacement and users cannot reliably request or observe cancellation. | `app.js:655-743`; `QueryController.cs:1120-1142`; no lifecycle listeners/control | Slice 4, `feat: control browser job lifecycle` |
| P19-F3 | **Medium** — a transient failure permanently loses monitoring, while fixed synchronized polling ignores bounded backoff, jitter, and server delay. | `app.js:665`, `672-676`, `737-743` | Slice 3, `perf: back off browser job status requests` |
| P19-F4 | **High** — the client cannot reject stale/protocol-invalid snapshots, wastes unchanged transfers, and fails to terminate for all P14 states. | `app.js:667-736`; current controller at `1026-1074` | Slice 2, `feat: consume versioned job status responses` |
| P19-F5 | **High** — late preview/action responses and mutable feedback target state can bind results, retry, or feedback to the wrong attempt. | `app.js:716-817`, `1292-1473` | Slice 5, `fix: isolate terminal job actions` |
| P19-F6 | **Medium** — polling, cancellation, reconnection, pause, and terminal changes are not exposed as an accessible, keyboard-operable state. | `index.html:94-99`; no live region/cancel/resume state | Slice 6, `feat: expose accessible job monitoring state` |

## Goals

1. Give one browser document exactly one active job-monitor session and each session at most one scheduled status timer and one in-flight status request.
2. Make starting the same logical job idempotent and starting a different job invalidate and abort every status/preview/action continuation owned by the prior generation.
3. Replace interval polling with serial scheduling: schedule the next request only after the current request and snapshot handling settle.
4. Consume P14's schema version, monotonic snapshot version, opaque ETag, `If-None-Match`, `304`, `Cache-Control: no-store`, complete status set, and minimized public DTO.
5. Bound request duration, response bytes, normal cadence, retry delay, jitter, consecutive transient failures, and one automatic-monitoring session.
6. Honor P13 code/category/retry data and clamped `Retry-After` without parsing titles, exception text, provider names, `temperature`, or LDAP phrases.
7. Keep transport retry/resume on the same job; create a new P14 attempt only after an explicit authenticated user retry action with a stable idempotency key.
8. Make explicit cancel, status-fetch abort, page suspension, supersession, server cancellation request, and terminal cancellation separate deterministic transitions.
9. Stop all automatic work on terminal status before invoking terminal rendering, and make preview/download/feedback continuations generation-safe.
10. Preserve P17's exact job-scoped feedback target and retry-first sequencing without browser-supplied execution metadata.
11. Provide accessible, non-color-only queued/running/cancelling/reconnecting/paused/terminal UI and keyboard-operable controls.
12. Prove races and timing with a fake clock, controlled fetch promises, fake random source, and fake view without live network, wall-clock sleeps, IIS, AD, or provider credentials.

## Non-goals

- Do not change server job transitions, leases, queueing, retention, authorization, idempotency, retry eligibility, or artifact publication. P14 owns them.
- Do not introduce server push, WebSockets, SignalR, service workers, background sync, or cross-tab leader election. Conditional bounded polling is the approved initial browser transport.
- Do not automatically cancel a server job when a fetch is aborted, a tab becomes hidden, a document enters the back/forward cache, or navigation begins.
- Do not automatically create another job after a terminal failure or polling transport failure. P13 retry data informs display/delay; P14 requires explicit attempt creation.
- Do not persist active job IDs, ETags, snapshots, query text, feedback bodies, or owner data in local/session storage. BFCache resume uses the live in-memory session only; full reload recovery is a future decision.
- Do not redesign CSV enrichment's simulated progress interval. P18 owns CSV ingestion and any later background-job unification.
- Do not expand public job DTOs with raw query/context, exact model ID, plans, provider bodies, exception text, owner identity, physical paths, or P17's private receipt.
- Do not replace P07 preview/export transport, recompute aggregation, or retain result rows in the poller.
- Do not redefine P17 consent, event schema, one-event rule, storage, or submission window.
- Do not add browser telemetry/beacons carrying job IDs or user inputs. P16/P13 safe server telemetry remains authoritative.
- Do not add a bundler, transpiler, framework, virtual DOM, general state-management library, or third-party test/DOM package without a new approved dependency decision.

## Accepted dependency contracts

### P01/P03 — verification and tooling

P19 extends P01's canonical `scripts/verify.ps1`; it does not create a second developer command. Use the platform Node `node:test` runner, `node:assert/strict`, native ES modules, and small injected fakes so the initial browser suite needs no npm runtime/test dependency. Pin the exact owner-approved Node patch in root `.node-version`, repeat that exact version in `package.json`'s closed engine check, pin the exact npm version in `packageManager`, and validate both before tests. Commit `package-lock.json` even when the dependency graph is empty. P03 audits any dependency added later.

The canonical verifier restores with the lock, runs browser tests noninteractively, and propagates native exit codes. If and only if P01's CI owner decision is approved and its authoritative workflow has landed, the same P19-F1 commit provisions the exact checked-in Node version there before invoking the unchanged verifier command; it does not create or authorize CI on its own. Otherwise local canonical integration lands and CI provisioning remains explicitly blocked on P01's owner gate. Test output remains bounded and contains no real query/job/owner data.

### P06 — finite server execution

P06 initially bounds active execution at 120 seconds and P14 separately bounds queue age. P19's automatic-monitoring bound is a client request budget, not a claim that the server job ended. Reaching it pauses network monitoring, keeps the last known job state, and offers explicit same-job resume/cancel controls. It never publishes a synthetic Failed or Cancelled status.

### P07 — preview and downloads

Only a P14 Completed snapshot can expose P07-authorized preview/download links. P19 validates those links as same-origin expected route-family URLs and never accepts physical paths. Terminal status is committed before preview retrieval. Preview/export failure changes only the preview/download presentation; it cannot rewrite the completed job. P07 controls artifact expiry, leases, byte limits, and error codes.

### P13 — typed failures and retry hints

P19 uses stable `code`, `category`, `retry`, and optional clamped `Retry-After` for control flow. It displays only P13's fixed `title`/safe `detail`. It never parses provider/LDAP/exception prose or decides retryability from HTTP status text.

Automatic status-monitor retry is permitted only for a transport failure or a status-request problem classified `retry_same_operation` or `retry_after_delay`; it always repeats the same conditional GET. `retry_new_attempt`, `narrow_request`, and `never` never create a browser-automatic job. Terminal job retry remains an explicit P14 action.

### P14 — versioned job API

P19 starts only after P14's SID-authorized API lands. The consumed public shape is:

```text
QueryJobPublicSnapshotV1
  schemaVersion, jobId, version, status,
  createdAt, startedAt?, terminalAt?, clientExpiresAt?,
  progress?, modelRoute?, retryLineage,
  terminalOutcome?, completedResult?
```

Closed statuses are `queued`, `running`, `cancellation_requested`, `completed`, `failed`, `cancelled`, and `interrupted`. `version` is a positive safe integer that increases on every observable persisted mutation. The status route returns `Cache-Control: no-store`, a bounded opaque ETag derived from job ID/version, and `304 Not Modified` for matching `If-None-Match`. P19 does not reconstruct the ETag.

Admission/retry success or replay is `202` with canonical job ID, stable `Location`, snapshot version, and replay flag. The adapter requires `Location` to be same-origin and the exact expected status route for that returned job; it never follows an arbitrary URL. P19 retains one idempotency key per create/retry interaction through ambiguous transport responses. Cancel is an idempotent command; its response is not evidence of terminal cancellation. The next status snapshot remains authoritative.

Missing and non-owner jobs share P14's `404 job_not_found`. P19 does not distinguish them. Completed/Failed/Cancelled/Interrupted are terminal; `CancellationRequested` is active and continues conditional monitoring.

### P17 — feedback interaction

P17 owns `POST /api/query/jobs/{jobId}/feedback`, its distinct job-scoped idempotency key, explicit consent, and one immutable feedback event per attempt. P19 passes only the immutable terminal target job ID to P17's UI adapter; it never supplies query, model, result count, timing, retry flags, owner, or lineage.

When a negative interaction requests a retry, the browser first resolves P14 retry admission with the retry interaction's stable key. Only after accepted/existing or failed admission is known may P17 submit the one negative event for the parent attempt using its separate stable feedback key. Starting the accepted child poller does not change that captured parent target. Feedback failure never cancels or rolls back the child.

P17 Slice 6's landed browser flow is the source of truth; `FeedbackInteractionPort` is P19's local narrow adapter name, not a new P17/server API. At implementation start, record the exact landed function/module signatures used for open, submit, and retry-outcome handoff. If P17 still exposes globals, P19-F5 may encapsulate those functions without changing P17 request, consent, key, or one-event semantics. Any semantic mismatch stops the slice for plan reconciliation.

## Core invariants

- One `BrowserJobSessionController` owns the current document's job session. No other module starts a status timer or status fetch.
- A monotonically increasing in-memory generation identifies ownership. Every asynchronous continuation compares its captured generation and job ID before changing state or the view.
- At most one status timer handle and one in-flight status request exist. A timer is cleared before its callback starts; the next timer is scheduled only in the prior request's final state transition.
- `start(jobId)` is idempotent for the already-active job. `start(newJobId)` aborts/invalidates the old generation's status, preview, cancel, and unresolved retry work before exposing the new one. A post-admission P17 submission uses its own immutable interaction scope and is not rebound to or cancelled by the accepted child generation.
- `stop`, `suspend`, terminal publication, and `dispose` are idempotent. Each clears the owned timer, aborts owned requests, and makes late continuations no-ops.
- A browser abort is local transport control. Only a successful P14 cancel command can record cancellation intent, and only a P14 status snapshot can report `CancellationRequested` or terminal `Cancelled`.
- One validated `200` snapshot establishes ETag/version state. Only that same session may send the ETag. `304` never invokes snapshot rendering and is invalid before an initial `200`.
- Snapshot versions never regress. An equal-version `200` is an idempotent no-render response only when its ETag agrees; a higher version is the only response that updates visible job state.
- Terminal status latches before terminal work begins. A terminal version is handled exactly once even if a duplicate body is received.
- A transient status failure retains the last authoritative job snapshot and changes only the separate connection state.
- Backoff, `Retry-After`, and jitter affect timing only. They never change job status, retry eligibility, identity, or idempotency keys.
- Hidden/page lifecycle suspension owns no timer/request. Resume issues one immediate conditional revalidation of the same job.
- No raw response body, query, model ID, owner, job snapshot, or P17 input is written to console, storage, DOM data attributes, URLs outside fixed route families, or client telemetry.

## Browser module architecture

Convert the existing classic IIFE to native modules without introducing a build step:

```text
csharp/wwwroot/js/job-api.js
  bounded response reader, P13 problem parser, P14 snapshot parser,
  conditional status/cancel/retry requests

csharp/wwwroot/js/job-poller.js
  BrowserJobSessionController, policy, generation, timer/request ownership,
  version/ETag state, backoff, suspension, terminal latch

csharp/wwwroot/js/job-view.js
  DOM projection and accessible controls; no network/timers/job authority

csharp/wwwroot/js/app.js
  page composition, create/retry/feedback orchestration, P07 terminal rendering
```

`index.html` loads `app.js` with `type="module"`. Remove inline `onclick` handlers as their P17 replacement lands; bind listeners once during module initialization. Expose no polling or feedback functions on `window`.

Construct dependencies explicitly:

```text
BrowserJobSessionController
  JobStatusClient
  JobViewPort
  Scheduler: setTimeout, clearTimeout, monotonicNow
  WallClock: utcNow (Retry-After HTTP-date only)
  RandomUnit: next in [0,1)
  PollingPolicy
```

Production adapters use `fetch`, `AbortController`, `performance.now`, `Date`, and a random source. Tests use a manually advanced scheduler, controlled response promises, fixed wall time, enumerated random values, and a recording view. Do not call real timers or network from unit tests.

## Session state machine

The controller has closed phases:

```text
Idle
Monitoring(job, generation, lastVersion?, etag?, lastSnapshot?)
Waiting(job, generation, timer)
Suspended(job, generation, blockers=nonempty subset of {hidden,pagehide,offline})
Paused(job, generation, reason=transport_budget|session_budget|protocol)
Terminal(job, generation, status, version)
Disposed
```

`Requesting` is an internal ownership flag within Monitoring, not a second session. Connection presentation is `connected`, `retry_wait`, `offline`, or `paused` and never replaces the last job status.

Allowed transitions:

```text
Idle | Terminal | Paused --start(new job)--> Monitoring(new generation)
Monitoring --request starts--> Monitoring(requestOwned=true)
Monitoring --valid active snapshot--> Waiting(one timer)
Monitoring --valid terminal snapshot--> Terminal(timer/request cleared first)
Monitoring --retryable failure--> Waiting(backoff) | Paused(budget)
Monitoring | Waiting --first hidden/pagehide/offline blocker--> Suspended
Suspended --add blocker or remove blocker while another remains--> Suspended
Suspended --remove final blocker--> Monitoring(immediate conditional request)
any live phase --start(different job)--> Monitoring(new generation; old aborted) when no blocker is active, otherwise Suspended(new generation; old aborted)
any live phase --dispose--> Disposed
```

No transition from a local phase writes a server job status. Unknown phase/status transitions fail closed into Paused(protocol), clear automatic work, retain safe last-known state, and present a refresh action.

## Single-flight scheduling algorithm

`start(jobId)` canonicalizes through P14's job-ID parser/adapter and increments the checked generation only for a different/new session. Checked generation overflow disposes the controller and requires a page refresh; it never wraps. Starting clears the prior timer and aborts the prior generation's status, preview, cancel, and unresolved retry scopes before the view binds the new job. A consented P17 submission begun after retry admission is governed by its separate interaction token and captured parent target, so child start neither aborts it nor grants it authority to mutate the child view.

`requestNow(generation)`:

1. Return if generation/job no longer owns the controller, the session is suspended/paused/terminal/disposed, or a status request already exists.
2. Clear/null the firing timer before starting I/O.
3. Create one request controller linked to the session controller and one tracked request-timeout timer.
4. Send the fixed same-origin status GET with credentials, `cache: no-store`, `Accept: application/json, application/problem+json`, and `If-None-Match` only when this session owns a validated ETag.
5. Clear the request-timeout timer in `finally`, regardless of response, abort, or parser failure.
6. After every await, verify generation, job ID, and request identity before parsing further or mutating state/view.
7. Process exactly one response outcome.
8. Null the in-flight owner before scheduling at most one next timer.

The controller never calls `setInterval`, recursively invokes an unawaited request, or stores a promise without observing it. Request timeout aborts only the status GET and enters the transient-failure classifier. An abort caused by supersession/suspension/disposal is expected teardown and consumes no failure budget.

## Version, ETag, and response validation

The status client reads response bytes through a counting reader. `Content-Length` over the bound rejects before reading; absent/incorrect length still stops at bound plus one. Initial recommendations are `MaxStatusResponseBytes=65536`, `MaxProblemResponseBytes=16384`, and `MaxEtagBytes=256`.

For `200 OK`:

- Require JSON media type, schema version 1, exact requested job ID, positive safe-integer version, a known status, and the P14 status-specific nullable shape.
- Require one bounded ETag containing no control character. Treat it as opaque and send it verbatim only through Fetch's header API.
- Require `Cache-Control` to contain `no-store`; the shared server/browser contract fixture fails if P14 or P19 drops it.
- Queued has no started/terminal/result/outcome fields. Running and CancellationRequested have started time but no terminal/result/outcome. Completed has terminal/client-expiry and completed-result fields but no P13 failure/cancellation outcome. Failed, Cancelled, and Interrupted have terminal/client-expiry plus their sanitized P13 outcome and no completed result.
- First version establishes state. A lower version is protocol-invalid. Equal version plus different ETag is protocol-invalid; equal version plus equal ETag performs no render. A higher version replaces the immutable last snapshot/ETag.
- Do not require contiguous versions; the browser can legitimately miss intermediate progress updates.
- Mark a terminal snapshot and clear scheduling before calling the terminal renderer.

For `304 Not Modified`:

- Require prior validated snapshot/ETag for the same job/generation.
- Require the same `Cache-Control: no-store` contract; retain the prior ETag if the response omits it and reject a contradictory replacement ETag.
- Read no body, perform no progress/terminal render, reset consecutive transient failures, and schedule the normal cadence.

For an error:

- Parse a bounded P13 problem only when the media type and schema are valid. Control flow uses its fixed code/category/retry and response `Retry-After`; display uses fixed title/safe detail.
- `401` stops and presents reauthentication. P14's indistinguishable `404 job_not_found` stops and clears retry/cancel authority. Other nonretryable/protocol errors pause or terminate monitoring according to their typed contract.
- A malformed/oversized body becomes a fixed local protocol state; never render or log the body.

## Cadence, backoff, jitter, and budgets

Recommended initial immutable policy, pending owner approval:

```text
NormalPollDelayMs                 2000
NormalJitterRatio                0.10
StatusRequestTimeoutMs          15000
TransientBackoffBaseMs           1000
TransientBackoffCapMs           30000
ServerRetryAfterMinMs             1000
ServerRetryAfterMaxMs           300000
MaxConsecutiveTransientFailures      6
MaxAutomaticMonitoringMs       2700000  (45 active minutes)
MaxStatusResponseBytes            65536
MaxProblemResponseBytes           16384
MaxEtagBytes                         256
```

All values are finite positive integers and validated once during composition. Checked arithmetic prevents exponent/session-duration overflow. `NormalJitterRatio` is within `[0, 0.25]`; runtime random samples outside `[0,1)` are invariant failures.

After a valid 200/304, normal delay is:

```text
floor(NormalPollDelayMs * (0.9 + 0.2 * randomUnit))
```

With the recommended ratio this is 1800–2199 ms. General implementation uses `1-ratio + 2*ratio*randomUnit`.

For consecutive transient failure number `n` starting at 1:

```text
cap_n = min(TransientBackoffCapMs,
            TransientBackoffBaseMs * 2^(n-1))
equal_jitter = floor(cap_n / 2 + randomUnit * cap_n / 2)
delay = max(equal_jitter, valid Retry-After delay)
```

Parse `Retry-After` as whole delta-seconds or a standards-compliant HTTP date against the injected wall clock. Clamp valid positive values to 1–300 seconds. Ignore malformed, negative, overflowed, NaN, or past values. Apply it only to `retry_after_delay`; it is a minimum, so jitter never schedules earlier than the server hint.

A valid 200/304 resets consecutive failures. The sixth consecutive transient failure reaches `MaxConsecutiveTransientFailures`, pauses with no timer/request, and offers `Resume status checks`; explicit resume resets the consecutive counter but keeps the same job/version/ETag. Cumulative active monitoring excludes hidden/pagehide/offline suspension. At 45 active minutes it pauses similarly. Each explicit user resume begins a new bounded automatic session; no automatic loop can run forever.

## Cancellation and action single-flight

Add a real `Cancel query` button for Queued and Running. Bind one action handler and disable it while a cancel request is unresolved. The action adapter has its own bounded request timeout and generation scope; it does not reuse or replace the status controller's abort signal.

Cancellation rules:

1. Clicking cancel changes only the action presentation to `Requesting cancellation…` and POSTs P14's fixed cancel route.
2. A 2xx accepted/already-requested/already-cancelled result triggers one immediate status revalidation. It does not synthesize Cancelled.
3. A `409 job_not_cancellable` also triggers immediate status revalidation because completion may have won.
4. A timeout/disconnect is ambiguous. Keep monitoring the same job, show `Cancellation could not be confirmed`, and permit the idempotent cancel action again.
5. A P14 `CancellationRequested` snapshot shows `Cancelling…`, disables duplicate cancel, and continues conditional status monitoring.
6. Only a terminal `Cancelled` snapshot stops monitoring and announces cancellation. Completed/Failed/Interrupted wins are rendered as received.

Retry and create buttons use separate one-action-at-a-time gates and one canonical UUID idempotency key per user interaction. A lost response retries with the same key. Double click/Enter cannot start parallel action calls or generate a second key.

## Page, visibility, offline, and replacement teardown

Install lifecycle listeners once and remove them on controller disposal. Track visibility, page-presence, and connectivity as an idempotent blocker set rather than one replaceable reason. `hidden`, `pagehide`, and `offline` add their own blocker; `visible`, `pageshow`, and `online` remove only their paired blocker. A start or resume may issue I/O only when the set is empty:

- `visibilitychange` to hidden: suspend, clear timer, abort status/preview reads, retain immutable last snapshot/ETag and terminal preview state, and never POST cancel.
- visible: remove only the hidden blocker. If no blocker remains and the same nonterminal session is active, issue one immediate conditional revalidation. If no blocker remains and the same Completed generation has an unfinished preview, restart that one bounded preview read instead of restarting status polling.
- `pagehide`: always suspend before BFCache/navigation. Do not use `unload`/`beforeunload` network calls.
- `pageshow`: remove only the pagehide blocker and resume the in-memory session when BFCache restored it and no other blocker remains; a fresh reload has no persisted session.
- `offline`: suspend automatic requests and present offline state.
- `online`: remove only the offline blocker and resume once if visibility/page state permits and no other blocker remains.

If blockers overlap, clearing only one never starts a request; for example, `online` while hidden and `visible` while offline remain suspended. When the final blocker clears, generation plus in-flight/timer ownership coalesces simultaneous resume signals into one request. Starting a job while any blocker is active binds the new generation in Suspended state and owns no timer/request. Aborts caused by these transitions never show as network errors. A page leaving permanently relies on normal browser object collection after deterministic abort/listener teardown; it does not attempt a keepalive cancellation.

Starting a new root job or accepted retry child calls `start(newJobId)` only after admission returns. The old generation is invalid before new UI state is exposed. Late old status, cancel, preview, retry, or feedback responses can complete but must fail the generation/interaction check before touching the new view.

## Terminal rendering and P07 integration

On terminal snapshot, latch terminal state and clear the status timer/request before invoking any view callback.

- Completed: bind the exact terminal job/version, show authoritative row count/download actions, and fetch P07 preview through a terminal-render scope owned by that generation. A supersession/page suspension aborts preview. A late preview cannot render. Preview failure leaves completed summary/download state intact and renders the typed P07 problem locally.
- Failed: show P13 fixed title/detail plus code-specific action. `narrow_request` focuses the query editor; `retry_new_attempt` or elapsed `retry_after_delay` may enable an explicit P14 retry button. No automatic retry.
- Cancelled: show a distinct cancellation outcome, clear busy state, and return focus/action to a new query.
- Interrupted: show a distinct service-interrupted outcome and expose explicit retry only if the P13/P14 contract permits it.

Unknown schema/status, regressive version, wrong job ID, invalid terminal shape, missing required ETag, or duplicate contradictory terminal data stops automatic work as a protocol state. It never guesses Completed or retries a new attempt.

## P17-safe feedback and retry sequencing

Remove `feedbackState.currentJobId || state.currentJobId` fallback. A feedback interaction captures one immutable `targetJobId` from the terminal callback plus one P17 idempotency key. It contains no query/model/result/timing/lineage metadata.

For negative feedback without retry, P17 submits once after complete consent/comment collection. For negative feedback with retry:

1. Capture parent target, normalized user choices, P17 key, and one P14 retry key in an interaction object.
2. Disable duplicate feedback/retry controls.
3. Await P14 retry admission to accepted/existing or typed failure using the same retry key across transport retries.
4. If a child is accepted, start its poller by child ID while retaining the parent target in the interaction closure.
5. Submit the one P17 event for the parent after the retry outcome is known; accepted-child facts come from P14's private same-read projection, not the browser.
6. If retry admission fails, submit the one consented negative event without a fabricated child fact.
7. If feedback fails, keep the child monitor and offer a same-key feedback replay only within P17's authority window. Never resubmit a modified body under that key.

P19 does not decide whether P17 exposes feedback on Failed/Cancelled/Interrupted. It invokes the feedback adapter only for terminal classes enabled by the landed P17 UI policy.

## Accessible and user-visible state

Add distinct elements rather than overloading the result summary:

```text
jobStatusRegion     role=status, aria-live=polite, aria-atomic=true
jobConnectionState  concise connection/retry/pause text
jobCancelButton     real button, visible only while cancellable
jobResumeButton     real button, visible only while monitor paused
jobTerminalHeading  tabindex=-1 focus target
errorRegion         role=alert for actionable terminal/protocol errors
resultsRegion       aria-busy=true only while authoritative job is active
```

Do not announce unchanged `304` responses, equal versions, countdown ticks, node counters on every poll, or hidden-tab transitions. Announce meaningful versioned phase/status changes, one reconnecting message, one paused message, and one terminal message. Progress text is plain text, never `innerHTML`.

User-visible mapping:

| Authoritative/connection state | Message | Controls |
|---|---|---|
| Queued | `Query queued.` | Cancel |
| Running | Safe phase/progress summary | Cancel |
| CancellationRequested | `Cancellation requested; waiting for the job to stop.` | Cancel disabled |
| Retry wait | Preserve last job message; `Connection interrupted; retrying shortly.` | Cancel when server state permits |
| Offline/suspended | Preserve last job message; `Status checks paused while offline/hidden.` | Resume when eligible; Cancel when online |
| Transport/session budget paused | `Status checks paused. The server job may still be running.` | Resume, Cancel |
| Completed | `Query completed.` | Preview/download and P17 feedback |
| Failed | P13 fixed safe title/detail | P14-eligible explicit retry or edit query |
| Cancelled | `Query cancelled.` | New query |
| Interrupted | P13 fixed service interruption message | P14-eligible explicit retry |

Buttons retain visible focus, have stable names, and expose disabled state. Spinner animation is decorative/`aria-hidden` and respects `prefers-reduced-motion`. Do not move focus for every progress update. On terminal state, move focus once to the terminal heading only when the page is visible and the session was initiated in this document; never steal focus after BFCache resume without user activity.

## Privacy and safe browser diagnostics

Remove logging of response bodies and mutable job objects. Production console output, if retained at all, uses only fixed local event names and never query/context, problem detail, model/provider ID, owner, P17 comment/consent, physical path, raw URL, or snapshot JSON. Do not place job IDs or ETags in DOM attributes, analytics, local storage, or error copy.

The UI may use an opaque job ID only inside the fixed same-origin API route and in-memory session. P13's ordinary request correlation can be displayed from a safe problem when present; it is not parsed into control flow.

## Deterministic verification

### Tooling and fakes

Use native Node tests and no live browser/network for the canonical race suite:

```text
tests/browser/fakes/fake-clock.js
tests/browser/fakes/fake-fetch.js
tests/browser/fakes/fake-view.js
tests/browser/job-poller.test.js
tests/browser/job-api-contract.test.js
tests/browser/job-actions.test.js
tests/browser/job-view.test.js
tests/browser/fixtures/...
```

The fake clock queues timer callbacks by due time and exposes `advanceBy`/`runNext` without sleep. Fake fetch returns controlled promises, records signals/headers, and can resolve after abort to prove generation checks. Fake random uses a finite declared sequence and fails on unexpected consumption. The recording view stores fixed method calls, not DOM strings from production data.

P14/P13 .NET HTTP golden tests and Node contract tests consume the same checked-in bounded fixture files for status/problem casing and headers. P17 browser fixtures use only route job ID, idempotency key, sentiment/comment/consent; forbidden legacy metadata is absent. Do not maintain a second handwritten server enum list without a parity guard.

### Required tests

1. Starting one job creates one immediate request; advancing many normal intervals never produces more than one timer or one in-flight status GET.
2. Hold the first request beyond multiple interval durations; no second request starts. Resolve it and exactly one next timer appears.
3. Repeated `start` for the same active job is idempotent. Starting another job aborts the old scope and creates one new generation.
4. Resolve an old status/preview/action promise after supersession; it makes zero state/view calls and cannot stop the new session.
5. Duplicate/equal terminal response invokes terminal handling exactly once; terminal latch owns no timer/request before the handler begins.
6. Status-request timeout clears its timeout handle and enters transient backoff; lifecycle/supersession abort consumes no failure count.
7. Valid 200 stores ETag/version and the next request sends exact `If-None-Match`; 304 reads no body, renders nothing, and schedules normally.
8. Lower version, equal version with changed ETag, wrong job ID, unknown schema/status, missing/oversized ETag, malformed/oversized body, and 304-before-200 pause as protocol failures.
9. A higher noncontiguous version is accepted; equal version/equal ETag 200 is a no-render compatibility case.
10. Table-drive Queued, Running, CancellationRequested, Completed, Failed, Cancelled, and Interrupted shapes and exact terminal/nonterminal behavior.
11. Random endpoints prove normal jitter and equal-jitter min/max. Checked exponential delay reaches but never exceeds the client cap.
12. Delta-seconds and HTTP-date `Retry-After` parse against fake time, clamp to 1–300 seconds, dominate smaller jitter, and ignore invalid/past/overflow values.
13. P13 `retry_same_operation`/`retry_after_delay` and network failures retry the same GET; `never`, `narrow_request`, and `retry_new_attempt` never auto-create a job.
14. The first five transient failures schedule bounded waits; the sixth reaches the configured limit and pauses with zero timer/request. Manual resume retains job/version/ETag and starts one request.
15. Advancing 45 active minutes pauses. Hidden/offline duration does not consume active budget; user resume starts one new bounded session.
16. Hidden/pagehide/offline each add an idempotent blocker, clear timer, and abort the request without POSTing cancel. Table-driven overlapping blockers prove that clearing one cause while another remains starts no request; only clearing the final cause starts one coalesced conditional request. Starting a new job while blocked remains suspended.
17. Listener registration occurs once and disposal removes every listener. Repeated stop/suspend/dispose is idempotent.
18. Cancel click sends one action and disables duplicates. Accepted/conflict causes revalidation but no synthetic terminal state; ambiguous timeout keeps polling and allows idempotent retry.
19. CancellationRequested announces cancelling and remains active; only a server Cancelled snapshot terminates. Completion winning the race renders Completed.
20. Create/retry double click uses one request/key. Ambiguous retry transport repeats the same key and an accepted replay starts one child poller.
21. Completed stops polling before preview. Supersession/suspension aborts preview, and late preview cannot render; preview failure preserves completed/download state.
22. P17 negative-retry flow awaits P14 admission, starts only an accepted/existing child, and submits one event for the captured parent with a separate stable key and no client execution metadata.
23. Retry rejection records no child; feedback still submits once only after the failure is known. Feedback failure never stops an accepted child and exact replay retains the original body/key.
24. Static/adapter tests prove `job-poller.js` and `job-api.js` contain no `setInterval`; `app.js` contains no job `pollInterval`/`startPolling` path and ignores the separately named `csvProgressInterval` occurrence owned by P18. If implemented as an `app.js` scan, the exclusion is limited to the existing CSV function/block and cites this P18 non-goal; every `setInterval` in a job-status symbol/module still fails. The P19 guard neither requires, deletes, nor redesigns CSV progress. The same tests reject inline job/feedback `onclick`, global polling functions, raw error-body logs, legacy feedback metadata, and message-text retry parsing.
25. View tests cover live-region roles, no unchanged/countdown announcement spam, `aria-busy`, cancel/resume disabled/visible states, terminal focus once, keyboard activation, and reduced-motion styling.
26. Seed query/model/SID/comment/ETag/problem sentinels and prove they never enter console calls, storage, DOM attributes, connection text, or action metadata.
27. The canonical verifier fails on one deliberately failing browser assertion and runs successfully from repository root and another working directory with no network after restore.

### Red/green and mutation proof

For each slice:

1. Add the focused test and confirm it fails against current or deliberately incomplete behavior.
2. Implement only the owning finding and confirm the focused test passes.
3. Temporarily disable/revert the protected behavior without rewriting history.
4. Confirm the test fails for the predicted observable reason.
5. Restore the implementation, run focused browser tests, then P01's canonical verification.
6. Commit only the restored one-finding slice and leave no timer fixture, mutation, coverage, or npm cache artifact in the worktree.

Mandatory mutations:

- Replace serial scheduling with `setInterval`; blocked-request test observes overlap.
- Remove generation check after an await; stale old response mutates the new view.
- Schedule before clearing in-flight ownership; timer/request-count invariant fails.
- Omit timeout cleanup; fake clock reports a leaked handle after success/abort.
- Send ETag from the prior job; same-job conditional-header guard fails.
- Accept a lower version or render a 304; version/unchanged guards fail.
- Reset backoff on an invalid response or ignore `Retry-After`; exact fake-time schedule fails.
- Treat navigation abort as cancel; no-cancel-on-pagehide guard observes a POST.
- Mark cancel terminal on action response; cancel/completion race guard renders the wrong outcome.
- Remove preview generation check; late preview overwrites the new job.
- Read `state.currentJobId` during P17 submission; parent/child target guard records the child incorrectly.
- Reintroduce message substring retry logic; title-variation guard changes behavior.
- Remove live-region deduplication or terminal focus latch; accessibility call-count guard fails.

## Implementation sequencing

P01, P03 tooling decision, P06, P07, P13, P14, and P17 land first. At P19 start, re-read their actual public types/routes/fixtures and stop on semantic drift. Adapt identifier/type names only when meaning is unchanged; reconcile plans rather than adding a permissive compatibility parser.

Each slice below is exactly the owning commit named in the admitted-findings table.

### Slice 1 / P19-F1 — Single-flight controller and browser harness

Commit intent: `refactor: make browser job polling single-flight`

- Add the exact pinned zero-dependency Node test harness, lock/version metadata, canonical verifier integration, and deterministic fake clock/fetch/random/view. Update an authoritative CI adapter only when P01's owner-approved workflow already exists; otherwise record that provisioning remains gated.
- Extract `BrowserJobSessionController`, generation ownership, one timer/one request invariants, serial scheduling, idempotent same-job start, supersession, request timeout ownership, and terminal latch.
- Convert `app.js` to native module composition and remove the job status `setInterval` without adding lifecycle/backoff/version behavior owned by later slices.
- Add tests 1–6 and the overlap/generation/timer-leak mutations.

### Slice 2 / P19-F4 — Versioned P13/P14 status adapter

Commit intent: `feat: consume versioned job status responses`

- Add bounded status/problem readers, exact P14 v1 parser, opaque bounded ETag storage, conditional GET/304 handling, monotonic version rules, complete status table, and same-origin P07 URL validation.
- Consume shared P13/P14 golden fixtures and remove raw query/model/error expectations from status rendering.
- Add tests 7–10, contract parity, secret sentinels, and ETag/version/protocol mutations.

### Slice 3 / P19-F3 — Bounded cadence and recovery

Commit intent: `perf: back off browser job status requests`

- Add validated polling policy, normal jitter, equal-jitter exponential backoff, bounded `Retry-After`, request/consecutive/session budgets, connection state, pause, and explicit same-job resume.
- Classify from P13 retry fields and network outcome only. Never auto-create a job.
- Add tests 11–15 and backoff/hint/message-independence mutations.

### Slice 4 / P19-F2 — Cancel and document lifecycle

Commit intent: `feat: control browser job lifecycle`

- Add cancel action single-flight, ambiguous-outcome handling, immediate revalidation, CancellationRequested presentation, and terminal-only cancellation.
- Add once-owned visibility/pagehide/pageshow/offline/online listeners, coalesced resume, abort scopes, and disposal. Navigation never sends cancel.
- Add functional cancel/resume controls with the minimum correct state binding; Slice 6 completes accessibility presentation.
- Add tests 16–19 and lifecycle/cancel race mutations.

### Slice 5 / P19-F5 — Terminal, retry, and feedback isolation

Commit intent: `fix: isolate terminal job actions`

- Make P07 preview/render scope generation-bound and keep completed state on preview/export errors.
- Add create/retry action gates and stable per-interaction P14 keys through response ambiguity.
- Integrate the landed P17 interaction by immutable parent target and separate key; enforce retry-first, one-event sequencing and remove all mutable job/query/model/timing feedback fallbacks/globals.
- Add tests 20–24 and preview/parent-target/action-key mutations.

### Slice 6 / P19-F6 — Accessible state projection and final guards

Commit intent: `feat: expose accessible job monitoring state`

- Add the job status/connection regions, busy state, terminal focus target, accessible cancel/resume/retry controls, non-color state styling, reduced motion, and deduplicated announcements.
- Add view/static privacy and accessibility tests, bounded fixture output, canonical integration, and supported-browser manual smoke instructions.
- Remove superseded polling helpers/selectors and document the P13/P14/P17 browser contract.
- Add tests 25–27 and live-region/focus/privacy mutations.

## Acceptance criteria

- No production job status path uses `setInterval`, an unobserved promise, or more than one timer/status request for one document session.
- Starting the same job is idempotent; starting another aborts and invalidates every old continuation before new rendering.
- Slow requests cannot overlap and stale status/preview/action responses cannot mutate a newer job.
- Request/response/ETag/timing/failure/session bounds are finite, validated, and exact-boundary tested.
- P14 schema/job/version/status shapes are validated; ETags are same-session opaque values; 304 and equal versions cause no DOM churn.
- Queued, Running, CancellationRequested, Completed, Failed, Cancelled, and Interrupted all have deterministic terminal/nonterminal behavior.
- P13 code/category/retry/Retry-After drive behavior; titles/details are display-only and raw bodies/messages are never parsed or logged.
- Network/status transport recovery repeats only the same conditional GET and pauses after its finite budget; no automatic new job exists.
- Explicit cancel is separate from fetch abort/navigation and only a terminal server snapshot decides outcome.
- Hidden/pagehide/offline sessions own no timer/request, resume once, and never send implicit cancel.
- Completed status latches before P07 preview; preview failure/late completion cannot rewrite job state or another generation.
- P17 feedback binds one immutable terminal parent, uses a separate stable key, follows retry-first sequencing, and sends no browser execution metadata.
- Queued/running/cancelling/reconnecting/paused/terminal states and controls are keyboard accessible, non-color-only, and announced without poll spam.
- Every admitted finding lands in exactly its named single commit with focused red/green mutation evidence and canonical verification.
- No implementation slice starts until its owner decisions and dependency contracts are approved/landed.

## Rollback

Use new revert commits in reverse slice order; never amend, rebase, squash, or force-push reviewed history.

- Revert P17/terminal integration before removing its captured-target adapter. Disable feedback if exact P17 authority/idempotency is uncertain; never restore client-supplied query/model/retry metadata.
- Revert accessible presentation before its view methods, but retain single-flight, teardown, typed failures, and server-authoritative status behavior.
- Revert lifecycle listeners/actions before the controller methods they call. A partial rollback must never map page navigation to server cancellation.
- Revert backoff/version adapters before the single-flight core. P14 tolerates unconditional GET, but do not restore overlapping `setInterval`; if the conditional adapter is unusable, disable async monitoring with a fixed refresh-required message until repaired.
- Remove the Node stage only in the same revert that removes all browser tests/tool metadata and restores P01's prior canonical verifier/CI shape.
- P14/P07/P17 server state and artifacts survive browser rollback. No rollback deletes jobs, feedback, or artifacts.

## Risks and mitigations

- **Native modules/tool pin can expose an unsupported browser or runner.** Choose an explicit evergreen browser baseline and exact Node/npm verification versions; fail with a clear unsupported-browser state rather than loading half the app.
- **Abort races are easy to reintroduce.** Centralize timer/request/generation ownership and require controlled promises that resolve after abort in every affected slice.
- **Backoff can delay a newly terminal job.** Reset on every valid response, keep normal cadence short, honor server minima, cap delays, and offer explicit resume after a bounded pause.
- **Pausing hidden tabs delays display, not execution.** Make that user-visible on resume and never infer cancellation; the server remains P14-authoritative.
- **A 45-minute client session can end before an unusually long valid job.** Pause rather than fail, preserve job/version/ETag, and let the user resume the same job. Tune only with owner approval and observed behavior.
- **Strict response validation can reveal P13/P14 drift at rollout.** Land shared golden fixtures and deploy server contract before browser consumer; fail closed rather than silently accepting raw legacy objects.
- **Zero-dependency view tests are not a full browser engine.** Keep DOM projection thin, test it with fakes/static guards, and require a supported-browser keyboard/screen-reader smoke before production promotion.
- **P17 and P19 both touch feedback UI.** Sequence P17 first, consume its exact interaction seam, and never reintroduce global mutable fallback state.
- **Cancel response loss is ambiguous.** Keep polling, allow idempotent cancel replay, and wait for the authoritative snapshot.
- **Multiple tabs can poll the same job independently.** This plan bounds each document; cross-tab coordination is deliberately out of scope and requires a separate privacy/lifecycle design if evidence justifies it.

## Open owner decisions

### Decision 1 — Browser and test baseline

Choose native evergreen-browser ES modules plus an exact pinned Node/npm pair using zero-dependency `node:test`, or add a bundler/test framework and its dependency lifecycle. Recommendation: native modules and built-in tests; the current app already requires modern Fetch/Abort APIs, and avoiding a dependency graph keeps deployment and audit small.

Blocked until decided: Slice 1 and therefore all P19 implementation.

### Decision 2 — Initial polling bounds

Approve 2-second normal cadence with 10% jitter, 15-second request timeout, 1–30-second equal-jitter client backoff, clamped 1–300-second server hints, six consecutive transient retries, a 45-active-minute automatic session, 64 KiB status bodies, 16 KiB problems, and 256-byte ETags. Recommendation: begin finite and tune from evidence.

Blocked until decided: Slices 1–3.

### Decision 3 — Hidden/offline/navigation behavior

Choose pause-and-resume or continued polling while hidden/offline. Recommendation: suspend timers and requests on hidden, offline, or pagehide; resume one conditional GET on visible/online/pageshow; never auto-cancel. This lowers needless traffic and preserves P13/P14's distinction between transport teardown and explicit job cancellation.

Blocked until decided: Slice 4.

### Decision 4 — Transport exhaustion UX

Choose indefinite automatic retries or a finite pause with user resume. Recommendation: pause after six consecutive transient failures or 45 active minutes, preserve the last server snapshot/ETag, and offer same-job Resume and Cancel. This bounds browser work without inventing a false terminal result or duplicate attempt.

Blocked until decided: Slices 3, 4, and 6.

## Advisory review

Record no more than three substantive headless Claude Code rounds. Each invocation uses the configured model with no model override, maximum effort, structured JSON, read-only `Read`/`Grep`/`Glob`, strict empty MCP, and no session persistence. Record the exact model from the invocation envelope, material findings, applied revision or retained disagreement, and verdict. If round 3 requires repair, apply it and mark the final repair as not independently re-reviewed; do not run a fourth round.

### Round 1 — 2026-07-22T00:05:39Z

**Reviewer:** Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict:** Revisions required; one scope correction applied

- Confirmed the single-flight/generation algorithm, P13/P14/P17/P07 boundaries, ETag/version handling, bounded backoff, cancel/navigation distinctions, deterministic tests, accessibility design, and exact F1–F6 commit mapping have no material defect.
- Found that required test 24's unqualified `no setInterval` wording conflicted with the explicit P18 non-goal because `app.js` legitimately retains `csvProgressInterval`.
- Scoped the prohibition to `job-poller.js`/`job-api.js` and the removed job `pollInterval`/`startPolling` path; a narrowly named P18 CSV exclusion makes that code irrelevant to P19 rather than a target or prerequisite of its guard.
- Also clarified that P19 updates CI only after P01's owner-gated workflow exists and that `FeedbackInteractionPort` is a local adapter over P17 Slice 6's landed browser seam, not a new server contract.

### Round 2 — 2026-07-22T00:09:28Z

**Reviewer:** Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict:** Accepted; no material findings

- Confirmed test 24's scoped job-status prohibition preserves P18 ownership while still detecting a reintroduced job interval.
- Confirmed CI provisioning remains behind P01's owner gate and is only enabling infrastructure in P19-F1, not a second finding.
- Confirmed `FeedbackInteractionPort` is a local adapter over P17 Slice 6 and introduces no new server authority.
- Confirmed the complete revised plan has no remaining material race, HTTP, boundedness, dependency, privacy, accessibility, verification, sequencing, or one-commit-mapping defect.
- Suggested one optional wording precision: describe the CSV rule as an exclusion rather than claiming the guard itself prevents deletion. That clarification was applied after the verdict and proceeds to final round 3.

### Round 3 — 2026-07-22T00:11:34Z

**Reviewer:** Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict:** Accepted; no findings

- Confirmed the final test-24 wording is implementable against the named current CSV block, ignores P18-owned progress rather than requiring or changing it, and still rejects every job-status `setInterval` through static and behavioral guards.
- Confirmed the single-flight/generation algorithm, ETag/version contract, bounded backoff/`Retry-After`, cancel/navigation behavior, P07/P13/P14/P17 boundaries, privacy, accessibility, deterministic verification, sequencing, rollback, and F1–F6 one-commit map have no remaining material defect.
- At the close of round 3, no reviewer-requested repair was required.

### Post-review independent consistency audit — 2026-07-22

This is not a fourth advisory-review round. An independent read-only audit after round 3 found three internal contradictions and repaired them:

- Aligned the six-failure decision, policy value, prose, and deterministic test so the sixth consecutive transient failure pauses rather than scheduling a seventh attempt.
- Moved create/retry single-flight test 20 from Slice 4/P19-F2 to its actual Slice 5/P19-F5 owner, preserving a green one-finding-per-commit sequence.
- Replaced the lossy single suspension reason with an idempotent blocker set and added overlapping-cause/start-while-blocked guards so partial resume cannot issue traffic while another lifecycle blocker remains.

These repairs were not independently re-reviewed because the three-round limit was already reached. Implementation remains blocked on owner approval and must validate these exact repaired contracts during its required red/green guard proofs.
