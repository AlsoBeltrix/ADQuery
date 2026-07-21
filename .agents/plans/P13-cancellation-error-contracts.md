# P13 — End-to-End Cancellation and Failure Contracts

Status: **Draft — implementation is not authorized**

Owner approval: Pending

Implementation dependencies: P01, P02, P04, P05, P06, P07, P09, and P12 must supply their verified provider, CSV, budget, artifact, LDAP, and compilation failure contracts first. P10/P11 failures flow through P12/P06. P13 must land before P14 persists immutable job outcomes and before P19 consumes browser retry/cancellation behavior.

Review status: Accepted in advisory round 2

## Problem

The application currently collapses distinct causes into strings and broad catches. Provider status/body text is embedded in `ErrorMessage`; the directory executor catches both cancellation and timeout as one message; LDAP and CSV helpers swallow broad exceptions and return partial or missing data; synchronous controllers map `OperationCanceledException` to HTTP 408; background jobs map every `OperationCanceledException` to `Cancelled` and persist `ex.Message` for unexpected failures.

This loses whether work stopped because the caller disconnected, a user explicitly cancelled a job, the P06 query deadline expired, the P09 LDAP operation timed out while its worker stayed blocked, the host shut down, admission was saturated, a provider request was incompatible, or a dependency failed. It also allows raw provider/exception content to reach users and makes retry logic depend on prose.

P02/P04/P06/P07/P09/P12 introduce typed local causes and no-partial guarantees. P13 must preserve those causes across service, workflow, background job, HTTP, and browser-facing boundaries while keeping cancellation races deterministic.

## Repository evidence

- `ClaudeService` returns mutable response objects with `Success` and `ErrorMessage`. Non-success provider responses include HTTP status plus the full bounded/unbounded error body in user-visible and logged strings.
- The reported Vertex response rejects deprecated `temperature`, but current callers see it only as `BadRequest` text rather than a stable nonretryable provider-capability failure.
- `ClaudeService` broad catches turn all thrown causes into `"Error generating ... plan."`; cancellation is not rethrown distinctly.
- `DirectoryPlanExecutor` catches `OperationCanceledException` and returns `"Query execution cancelled or timed out."`, then catches every other exception into one generic result.
- The synchronous controller catches `OperationCanceledException` and returns 408 even when `HttpContext.RequestAborted` means the client is already gone.
- `ActiveDirectoryService` group/member and lookup loops catch `Exception`, log record values/DNs, and continue, which can hide timeout, budget, cancellation, or dependency failure as partial success.
- `CsvEnrichmentService.LookupUserAsync` catches every exception, logs the lookup value, and returns null; P04's reviewed target removes that ambiguity and makes operational failure atomic.
- `QueryJobManager` turns any `OperationCanceledException` into `JobStatus.Cancelled`, even if a deadline or host shutdown caused it.
- `QueryJobManager` persists `ex.Message` and validation/provider strings in mutable `QueryJob.ErrorMessage`.
- Controllers return a mixture of `BadRequest` anonymous objects, `QueryResponse.Error`, plain strings, 408, 500, 503, and ad-hoc authorization/not-found responses.
- No versioned problem-details extension, stable retryability, cancellation provenance, safe detail policy, or typed job failure exists.
- P09's reviewed target distinguishes caller cancellation, P06 deadline, queue saturation, queue timeout, LDAP operation timeout, shutdown, and dependency failure while keeping physical worker occupancy truthful.

## Goals

1. Define one immutable, versioned failure descriptor shared by workflows and jobs.
2. Preserve stable dependency/validation/budget/artifact codes without parsing messages.
3. Track caller, user-job, deadline, dependency-timeout, and shutdown cancellation causes explicitly.
4. Select exactly one terminal cause in cancellation/completion races.
5. Preserve original exceptions for local causal logging without persisting or exposing their text.
6. Guarantee expected typed terminal failures are never swallowed into partial success.
7. Map failures to versioned sanitized HTTP problem details consistently.
8. Define retry disposition independently of HTTP status or display wording.
9. Store typed immutable job failure/cancellation snapshots instead of raw strings.
10. Give P19 stable browser behavior for retry, pause, cancellation, and protocol failures.
11. Keep P04/P06/P07 no-partial publication semantics intact.
12. Add deterministic race/failure-injection guards with fake time and tokens.

## Non-goals

- Do not add automatic provider, LDAP, query, or job retries. This plan describes whether a later explicit retry is safe.
- Do not change P02 model capability selection or request payloads.
- Do not change P06 limits/deadlines or P09 scheduler containment.
- Do not change P14 job transition/storage/admission algorithms; P13 supplies outcome values P14 later stores atomically.
- Do not change P19 polling timers/UI architecture; P19 consumes the contract.
- Do not expose provider bodies, LDAP values, raw plan diagnostics, secrets, physical paths, stack traces, or exception messages.
- Do not treat host shutdown, dependency timeout, or budget exhaustion as user cancellation.
- Do not return successful partial data after a typed hard failure.
- Do not add distributed tracing/exporters or redesign general logging sinks; P16 owns configuration/sinks.

## Core failure model

Introduce immutable values:

```csharp
public sealed record FailureDescriptor(
    int SchemaVersion,
    FailureCode Code,
    FailureCategory Category,
    RetryDisposition Retry,
    FailureOrigin Origin,
    ImmutableArray<FailureArgument> SafeArguments,
    FailureCorrelation Correlation);
```

Closed enums:

```text
FailureCategory
  invalid_request
  unauthorized
  forbidden
  not_found
  conflict
  budget
  capacity
  cancelled
  dependency
  protocol
  internal

RetryDisposition
  never
  retry_same_operation
  retry_new_attempt
  retry_after_delay
  narrow_request

FailureOrigin
  request
  plan_compiler
  provider
  directory
  execution
  csv
  artifact
  job
  host
```

`FailureCode` is a closed value type over registered lower-snake-case strings so dependency plans can add codes without exposing arbitrary strings. One central registry maps every code to category, default retry disposition, HTTP status/title, safe argument schema, logging severity, and job terminal class. Duplicate or unknown registration fails startup.

Descriptors contain no `Exception`, stack, raw message, body, query, plan, cell, DN, owner, physical path, model response, or credential. Local `FailureCapture` may pair a descriptor with `ExceptionDispatchInfo` only inside the active process call chain; it is never serialized, cached, stored in a job, or returned.

## Result and exception boundary

Expected failures use one result type at workflow boundaries:

```csharp
public readonly record struct OperationResult<T>(T? Value, FailureDescriptor? Failure)
{
    public bool IsSuccess { get; }
}
```

Construction enforces exactly one of value/failure. Cancellation may travel as `OperationCanceledException` only inside cancellable lower-level APIs; the owning workflow catches it once, resolves provenance through the cancellation context, and creates the descriptor or abandons the disconnected response.

Typed P06/P07/P09/P12 exceptions/results are translated once by registered adapters. Do not catch and rethrow a new untyped `Exception`, return null, or translate the same failure at every layer.

Unexpected programmer/invariant failures are caught only at the workflow/background boundary, logged with a safe event plus local exception capture, and mapped to `internal_error`. They are never treated as retryable by default and never expose `ex.Message`.

## Cancellation provenance

Create one request/attempt-scoped `OperationCancellationContext` with linked tokens but separate cause registrations:

```text
CallerAborted       HTTP request/browser connection token
UserJobCancelled    explicit P14 cancel command token
QueryDeadline       P06 deadline token
HostStopping        application lifetime token
```

The context owns one atomic `TerminalStopClaim` slot. A claim is an immutable discriminated value containing exactly one of:

- a configured cancellation cause plus injected monotonic timestamp/sequence; or
- a typed dependency-timeout `FailureDescriptor` plus injected monotonic timestamp/sequence.

Cancellation registrations and dependency-timeout adapters both call the same `TryClaimTerminalStop` operation, implemented as one `Interlocked.CompareExchange` from no-claim to the candidate. There is no second timeout arbiter. The first observed claim wins; later cancellation signals or timeout results remain observable in diagnostics/metrics counts but cannot rewrite the claim. Normal success or non-timeout failure publication checks the slot once at its atomic workflow completion boundary, and a winning stop claim prevents a late completion/fault from being published.

The context exposes:

- One linked observation token passed through services.
- The individual tokens for adapters that must distinguish ownership.
- `ResolveCancellation(OperationCanceledException)` which returns the winning cancellation claim, preserves a dependency-timeout claim that already won, or produces a fixed `unattributed_cancellation` internal failure when no configured token or dependency timeout caused the exception.
- Idempotent disposal of registrations and deadline source.

Do not infer cause from `OperationCanceledException.Message`, exception type alone, `CancellationToken == default`, or whichever token happens to be checked after the catch.

### Dependency operation timeouts

P09 LDAP operation timeout and P02 provider timeout are dependency failures, not parent cancellation tokens. Their adapter may complete the caller promptly while physical work remains contained as the dependency plan specifies.

Before returning a dependency timeout, atomically claim through the same parent `TerminalStopClaim` slot used by token callbacks:

1. Build the typed provider/LDAP timeout descriptor without publishing it.
2. Attempt the single no-claim-to-timeout `CompareExchange`.
3. If caller/user/deadline/host already owns the slot, preserve that cancellation claim.
4. If the timeout wins, every later token callback loses against it.
5. A late dependency completion/fault checks the same slot and cannot overwrite either winner.

P06 owns query-deadline semantics. Its deadline token and every `QueryBudgetExceededException`, including dimension `execution_time`, map to `query_budget_exceeded` with the exception's stable dimension supplied as a registered safe enum argument. P13 does not introduce `query_deadline_exceeded`. The `execution_time` argument keeps the query deadline distinct from LDAP/provider timeout without changing P06's accepted category, HTTP status, or retry disposition. Explicit job cancellation remains `job_cancelled`, not budget exhaustion. Host stopping remains `service_stopping`, not job cancelled.

### HTTP caller disconnect

If `HttpContext.RequestAborted` is the winning cause and the response has not begun, stop work and do not attempt to write a problem response to the disconnected client. Record a low-cardinality `caller_disconnected` outcome. Do not return 408.

If a transport remains connected but an explicit supported request-cancel command exists, map that command's typed outcome normally. P14 owns job cancellation endpoints.

## Code registry and mappings

The registry combines dependency-owned codes with P13-owned boundary codes. Initial mapping includes:

| Cause/code | Category | HTTP | Retry | Job outcome |
|---|---|---:|---|---|
| `request_invalid` (binding/request shape) | invalid_request | 400 | never | failed |
| `plan_invalid` (P13 aggregate carrying P12 diagnostics) | invalid_request | 422 | never | failed |
| `query_budget_exceeded` (all P06 dimensions) | budget | 422 | narrow_request | failed |
| `caller_disconnected` | cancelled | no response | never | n/a |
| `job_cancelled` | cancelled | 409/accepted endpoint contract | never | cancelled |
| `service_stopping` | capacity | 503 | retry_new_attempt | interrupted/failed until P14 decides |
| `provider_configuration_invalid` | internal | 500 | never | failed |
| `provider_authentication_failed` | dependency | 502 | never | failed |
| `provider_capability_mismatch` | dependency | 502 | never | failed |
| `provider_request_rejected` | dependency | 502 | never | failed |
| `provider_rate_limited` | capacity | 503 | retry_after_delay | failed |
| `provider_timeout` | dependency | 504 | retry_new_attempt | failed |
| `provider_unavailable` | dependency | 503 | retry_after_delay | failed |
| `provider_protocol_invalid` | protocol | 502 | retry_new_attempt | failed |
| `ldap_queue_saturated` | capacity | 503 | retry_after_delay | failed |
| `ldap_queue_timeout` | capacity | 503 | retry_new_attempt | failed |
| `ldap_operation_timeout` | dependency | 504 | retry_new_attempt | failed |
| `ldap_dependency_failed` | dependency | 503 | retry_new_attempt | failed |
| `ldap_scheduler_stopping` | capacity | 503 | retry_new_attempt | failed |
| `artifact_size_exceeded` | budget | 422 | narrow_request | failed |
| `artifact_manifest_too_large` | budget | 422 | narrow_request | failed |
| `artifact_store_full` | capacity | 503 | retry_after_delay | failed |
| `artifact_generation_busy` | capacity | 503 | retry_after_delay | failed |
| `artifact_root_in_use` | capacity | 503 | retry_after_delay | startup unavailable |
| `artifact_write_failed` | internal | 500 | retry_new_attempt | failed |
| `artifact_publish_failed` | internal | 500 | retry_new_attempt | failed |
| `artifact_not_found` | not_found | 404 | never | n/a |
| `artifact_expired` | not_found | 404 | never | n/a |
| `artifact_forbidden` | forbidden | 403 | never | n/a |
| `artifact_corrupt` | internal | 500 | never | failed |
| `export_size_exceeded` | budget | 422 | narrow_request | export-only failure |
| `export_generation_busy` | capacity | 503 | retry_after_delay | export-only failure |
| `export_generation_failed` | internal | 500 | retry_new_attempt | export-only failure |
| `unsupported_export_format` | invalid_request | 400 | never | n/a |
| `internal_error` | internal | 500 | never | failed |

The `ldap_*` strings are owned by P09, not invented by P13. Slice 1 is blocked until the reviewed P09 plan publishes its final closed taxonomy; the registry must consume those exact strings and its exhaustiveness tests must fail on a mismatch. Any later rename is a versioned cross-plan contract change.

`artifact_root_in_use` is normally exposed only as unavailable readiness because P07 fails startup; the 503 mapping is the sanitized fallback if a hosting boundary must render it. It is never downgraded to request-local success or a second-writer fallback.

`provider_capability_mismatch` covers an upstream rejection such as Vertex reporting that `temperature` is deprecated when the configured capability/request builder emitted it. P02 should prevent that request; if it occurs, P13 returns a sanitized fixed title and correlation ID, not the Vertex body or parameter text. It is nonretryable until configuration/code changes.

The exact P14 cancellation endpoint statuses remain P14-owned. P13 provides descriptor/category; P14 decides accepted/already-terminal/conflict transitions and HTTP mapping within its plan.

## Safe arguments and retry-after

Each code registers an exact argument schema. Permitted values are bounded integers/durations, fixed enum labels, and sanitized correlation tokens. No caller/provider/LDAP-supplied free text is permitted.

`Retry-After` is emitted only for `retry_after_delay` and uses a server-computed/clamped whole-second value:

- Minimum 1 second.
- Maximum 300 seconds.
- Provider header values are parsed defensively but never passed through without clamping.
- Queue/capacity services may supply a configured delay.
- Missing/invalid delay falls back to a code-specific finite default.

Retry disposition is advisory machine data, not permission to retry automatically. P19/P14 explicit retry creates or resumes only under their own idempotency rules.

## Problem-details contract

Use `application/problem+json` with a stable extension schema:

```json
{
  "type": "urn:adquery:error:provider_capability_mismatch",
  "title": "The configured model request is incompatible.",
  "status": 502,
  "instance": "/api/query/requests/<opaque-request-id>",
  "code": "provider_capability_mismatch",
  "category": "dependency",
  "retryable": false,
  "retry": "never",
  "request_id": "<opaque-request-id>",
  "schema_version": 1
}
```

Rules:

- `type`, `title`, status, category, and retry defaults come only from the registry.
- `detail` is omitted by default. A code may use one fixed safe sentence; never dependency/exception text.
- `instance` contains only the route family and opaque request/job/artifact ID already authorized for that caller.
- `traceId` may be included as an opaque correlation value; never expose internal topology.
- Validation may include a bounded array of P12 code/path diagnostics under one registered extension.
- Response bodies have an explicit 16 KiB encoded ceiling; over-ceiling optional arguments/diagnostics are omitted deterministically.
- 401 retains the framework's authentication challenge; 403 does not reveal owner/policy details.
- HEAD/error responses obey HTTP body rules.
- Errors after response headers begin abort the response and log the safe code; never append problem JSON to a partial file/download.

## Provider adaptation

P02's request gateway returns typed provider outcomes containing only:

```text
HTTP status
bounded provider error type/code
sanitized capability classification
validated Retry-After
opaque provider request correlation ID when safe
local causal exception capture
```

Classification:

- Missing/invalid local configuration -> `provider_configuration_invalid`.
- 401/403 -> `provider_authentication_failed`.
- Known unsupported/deprecated request field or model-capability rejection -> `provider_capability_mismatch`.
- Other provider 400/422 -> `provider_request_rejected`.
- 429 -> `provider_rate_limited`.
- timeout -> `provider_timeout` unless parent cancellation already won.
- 5xx/connection -> `provider_unavailable`.
- malformed successful envelope/plan extraction protocol -> `provider_protocol_invalid`.

Provider response bodies may be parsed into fixed classifications but are not stored in `FailureDescriptor`, public output, feedback events, or ordinary logs. Cancellation must be rethrown/resolved, not caught by the current broad exception handler.

## LDAP and CSV adaptation

P09 exceptions map one-to-one to P09's finalized stable code strings without losing their inner causal exception locally. Broad directory catches must rethrow:

- caller/user/deadline/host cancellation;
- P06 budget exhaustion;
- P09 saturation, queue timeout, operation timeout, stopping, and fatal dependency failure.

Only explicitly characterized per-entry not-found/nonfatal data-quality outcomes may remain best effort. They become typed lookup outcomes, not null from an exception. Any terminal typed failure discards accumulated search/lookup/traversal results.

P04 outcomes remain `Found`, `NotFound`, and `OperationalFailure`, with cancellation rethrown rather than converted to data. P05 adds `Ambiguous` and may name the operational result `Failed` at its batching boundary. P13 maps only the P04/P05 operational-failure case to terminal failure; ordinary found/not-found/ambiguous behavior follows P04/P05 decisions. Cancellation, deadline, LDAP terminal failure, compiler failure, or artifact failure publishes no CSV success.

## Compiler and artifact adaptation

P12 diagnostics retain their code/path order. The synchronous and queued workflows wrap them in one `plan_invalid` failure with a bounded structured diagnostic extension; they do not join rendered messages for control flow. The P12 interim string renderer is removed from query/job surfaces when P13 lands; the explicit validation list may remain as presentation compatibility.

P07 stable causes map through the registry. Export-only failures after a canonical artifact exists return a download problem without changing the completed query/job. Canonical publication failure is terminal for the query and leaves no success reference. A shared export generation continues if one waiter disconnects, per P07; that waiter's caller cancellation does not cancel the shared producer.

## Job failure contract

Replace `QueryJob.ErrorMessage` with an immutable, versioned job outcome payload:

```text
FailureDescriptor? Failure
CancellationDescriptor? Cancellation
```

P13 supplies values; P14 later owns atomic transitions/versioning.

- Explicit accepted user cancellation produces terminal `Cancelled` only when P14's transition wins.
- Query deadline (`query_budget_exceeded` with dimension `execution_time`), provider/LDAP timeout, other budget dimensions, compiler, artifact, and internal failure produce `Failed` with the exact descriptor.
- Host stopping produces a distinct interruption/failure code; it is not recorded as user cancellation. P14 decides restart recovery.
- A late failure/cancellation cannot overwrite a terminal completion.
- Retried jobs reference the prior job ID through P14/P17 metadata but receive a new descriptor/attempt.
- Public job snapshots expose only the sanitized descriptor fields; logs/storage never persist `Exception` or raw text.

Until P14 lands, adapt the current store with one locked/atomic method that accepts the typed outcome and renders a bounded fixed compatibility string only for obsolete fields. Do not attempt P14's full state-machine redesign in P13.

## Browser handoff

P19 consumes:

- `code`, `category`, `retry`, and optional clamped `Retry-After` for control flow.
- `title`/fixed `detail` only for display.
- Job status/version from P14, never inferred from HTTP problem text.
- Nonretryable provider capability/config/auth failures stop without polling or resubmission.
- Retryable status-monitor transport failures pause/resume the same job rather than creating a new one.
- Explicit cancel remains separate from aborting browser fetch.

No browser logic parses provider names, exception text, `temperature`, LDAP phrases, or human-readable messages.

## Logging and metrics

Log a terminal failure once at its owning workflow/job boundary. Lower layers may log state transitions at debug/trace but must not repeatedly log the same full failure.

Safe structured fields:

```text
failure.code
failure.category
failure.origin
failure.retry
request_id/job_id (opaque and already scoped)
dependency kind
fixed outcome
elapsed bucket
```

Never log raw query/context/prompt/plan/provider body/API key/header, LDAP value/DN, CSV cell, filesystem path, or public rendered detail as a structured value. P16 later owns sinks/redaction; P13 supplies the safe event contract.

Metrics:

```text
adquery.failure
adquery.cancellation
adquery.problem_response
adquery.retry_disposition
```

Tags are fixed code/category/origin/retry and route family. Never tag IDs, model names from user input, values, messages, or paths.

## Deterministic tests

Use fake `TimeProvider`, controllable tokens/tasks, fake HTTP handlers, P09 scheduler fakes, and fake artifact/compiler services. No live provider, LDAP, IIS, sleeps, or wall-clock races.

### Descriptor and registry

1. Every dependency code has exactly one registry entry; duplicate/unknown code fails startup.
2. Every registry entry has a valid HTTP/category/retry/title/safe-argument schema.
3. Descriptor serialization is versioned, deterministic, bounded, and contains no exception/message/body fields.
4. `OperationResult<T>` cannot represent both/neither success and failure.
5. Local exception capture cannot be serialized or assigned to a job model.
6. Representative secret/query/provider/LDAP/path strings never appear in descriptor, problem JSON, job snapshot, or safe logs.

### Cancellation races

7. Caller, explicit job cancel, P06 deadline, and host tokens each produce their distinct internal cause; the P06 deadline publishes `query_budget_exceeded` with dimension `execution_time`.
8. Controlled simultaneous token and dependency-timeout signals use one `TerminalStopClaim` slot, preserve the first atomic claim, and cannot both win.
9. Unattributed `OperationCanceledException` maps to an internal invariant failure, not caller cancellation.
10. Caller disconnect before response yields no attempted problem body or 408.
11. P06 deadline racing LDAP/provider timeout preserves whichever cause claimed the shared slot first; HTTP and stored job outcomes use that same winner.
12. A timed-out/detached P09 worker remains physically occupied while P13 returns the typed timeout.
13. Late completion/fault after a returned cancellation/timeout is observed once and cannot overwrite the result.
14. Cancellation registrations/deadline sources dispose exactly once on every exit.

### Provider and dependency classification

15. Missing config, 401/403, deprecated-field 400, other 400, 429, timeout, 5xx, connection, and malformed-success fixtures map to exact codes.
16. The reported Vertex deprecated-`temperature` envelope maps to `provider_capability_mismatch`, 502, `never`, with none of its body/parameter text in public output.
17. Invalid/malicious `Retry-After` values are rejected/clamped to 1–300 seconds.
18. Every P09 typed exception maps one-to-one and retains parent cancellation precedence.
19. P04 not-found/ambiguous remains ordinary typed data outcome; operational failure is terminal and atomic.
20. P12 diagnostic order/codes survive wrapping; no renderer prose controls behavior.
21. P07 export failure preserves canonical completion; canonical publication failure produces no success.

### HTTP and job surfaces

22. Every registered code produces golden problem JSON, headers, content type, status, and encoded size.
23. Authentication challenge, forbidden, not-found, validation, dependency, capacity, budget, and internal fixtures expose no sensitive detail.
24. Error after response start aborts rather than appending JSON.
25. Sync and queued equivalent failures expose the same descriptor code/category/retry.
26. Explicit user cancel, P06 execution-time budget, host stop, and dependency timeout produce distinct job outcomes.
27. No job field persists `ex.Message` or provider/LDAP/plan prose.
28. Late failure cannot overwrite completed/cancelled terminal outcome under the temporary atomic adapter.
29. Browser contract tests drive behavior from code/retry only and ignore title/detail changes.

## Red/green guard proof

For every test-bearing slice:

1. Add the focused test and confirm current behavior fails where applicable.
2. Implement the smallest slice and confirm success.
3. Temporarily restore/bypass the protected behavior.
4. Confirm the focused guard fails for the intended reason.
5. Restore and run P01's canonical verification.
6. Commit only the restored slice.

Mandatory mutations:

- Restore raw provider body in `ErrorMessage`; secret/body sanitization guard fails.
- Map deprecated `temperature` 400 to generic client BadRequest or retryable failure; provider fixture fails.
- Map P06 dimension `execution_time` to a separate deadline code or an LDAP/provider timeout; the `query_budget_exceeded`/`execution_time` guard fails.
- Let a later cancellation signal overwrite the first; race guard fails.
- Treat P09 operation timeout as job cancellation; job-cause guard fails.
- Return 408 on `RequestAborted`; disconnect/no-write guard fails.
- Swallow typed LDAP/CSV terminal failure into null/partial rows; atomicity guard fails.
- Persist `ex.Message`; job/log serialization guard fails.
- Parse a display title for retryability; browser contract guard fails.
- Append problem JSON after download headers; response-start guard fails.

Leave no mutation in the worktree.

## Implementation slices

Each slice is one commit and must pass focused and canonical verification before the next.

### Slice 1 — Failure descriptors and registry

Commit intent: `feat: define stable failure contracts`

- Add descriptor/category/origin/retry/code registry, safe arguments, operation result, local causal capture, serialization, and startup validation.
- Add exhaustive registry, immutability, size, and secret-sanitization tests.

### Slice 2 — Cancellation provenance

Commit intent: `feat: preserve cancellation causes`

- Add request/attempt cancellation context, first-cause arbitration, injected time, deadline integration, and disposal.
- Add deterministic token/timeout/completion race tests.

### Slice 3 — Provider failures

Commit intent: `refactor: classify provider failures`

- Adapt P02 gateway results and cancellation.
- Remove raw response bodies from public/logged error strings.
- Add error-envelope, deprecated-field, timeout, retry-after, and redaction tests.

### Slice 4 — LDAP, compiler, CSV, and artifact adapters

Commit intent: `refactor: preserve typed dependency failures`

- Map P09/P12/P04/P07 causes once.
- Remove broad catches that swallow terminal failures.
- Preserve explicit ordinary lookup outcomes and atomic no-partial behavior.
- Add cross-plan golden and zero-publication tests.

### Slice 5 — Problem-details HTTP boundary

Commit intent: `feat: standardize query error responses`

- Add registry-backed problem factory/middleware or endpoint filter.
- Migrate synchronous query, validation, CSV, preview, download, and configuration-safe routes.
- Handle disconnect/no-response and response-already-started cases.
- Add golden HTTP/auth/size/header tests.

### Slice 6 — Typed background job outcomes

Commit intent: `refactor: store typed query failures`

- Add typed outcome fields and temporary atomic store adapter.
- Distinguish explicit cancel, P06 execution-time budget, shutdown, dependency, and unexpected failures.
- Remove `ex.Message` persistence and string-driven behavior.
- Add sync/queued parity and terminal-race tests.

### Slice 7 — Browser contract and cleanup

Commit intent: `refactor: consume stable query failures`

- Update current browser error handling to use code/retry fields without implementing P19's polling redesign.
- Remove legacy error-message parsing and redundant translation catches.
- Document P14/P19/P20 handoffs and add static/browser contract tests.

Verification:

```powershell
pwsh -NoLogo -NoProfile -File scripts/verify.ps1
```

## Acceptance criteria

- Every expected terminal failure has one immutable stable descriptor.
- No control flow parses exception/provider/diagnostic/display messages.
- Caller disconnect, explicit job cancel, P06 query deadline (`query_budget_exceeded`/`execution_time`), dependency timeout, and host stopping remain distinct.
- First-cause arbitration and late-completion behavior are deterministic and tested.
- P09 physical worker occupancy remains truthful after P13 returns timeout/cancellation.
- Typed budget, LDAP, compiler, CSV, and artifact failures are never swallowed into partial success.
- The Vertex deprecated-`temperature` response is sanitized, nonretryable, and classified as an upstream capability mismatch if it bypasses P02 prevention.
- Public problems are versioned, registry-backed, bounded, and contain no sensitive/raw details.
- Retry disposition and `Retry-After` are finite, typed, and independent of display text.
- Caller disconnect does not attempt a 408/problem response.
- Errors after response start abort safely.
- Jobs store typed sanitized failure/cancellation values, never `ex.Message`.
- Sync and queued equivalent causes expose identical codes/category/retry.
- P19 can implement behavior using code/retry/status/version only.
- Canonical verification and every red/green proof pass.
- Each implementation slice is committed separately.

## Rollback

Use new revert commits; do not rewrite history.

- Revert consumers before descriptor/registry contracts.
- Keep provider/body redaction even if problem-details migration rolls back.
- Keep P04/P06/P07/P09 no-partial and typed lower-level causes; do not restore swallowing catches.
- If typed job fields roll back temporarily, render fixed bounded compatibility text from the descriptor and keep the descriptor in active memory; never restore `ex.Message` persistence.
- Do not map deadline/timeout/shutdown to user cancellation to regain old UI behavior.
- Revert problem routes and golden tests together.
- P14/P19 implementations must preserve any P13 codes already consumed; coordinate a versioned contract change rather than silent rename.

## Risks

- **One registry can become a dumping ground.** Require a dependency owner, fixed mapping, safe argument schema, and exhaustive startup/test registration for each code.
- **HTTP status cannot encode every cause.** Stable code/category/retry carry machine semantics; status remains a broad transport class.
- **First cancellation signal depends on callback scheduling.** Atomic claim is the observable contract; deterministic tests control ordering and no later rewrite is allowed.
- **A client disconnect cannot receive explanation.** Do not waste work writing to a dead connection; record safe metrics/logs.
- **Provider classification may depend on body parsing.** Parse only bounded known envelopes into fixed codes, discard the body, and fall back safely.
- **Retryable does not mean idempotent automatic retry.** P14/P19 still own explicit attempt/session behavior.
- **Unexpected exception logging can leak via messages.** Log safe event fields; P16 later centralizes redaction/sinks.
- **Temporary job adapter is not P14 atomicity.** Limit it to typed outcome storage and preserve the residual race explicitly until P14.
- **Problem payload size can grow with validation diagnostics.** Apply encoded-size and count caps before serialization.
- **Export response may already be committed.** Abort transport; never mix problem JSON with file bytes.
- **P20 health has different exposure.** P20 consumes sanitized reason codes and cached state, not general detailed problems.

## Open owner decisions

### Decision 1 — Cancellation precedence

Choose first-observed atomic cause or a fixed priority that can rewrite an earlier cause. Recommendation: first observed wins and can never be rewritten; it matches the actual event that stopped work and makes races testable without relabeling a user cancellation as a later timeout.

Blocked until decided: Slice 2.

### Decision 2 — Public error schema

Choose versioned problem details with stable machine fields or preserve route-specific strings. Recommendation: use the registry-backed schema with code/category/retry/request ID; it removes message parsing and keeps sensitive provider/LDAP details private.

Blocked until decided: Slice 5.

### Decision 3 — Retry semantics

Choose a boolean retry flag or the explicit retry disposition enum. Recommendation: use the enum (`never`, same operation, new attempt, delayed, narrow request); a boolean cannot distinguish monitoring recovery, a new job attempt, overload delay, and a query that must be narrowed.

Blocked until decided: Slices 1, 3–7.

### Decision 4 — Host-shutdown job outcome

Choose `Failed(service_stopping)` until P14 adds recovery, or misclassify shutdown as `Cancelled`. Recommendation: record a distinct retryable failed/interrupted descriptor now; P14 can later add an `Interrupted` state without losing provenance.

Blocked until decided: Slice 6 and P14 transition design.

## Advisory Review

### Round 1 — 2026-07-21

**Reviewer:** Headless Claude Code 2.1.217 / configured model / maximum effort

**Verdict:** Revisions required

- Reconciled the query-deadline contract with accepted P06/P09 semantics: every P06 dimension, including `execution_time`, remains `query_budget_exceeded`/422/`narrow_request`; P13 no longer invents a conflicting `query_deadline_exceeded` code.
- Replaced separate-sounding cancellation and dependency-timeout arbitration with one atomic `TerminalStopClaim` slot shared by token callbacks, provider timeouts, and LDAP timeouts. The first claim controls HTTP and job outcomes and blocks late completion publication.
- Reclassified invalid local provider configuration as `internal`, defined P13's `plan_invalid` aggregate over P12 diagnostics, and blocked LDAP registry wiring on P09's finalized owned taxonomy.

### Round 2 — 2026-07-21

**Reviewer:** Headless Claude Code 2.1.217 / configured model / maximum effort

**Verdict:** Accepted

- Confirmed all P06 dimensions, including `execution_time`, retain `query_budget_exceeded`/422/`narrow_request` and the safe dimension argument preserves deadline provenance.
- Confirmed cancellation callbacks and dependency timeouts share one atomic `TerminalStopClaim`; the single winner governs both HTTP and stored job outcomes and blocks late completion.
- Confirmed caller disconnect, response-start handling, typed no-partial failures, deprecated-`temperature` classification/redaction, P04/P07/P09/P12 handoffs, pre-P14 job boundaries, deterministic guards, rollback, and decision gates are implementable without an invented contract.
- Applied optional precision by correcting P13/P12 and P04/P05 ownership labels, enumerating P07's finalized failure codes and retry mappings, and adding a mandatory mutation for P06 execution-time mapping.

Record no more than three headless Claude review rounds. Each round must identify material findings, the resulting revision or retained disagreement, and the reviewer's final assessment.
