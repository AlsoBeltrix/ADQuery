# P20 — Separated Liveness, Readiness, Deployment, and Diagnostics

Status: **Draft — implementation is not authorized**

Owner approval: Pending

Implementation dependencies: P01, P02, P06, P07, P09, P12, P13, P14, P16, and P17 must land first. P15 may land its non-probe deployment foundations in parallel, but production promotion remains blocked until P20's release-aware deployment contract and P15's matching hard-probe/rollback-drain consumer both land. Consume predecessor types and fixed failure codes; do not recreate their policy, scheduler, storage, or error-classification logic.

Review status: Round 3 accepted the independently audited repairs with no material findings. Two optional precision edits were applied afterward and were not independently re-reviewed; no fourth advisory round is permitted.

## Problem

The application has one misleading health surface instead of distinct operational contracts. `GET /health` runs an external model generation and an in-process plan validation during the request. A liveness poll can therefore spend provider tokens, wait on dependencies, fail because of a model capability change, and add more work while the service is already overloaded. It does not prove that the durable job, artifact, feedback, storage, scheduler, or deployment state is safe for traffic.

The adjacent `GET /api/query/health` route is authorized like an ordinary query route and returns exact timing, parsing state, last-success time, and raw provider error text. Both health implementations catch and log exception objects/messages. Neither route supplies an immutable release identity, so P15 cannot prove that IIS is serving the package it just promoted or safely target the new release for the mandatory rollback drain.

The current routing also permits false positives. The legacy deploy script probes an inferred HTTP URL, follows redirects, treats health failure as a warning, and probes the application root. `MapFallbackToFile("index.html")` can turn an unreserved removed health path into a successful SPA response. A safe replacement needs explicit routes, constant-time reads, bounded background dependency observations, strict schemas, least-privilege exposure, and deterministic failure behavior.

Repository evidence before implementation:

- `csharp/Program.cs` registers `ClaudeHealthCheck` and `OrchestratorHealthCheck`, maps only `/health`, applies the fallback authorization policy, and later maps the SPA fallback.
- `csharp/Services/ClaudeHealthCheck.cs` invokes `IClaudeService.CheckHealthAsync`, returns exact duration/last-success/parsing fields, and stores exception type/message in health data.
- `csharp/Services/ClaudeService.cs` implements health by calling the normal generation path with a synthetic prompt. The reported Vertex error therefore also breaks health: `temperature` is deprecated by the configured model. P02 removes that field by default; P20 removes generation from request-time health entirely.
- `csharp/Services/OrchestratorHealthCheck.cs` constructs a mutable test plan on every poll, calls `IDirectoryPlanExecutor.ValidatePlanAsync`, returns selected validation messages, and logs exception objects.
- `csharp/Controllers/QueryController.cs` exposes `GET /api/query/health` to the normal query role, returns raw `ErrorMessage`, and reports HTTP 200 for some unhealthy outcomes.
- `csharp/appsettings.json` contains a legacy `HealthChecks:CheckIntervalSeconds` value that no current coordinator consumes.
- `csharp/deploy.ps1` probes the application root and `/health`, follows default redirect behavior, and continues after the health warning. P15 retires this workflow and owns all deployment retry/deadline/TLS behavior.
- The application fallback serves `index.html` for otherwise unmatched GET routes, so retired operational routes must be explicitly reserved as failures.

## Admitted findings and one-commit ownership

Each admitted finding is closed by exactly one implementation commit. Tests, documentation, and migration for that finding travel in the same commit. Do not fold a newly discovered concern into one of these commits: record and approve a separate finding first.

| Finding | Severity | Defect | Sole implementation commit |
| --- | --- | --- | --- |
| P20-F1 | High | The combined request-time `/health` makes liveness depend on provider generation and plan validation, amplifies load, and has ambiguous auth/fallback behavior. | `refactor(health): separate constant-time live and ready probes` |
| P20-F2 | High | No bounded authoritative snapshot combines lifecycle, durable stores, compiler, provider, and LDAP state; repeated dependency checks can overlap/self-amplify stuck LDAP work, while asynchronous-only local change hints can leave a stale ready snapshot after drain, stop, or store/invariant failure. | `feat(health): aggregate bounded dependency readiness` |
| P20-F3 | High | Ordinary query authorization exposes provider errors and exception-derived diagnostics; there is no closed, separately protected diagnostic contract. | `security(health): protect bounded diagnostics` |
| P20-F4 | High | There is no immutable serving-release identity or atomic deployment response, so P15 can accept the wrong process and cannot safely target a post-start rollback drain. | `feat(health): expose release-aware deployment probe` |

## Goals

1. Make process liveness a constant-time, dependency-free response.
2. Make traffic readiness a conservative result derived from one immutable, bounded, nonblocking state snapshot.
3. Synchronously invalidate that snapshot before any local readiness-critical transition becomes visible, and prevent an asynchronous stale projection from clearing the invalidation.
4. Move all provider and LDAP I/O to tracked, single-flight background probes with finite deadlines and no request-path work.
5. Reuse P02 request construction, P13 provider classifications, P06 execution context, and P09's one global scheduler.
6. Compile P12's fixed local self-test fixtures once at startup, never per health request.
7. Consume P07, P14, P16, and P17's committed in-memory projections without opening files or SQLite from an endpoint.
8. Return no diagnostic body from unprivileged liveness/readiness routes.
9. Expose a bounded, closed diagnostic document only to a dedicated diagnostics policy.
10. Expose an atomic authenticated deployment document with the immutable serving release ID on both ready and unready responses.
11. Prevent removed health routes from falling through to the SPA or another success response.
12. Keep health logs and metrics fixed-cardinality and free of queries, provider bodies, model identifiers, LDAP values, identities, paths, secrets, and exception text.
13. Prove schemas, caching, invalidation linearization, staleness, concurrency, scheduling, privacy, authorization, and P15 compatibility without live IIS, LDAP, or provider access.

## Non-goals

- Do not repair provider request compatibility here. P02 owns omission of `temperature`, request DTOs, and provider selection.
- Do not create a second provider client, retry policy, error parser, model-name capability table, or health-only sampling option.
- Do not redesign P06 budgets or bypass P09's queue, workers, physical-occupancy accounting, or native timeout policy.
- Do not compile or execute an arbitrary plan supplied by a health caller.
- Do not make health endpoints inspect SQLite, enumerate files, calculate free disk space, acquire leases, validate configuration, or scan artifacts/feedback on demand.
- Do not expose a raw exception, provider response, validation message, model ID, configured endpoint, LDAP root, DN/filter/value, storage path, user/group name, SID, query/job/artifact/feedback ID, or secret.
- Do not turn health into a general administration API, configuration dump, metrics scrape, raw dependency test, or operator drain endpoint.
- Do not let P20 choose deployment URI, credentials, redirects, TLS validation, consecutive successes, deadlines, rollback phases, or IIS behavior. P15 owns them.
- Do not infer release identity from a request header, environment variable, physical path, current directory, mutable configuration, assembly version, process ID, or clock at runtime.
- Do not preserve `/health` or `/api/query/health` as a compatibility alias. This repository is not in production; fail closed instead of perpetuating an unsafe contract.
- Do not add Kubernetes-specific startup semantics or assume a particular load balancer. The HTTP contracts remain platform-neutral and P15 consumes the deployment surface explicitly.

## Dependency and ownership boundaries

### P01 — verification

P01 supplies the one C# test project and `pwsh -NoLogo -NoProfile -File scripts/verify.ps1`. P20 extends that entry point only through ordinary project/test inclusion. Tests use `WebApplicationFactory`, deterministic fakes, `TimeProvider`, barriers, and captured outbound requests. The canonical verifier never contacts IIS, LDAP, Vertex, Anthropic, or any other live service.

### P02 and P13 — provider request and failure contract

P20's provider probe uses P02's one typed request builder/gateway and the configured primary provider/model snapshot. It sends a code-owned non-user prompt with a schema-fixed output cap of eight tokens, uses P02's default sampling behavior, and therefore omits `temperature`. It treats a structurally valid provider success as connectivity success and discards content without plan parsing.

P13 remains the only classifier of provider failures. P20 consumes these exact codes without parsing messages or bodies:

```text
provider_configuration_invalid
provider_authentication_failed
provider_capability_mismatch
provider_request_rejected
provider_rate_limited
provider_timeout
provider_unavailable
provider_protocol_invalid
```

The Vertex deprecated-`temperature` envelope, if it somehow survives P02, becomes only `provider_capability_mismatch`. The response body, parameter name, provider message, exception, and model identifier never enter a health snapshot, response, log, or metric.

### P06 and P09 — directory probe

P20 creates an ordinary explicit P06 execution context dedicated to one readiness attempt. It has one directory-operation unit and a finite active deadline derived from P09's already validated maximum logical queue-plus-operation wait. It carries no owner, query, ambient request, or unlimited exception. P09 still charges the operation only when its worker claims it.

The readiness attempt performs one RootDSE connectivity read through P09's singleton FIFO scheduler with operation kind `readiness`. It bypasses only the normal success cache so it tests current connectivity; an explicitly configured `ActiveDirectory:RootPath` must not suppress this P20 probe even though it suppresses ordinary default-naming-context discovery. The probe does not bypass admission, create a reserved worker, add a priority lane, start a replacement, or perform a subsequent directory search. The returned naming-context value is validated as present and then discarded without logging or storage.

P20 consumes P09's exact seven failure codes:

```text
ldap_queue_saturated
ldap_queue_timeout
ldap_operation_timeout
ldap_provider_timeout
ldap_scheduler_stopping
ldap_scheduler_faulted
ldap_dependency_failure
```

P09 must expose one narrow internal readiness adapter plus an O(1) immutable scheduler projection. For every admitted readiness work item, the adapter also exposes an opaque non-serializable retirement signal that completes only after the item no longer occupies the FIFO/worker and worker-owned cleanup finishes. After a queue timeout, caller detachment, or operation timeout, P20 latches the directory component unready and submits no replacement probe until that exact retirement signal completes. Host stop may cancel P20's wait but never the underlying P09 ownership. Immediate saturation creates no admitted item and may follow the normal failure-retry schedule. At most one P20 directory item can be queued, running, detached, or awaiting retirement.

The scheduler projection contains only fixed state and counts/buckets: lifecycle, configured capacity, queue occupancy, physical-active count, detached-active count, and a monotonic source generation. It contains no LDAP input. Before P09 publishes scheduler stopping/faulted, a full queue, or every physical worker detached, its readiness adapter synchronously invokes P20's local invalidation protocol with the reserved next source generation. Ordinary finite activity does not require an empty queue.

### P07 — result artifacts

P07 owns artifact-root lease, recovery, integrity, reservations, and capacity. P20 reads a P07-published immutable readiness projection; it never opens an artifact or its root. Before a healthy P07 state publishes lost ownership, exhausted global capacity, global corruption, or store unavailability, the adapter synchronously invalidates the `artifacts` fence with its reserved next source generation. `artifact_root_in_use`, `artifact_store_full`, and `artifact_corrupt` remain P07 codes and make readiness false. Request-specific size/busy/not-found/forbidden results do not become global readiness reasons unless P07's authoritative projection says the store itself is unavailable.

### P12 — compiler self-test

P12 supplies one code-owned valid fixture and one code-owned invalid fixture with its expected fixed diagnostic codes. P20 runs both once after the immutable policy/compiler snapshot exists. Success requires the valid fixture to compile and the invalid fixture to fail with the exact expected P12 codes. The test never invokes provider, LDAP, P07, or a job workflow and never serializes a plan into diagnostics. An unexpected result maps only to `compiler_self_test_failed` and remains unready until restart.

### P14 — service lifecycle and durable job state

P14 publishes an immutable O(1) projection after committed startup recovery, service-mode, capacity, receipt-invariant, and shutdown-state changes. P20 never queries SQLite. Within P14's existing state serialization point and before `Draining`, `Stopping`, unsafe reserve, store failure, receipt invariant failure, or invalid shutdown state becomes externally visible, P14 reserves its next projection generation and synchronously invalidates the `jobs` fence. Only `Accepting` with completed recovery, safe terminal reserve, a usable store, and valid shutdown bounds can be ready. `Draining` maps to `job_service_draining`; `Stopping` maps to `service_stopping`; store unavailable/capacity/invariant failures map to fixed job-service reasons. P20 does not change admission or resume drain.

P14's projection and P20's readiness are truthful rather than anticipatory: `ready=true` means the process is eligible to accept normal work now. It never means merely “dependencies look good while admission remains closed.” This definition is required by P15's post-start rollback-drain repair.

### P15 — deployment consumer

P15 supplies the canonical release ID, package manifest, explicit `ProbeBaseUri`, production credential, TLS/redirect policy, three-consecutive-observation rule, two-second spacing, 60-second monotonic deadline, deployment journal, and IIS rollback. P20 supplies only the project metadata emission, strict runtime parser, and endpoints.

Release identity has one exact producer/consumer boundary, now recorded in committed P15 amendment `1c42c69`. P20-F4 owns the conditional project `AssemblyMetadata` emission and immutable runtime parser. P15-F8/Slice 3, commit intent `feat(deploy): verify immutable package identity`, owns generating the canonical ID before publish, passing checked MSBuild argument `AdQueryReleaseId=<release_id>`, reading the published entry assembly metadata offline without loading/executing it, and requiring exactly one matching manifest value before archive acceptance. P15's required mutation omits or substitutes that property and must fail the published-assembly/manifest test before archive creation. P20 does not duplicate that generator, offline PE reader, manifest comparison, or guard.

The committed P15 rollback contract is mandatory: once `PoolStartRequested` is durable, any rollback mutation first parses P20's deployment response on `200` or `503`, requires the exact new release ID, and asks P14 to prove that release non-accepting and quiescent. An unavailable, malformed, unauthorized, or wrong-release response cannot establish authority and leads P15 to `RecoveryRequired` without stopping the pool or switching path/projection.

P20 does not implement P15's polling loop or return a suggested retry delay. P15 does not call the protected diagnostics endpoint or interpret component reason codes. It consumes the deployment envelope as one atomic liveness/readiness/identity observation and records only its own fixed status classes.

### P16 — configuration, startup, storage, logging, and authorization binding

P16 remains the only configuration catalog/binder and supplies the process-lifetime immutable options model. P20 adds its `HealthOptions` rows to `ApplicationConfigurationCatalog`; it does not inject raw `IConfiguration`, use `IOptionsMonitor`, invent aliases, or silently clamp values. Remove the unused legacy `HealthChecks` section without a migration alias because the application is not yet in production.

P16 publishes a nonblocking runtime projection after typed configuration validation, DataRoot marker/lease acquisition, storage-capacity checks, final logger initialization, and controlled shutdown changes. Before it publishes lost root ownership, exhausted required storage headroom, logger failure, or host stopping, its adapter reserves the next projection generation and synchronously invalidates the `runtime` fence. P20 receives only fixed reasons and a capacity bucket, never values or paths. A failure before listener promotion remains a safe startup failure in host logs; P20 does not start a diagnostic listener around invalid secrets or storage.

P16's `AuthorizationPolicyOptions` is extended with bounded nonempty production role lists for `DeploymentOperators` and `HealthDiagnosticsReaders`. P20 defines the policy semantics and P16 validates/binds the configured Windows roles. The ordinary query-user role and `AllowAllWindowsUsers` never satisfy either policy by fallback. No header token, query token, embedded credential, or health-specific secret is added.

### P17 — feedback writer

P17 publishes an immutable projection after writer-lease, recovery, schema, integrity, capacity, and write-state changes. Before it publishes lost writer ownership, schema/integrity failure, exhausted global capacity, or global write unavailability, its adapter reserves the next projection generation and synchronously invalidates the `feedback` fence. P20 never scans feedback files. `feedback_store_in_use`, `feedback_store_corrupt`, `feedback_schema_unsupported`, `feedback_store_full`, and an authoritative global write-unavailable state make readiness false. Request-specific validation, consent, duplicate, or target-not-found results do not.

## Endpoint and exposure contract

Map exact endpoints through one `MapOperationalHealthEndpoints` composition helper before the SPA fallback. All health responses set `Cache-Control: no-store, max-age=0`; none emits an ETag. A small middleware keyed by endpoint metadata applies this header before authorization so 401/403 responses cannot be cached. Global HTTPS behavior remains in force; P20 adds no HTTP exception and no redirect.

| Method and route | App authorization | Status and body | Intended consumer |
| --- | --- | --- | --- |
| `GET` or `HEAD /health/live` | Explicit `AllowAnonymous` at the ASP.NET policy layer | Always `204 No Content` once the server can dispatch the handler; no body and no dependency/state read. | Process supervisors and basic reachability checks. IIS may still enforce its site-level authentication policy. |
| `GET` or `HEAD /health/ready` | Explicit `AllowAnonymous` at the ASP.NET policy layer | `204 No Content` only for a fresh ready snapshot; otherwise `503 Service Unavailable`; no body in either case. | Traffic routing without diagnostic disclosure. |
| `GET /health/deployment` | `DeploymentProbe` policy | Closed JSON v1; `200 OK` when traffic-ready, `503 Service Unavailable` when live but unready. Both bodies carry the exact serving release ID. | P15 only. |
| `GET /health/diagnostics` | `HealthDiagnostics` policy | Closed JSON v1 and `200 OK` when retrieval succeeds, regardless of readiness. Overall/component state is inside the document. | Authorized operators. |

`/health/deployment` and `/health/diagnostics` return framework/P13-safe 401 or 403 behavior without any snapshot body. The two authorization policies require an authenticated Windows principal and at least one role from their respective validated lists. The production deployment credential is granted `DeploymentProbe` and P14 drain authority, not diagnostics authority. Diagnostics membership is managed separately.

Map an all-method `/health/{**remainder}` terminal endpoint after the four exact routes and an exact all-method `/api/query/health` terminal endpoint; both return bodyless `404 Not Found`. This reserves `/health`, misspellings, extra segments, and the retired controller route so `MapFallbackToFile` cannot return the SPA with status 200. Unsupported methods on the four exact routes fail with 404/405 and never reach the SPA. Do not add redirects, aliases, query-string modes, content-negotiated variants, or verbose response switches.

Liveness is deliberately narrower than readiness. The liveness handler does not resolve the snapshot publisher, `TimeProvider`, authorization service, provider, compiler, scheduler, store, logger, or configuration. If it can execute, the process is live. Host startup failures remain an unavailable listener; host stopping makes readiness false before the listener disappears.

## Deployment response v1

The deployment endpoint serializes one immutable read of the release identity and readiness snapshot. It performs only a monotonic freshness comparison against the snapshot's precomputed validity deadline. The exact JSON shape is:

```json
{
  "schema_version": 1,
  "release_id": "20260721T220000Z-91bfcbffb090",
  "live": true,
  "ready": false,
  "readiness_generation": 42
}
```

Contract rules:

- The response is `application/json; charset=utf-8`, source-generated, closed to extra fields, and at most 512 encoded bytes.
- `schema_version` is numeric `1`. P15 rejects every other version; P20 never silently changes v1 meaning.
- `live` is always `true` because a response from this exact handler proves dispatch. It is not copied from a mutable dependency flag.
- `ready` is the same traffic-readiness truth used by `/health/ready` at the instant of this snapshot read and freshness comparison.
- `readiness_generation` is a nonnegative checked process-local generation used only to show internal coherence. P15 neither requires it to change nor treats it as release identity.
- `200` requires `live=true` and `ready=true`. `503` requires `live=true` and `ready=false`. A `503` body remains authoritative for release targeting but never qualifies as a successful P15 observation.
- No reason code, component state, timestamp, retry suggestion, URI, credential, model, path, or process identifier appears.

## Immutable release identity

P15 generates one canonical release ID before publish using its approved grammar:

```text
^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{12}$
```

P20-F4 conditionally emits the supplied MSBuild property as exactly one project `AssemblyMetadata` entry named `AdQueryReleaseId`, then reads and validates the entry once during startup into an immutable, non-configurable `ReleaseIdentity`. P20 does not generate the production ID, invoke publish, inspect a package, or compare a manifest. P15-F8 owns all four consumer actions: generate the ID before publish, pass the checked property, inspect the published assembly offline, and require exact manifest equality before archive acceptance. Missing/duplicate/malformed metadata fails P15 packaging and non-Development startup; manifest mismatch is exclusively a P15 packaging failure.

Every non-Development environment requires the production grammar and fails before listener promotion when identity is absent or invalid. Development may use the literal `development` only when metadata is absent; that sentinel does not satisfy P15's production grammar. Tests inject a validated identity through the composition seam. No production configuration or request can override the assembly value, and the ID is never an authorization token.

The build metadata proves which package binary is executing only when paired with P15's clean-source, file-manifest, package-hash, immutable-directory, and path/read-back contract. P20 does not claim assembly metadata is a signature or supply-chain attestation.

## Readiness state model

Register one singleton publisher holding a deeply immutable `ReadinessSnapshot`. Suggested names may adapt to landed conventions, but the contract is fixed:

```text
ReadinessSnapshot
  Generation: long
  PublishedAtUtc: DateTimeOffset
  ReadyThroughTimestamp: long
  LocalSafetyEpoch: long
  LocalUnsafeMask: fixed component flags
  Components: fixed immutable array in canonical order

ComponentObservation
  Component: runtime | compiler | artifacts | jobs | feedback | provider | directory
  Condition: healthy | unhealthy | unknown | stale
  Reason: fixed HealthReasonCode or none
  ObservedTimestamp: long
  LatencyBucket: fixed enum
  CapacityBucket: fixed enum
  FailureBucket: fixed enum

LocalInvalidationToken (internal, non-serializable)
  Component
  SafetyEpoch
  RequiredSourceGeneration
```

The canonical component order above is part of diagnostics v1. The array always has exactly seven entries; sources cannot register arbitrary names. `LocalSafetyEpoch` and `LocalUnsafeMask` are internal fence state and are never serialized. Every process starts at generation zero with all components `unknown`, overall readiness false, and reason `health_not_observed`. One private publisher gate serializes bounded in-memory component/fence changes and publishes the complete new snapshot with `Volatile.Write`. Endpoints perform one `Volatile.Read`; they never acquire the gate or observe a partially updated array.

### Synchronous local invalidation fence

A capacity-one hint is insufficient for a healthy-to-unsafe local transition: the coordinator can be blocked while an old snapshot continues to return 204. Every readiness-critical local owner therefore uses one exact protocol. This applies to P14 drain/stop/store/reserve/invariant changes; P07/P16/P17 ownership, integrity, capacity, and global store failures; P09 scheduler stopping/faulted/full/all-detached transitions; and host stop. Compiler state is immutable after its startup self-test, and provider/RootDSE results already publish directly through the same state publisher.

1. Under its existing state serialization point, the owner checked-reserves a strictly increasing `SourceGeneration` for the transition before making the unsafe state externally visible. It calls `InvalidateLocal(component, fixedReason, requiredSourceGeneration)` synchronously. A source-generation overflow invokes the same permanent fail-closed publisher state; no arbitrary value crosses this seam.
2. Under the publisher gate, `InvalidateLocal` checked-increments `LocalSafetyEpoch`, latches that component's bit/token/required generation, replaces its component observation with the fixed unhealthy reason, forces aggregate readiness false with an already-expired `ReadyThroughTimestamp`, and `Volatile.Write`s the whole snapshot before returning. A repeat carrying the same or an older required source generation while the same unsafe reason is latched is idempotent: it returns the current token and changes neither epoch nor snapshot. A genuinely newer invalidation while already visibly unhealthy advances only the internal safety epoch/target; public `Generation` increments only when the serialized component condition/reason/bucket or aggregate readiness changes. The method performs no I/O, logging, metric callback, dependency resolution, or await. Safety/public-generation overflow freezes a permanent `health_generation_exhausted` unready snapshot.
3. Only after that call returns may the owner commit or publish the unsafe transition. It atomically publishes its own immutable projection with the reserved `SourceGeneration` and returned `SafetyEpoch` as `AcknowledgedInvalidationEpoch`, then emits the ordinary coalesced hint. If the authoritative transition aborts, the owner reserves a strictly newer source generation and publishes a healthy recovery projection acknowledging the same current safety token; reusing the unsafe projection's generation is invalid. The harmless false-negative remains until asynchronous recovery.
4. A global failure first detected by a failed operation is linearized when the owner classifies/publishes that global state: before returning that classification or exposing it to admission, it reserves the generation and performs the same invalidation. A health read completed before invalidation is ordered before that failure publication; every read after invalidation sees unready.
5. The coordinator reads an owner's projection outside the publisher gate. A healthy candidate may clear a local latch only through `TryPublishLocalRecovery`, which under the gate requires the candidate component to match the slot, `AcknowledgedInvalidationEpoch` to equal the current token, and `SourceGeneration` both to meet/exceed `RequiredSourceGeneration` and to be strictly newer than the last accepted projection. Initial healthy publication uses epoch zero. A missing/mismatched token, older generation, unsafe candidate, or global host-stop latch is rejected and cannot publish ready.
6. Recovery updates only that component against the publisher's current seven slots and recomputes aggregate readiness under the gate; the coordinator never overwrites the whole snapshot it read earlier. External probe results similarly carry their single-flight attempt generation and update only their current component, so a late result cannot overwrite a newer failure or local directory fence.

This is a linearizable fence, not merely a notification. If recovery acquires the gate first, its ready publication is ordered before the unsafe transition and the subsequent invalidation wins. If invalidation acquires first, its new token/generation makes every pre-invalidation recovery candidate fail. If a second invalidation follows a recovery read, the token mismatch rejects that stale recovery. There is no ordering in which the coordinator can publish ready between invalidation and the later unsafe projection.

Subsystem lock ordering is owner state lock then publisher gate. The coordinator reads projections before taking the publisher gate and never takes a subsystem lock while holding it, so the fence adds no lock cycle. No external I/O or durable transaction runs under the publisher gate. Host shutdown invokes runtime invalidation as the first action of P20's pre-registered stopping callback, and P14/P09 still perform their own required invalidation before publishing their stopping states; duplicate invalidation is conservative and bounded.

Each local adapter takes a required non-null `ILocalReadinessInvalidator` constructor dependency. There is no no-op implementation, optional service lookup, catch-and-continue path, or “signal only” fallback in production. Composition/startup tests enumerate the five local component adapters plus the host callback and fail if any unsafe transition lacks the invalidator operation in its exact operation log.

Increment public `Generation` only when a serialized component condition/reason/bucket or aggregate `ready` value changes. An internal-only safety-token/target update preserves the public generation and diagnostic publication time. Use checked arithmetic. Synthetic source/safety/public-generation overflow tests must fail closed to `health_generation_exhausted`; never wrap to a prior value. Store timestamps from injected `TimeProvider.GetTimestamp()` for scheduling/freshness and use UTC only for the protected diagnostic snapshot time. Wall-clock adjustments cannot extend readiness.

An endpoint derives its final readiness with one allocation-free monotonic comparison: the stored aggregate must be ready, `LocalUnsafeMask` must be empty, and the current timestamp must be strictly before `ReadyThroughTimestamp`. The mask and aggregate come from the same immutable read. This request-time comparison performs no refresh or dependency work and prevents a stalled background coordinator from serving either a locally invalidated or indefinitely stale success. The coordinator also publishes explicit stale state at scheduled expiry so diagnostics and metrics converge even without traffic.

Overall readiness is a strict AND of the seven required components. There is no “degraded but HTTP 204” state. Unknown, stale, unhealthy, unmapped enum values, publisher failure, or a component missing from the fixed set is unready. A healthy capacity bucket may be `normal`, `elevated`, or `critical` only while its owner still guarantees one maximum reservation and required reserve; `exhausted` is unhealthy.

## Readiness contributors

Local owners publish immutable current projections with a monotonic source generation and acknowledged invalidation epoch. Unsafe invalidation itself is synchronous and authoritative for fail-closed readiness; only subsequent projection reconciliation uses a hint. The coordinator coalesces those hints through one capacity-one signal and rereads the fixed projections. It never queues one event per update. Positive recovery may therefore lag safely, while no unsafe transition waits for the coordinator. Recomputing the seven small records is O(1) and performs no I/O.

| Component | Healthy requirement | Unready reasons |
| --- | --- | --- |
| `runtime` | P16 bootstrap completed; immutable options valid; DataRoot ownership/capacity and final logger usable; host not stopping; release identity valid. | `health_not_observed`, `configuration_invalid`, `storage_unavailable`, `storage_capacity_exhausted`, `logging_unavailable`, `release_identity_invalid`, `health_probe_faulted`. |
| `compiler` | P12's one startup valid/invalid fixture pair produced the exact expected outcomes. | `health_not_observed`, `compiler_self_test_failed`, `health_probe_faulted`. |
| `artifacts` | P07 recovery complete; exclusive root ownership held; integrity usable; at least one maximum result reservation remains safe. | P07's `artifact_root_in_use`, `artifact_store_full`, `artifact_corrupt`, or a fixed global store-unavailable code. |
| `jobs` | P14 recovery complete; mode `Accepting`; SQLite schema/store/receipt invariants usable; terminal reserve and shutdown bound safe. | `job_service_draining`, `service_stopping`, `job_store_unavailable`, `job_store_capacity_exhausted`, `job_store_invariant_failed`, `job_shutdown_bound_invalid`. |
| `feedback` | P17 recovery complete; outer/child writer ownership held; schema/integrity/write state usable; one maximum event remains admissible. | `feedback_store_in_use`, `feedback_store_corrupt`, `feedback_schema_unsupported`, `feedback_store_full`, `feedback_write_failed`. |
| `provider` | Latest P02/P13 active probe succeeded and remains fresh. | The eight exact P13 provider codes, `health_not_observed`, `health_observation_stale`, or `health_probe_faulted`. |
| `directory` | Latest scheduled RootDSE probe succeeded and remains fresh; P09 scheduler is running and not globally unavailable by its authoritative projection. | The seven exact P09 codes, `health_not_observed`, `health_observation_stale`, or `health_probe_faulted`. |

The P20-owned presentation codes are exactly:

```text
health_not_observed
health_observation_stale
health_probe_faulted
health_generation_exhausted
release_identity_invalid
compiler_self_test_failed
configuration_invalid
storage_unavailable
storage_capacity_exhausted
logging_unavailable
job_service_draining
job_store_unavailable
job_store_capacity_exhausted
job_store_invariant_failed
job_shutdown_bound_invalid
```

`service_stopping`, P07/P09/P13/P17 codes, and any other predecessor-owned code are referenced from their one canonical registries rather than redefined. Implement one explicit enum-to-wire catalog and a test that compares every allowed contributor enum value to that catalog. Do not accept a free-form string from a contributor. An unknown value is internally recorded as `health_probe_faulted`, logs a fixed component/phase/classification only, and keeps readiness false.

When a contributor has multiple simultaneous failures, choose one by fixed priority: ownership/integrity, stopping/draining, required reserve/capacity, dependency failure, stale, then not-observed. Diagnostics never returns an arbitrary list or whichever exception raced last.

## Background dependency coordination

Run provider and directory probing as two independent tracked loops so a detached LDAP call cannot stop provider freshness and a slow provider cannot stop scheduler observation. Both loops begin with an immediate attempt after yielding startup, allow at most one attempt of their kind, schedule from physical/logical completion rather than start time, and use injected `TimeProvider` delays. There is no `Timer`, `Thread.Sleep`, unobserved task, request token, or overlapping retry.

### Provider loop

1. Create a bounded probe request through P02's typed builder for the configured primary route, fixed prompt, and eight-token cap. Never use the normal plan-generation service or P12 parser.
2. Apply `ProviderProbeTimeout` through the landed P02/P13 cancellation/deadline boundary. Host stop wins over dependency classification.
3. On a structurally valid success, discard all generated content and publish healthy. Schedule the next attempt from completion after `SuccessProbeInterval`.
4. On `provider_rate_limited`, retry after the greater of the current failure backoff and P13's validated one-to-300-second `Retry-After`.
5. On `provider_timeout`, `provider_unavailable`, or `provider_protocol_invalid`, publish unready and use the failure backoff. Start at `FailureRetryInitial`, double after each consecutive transient failure through checked arithmetic, cap at `FailureRetryMaximum`, and reset after success.
6. On immutable configuration/auth/capability/request rejection (`provider_configuration_invalid`, `provider_authentication_failed`, `provider_capability_mismatch`, `provider_request_rejected`), publish unready and latch until restart. P16 options do not live-reload, so repeated requests cannot heal the state and would only amplify a permanent fault.
7. On an unexpected exception, preserve causal data only through P13's process-local capture, emit P16's fixed safe event, publish `health_probe_faulted`, and use the failure backoff. Never store the exception object/message in health state.

### Directory loop

1. If a prior readiness item has an incomplete P09 retirement signal, remain unready and await only that signal or host stop. Do not submit another item because a caller-visible timeout did not free physical/queue capacity.
2. Create the one-unit P06 context and submit one cache-bypassing RootDSE read through P09.
3. On success, discard the naming context, publish healthy, and schedule after `SuccessProbeInterval`.
4. On immediate `ldap_queue_saturated`, publish unready and use the failure backoff; no item was admitted.
5. On every admitted timeout/detachment, publish P09's exact code, retain only its opaque retirement signal, and do not start the failure delay until retirement completes.
6. On other retryable directory/dependency failures, publish the exact P09 code and use the same initial/doubling/capped failure backoff independently from the provider loop. Reset after success. `ldap_scheduler_stopping` and `ldap_scheduler_faulted` remain unready without new submissions until the scheduler projection reports a new running generation or the process restarts.
7. Host shutdown cancels P20's logical waits and joins its loops within the host's bound. P09 retains ownership of a physically blocked background worker and its cleanup.

Successful external observations expire exactly `ObservationMaxAge` after their completion timestamp. Starting a replacement attempt does not extend the old observation. Any explicit failure makes readiness false immediately even if an older success has not aged out. The coordinator's freshness timer is scheduled to the earliest required expiration and coalesced with local-change signals.

## Health configuration

Add these process-lifetime values to P16's canonical `Health` / `HealthOptions` registration:

| Option | Recommended checked-in value | Validation |
| --- | --- | --- |
| `SuccessProbeInterval` | 5 minutes | 1–15 minutes. |
| `FailureRetryInitial` | 15 seconds | 5–60 seconds and strictly less than success interval. |
| `FailureRetryMaximum` | 5 minutes | At least the initial delay, no greater than the success interval, and at most 15 minutes. |
| `ProviderProbeTimeout` | 15 seconds | 1–30 seconds; never zero or infinite. |
| `ObservationMaxAge` | 15 minutes | At least two success intervals plus the greater of provider timeout and P09's maximum logical readiness wait; at most 1 hour. |

Use typed `TimeSpan` binding, checked timestamp arithmetic, and `ValidateOnStart`. The directory deadline/queue/operation/native limits remain P09 values and are not duplicated here. Cross-option validation computes the effective configured P09 `MaxQueueWait` plus `OperationTimeout`, proves the chosen `ObservationMaxAge` satisfies the formula, and includes boundary fixtures proving the declared ranges have valid combinations. With P09's recommended 10-second queue wait and 20-second operation timeout, the recommended proof is `2 * 5 minutes + 30 seconds = 10 minutes 30 seconds <= 15 minutes`. At the upper bounds, two 15-minute success intervals plus P09's separately bounded theoretical maximum ten-minute queue-plus-operation wait require at least 40 minutes and remain satisfiable beneath the one-hour ceiling. Response size, component set, output-token cap, schemas, buckets, and reason codes are code contracts, not configuration. Remove `HealthChecks:CheckIntervalSeconds`; an unknown legacy key fails the P16 catalog/package check after the planned migration because silently ignoring it would imply a cadence that is not used.

At the recommended five-minute healthy cadence, one continuously running process makes at most 288 minimal provider probes and 288 RootDSE probes per day, excluding restarts. A continuously transient failure reaches the five-minute cap after five retries and is likewise bounded near that steady-state rate; permanent provider failures latch after one attempt. This external traffic and token cost requires owner approval before implementation.

## Protected diagnostics v1

The diagnostics endpoint reads the same single snapshot as readiness and projects a fixed array. It never calls a contributor. Retrieval itself returns 200 so operators can read an unready state; authorization/transport/serialization failures remain distinct HTTP failures.

```json
{
  "schema_version": 1,
  "live": true,
  "ready": false,
  "readiness_generation": 42,
  "snapshot_at_utc": "2026-07-21T22:05:00.0000000Z",
  "components": [
    {
      "component": "runtime",
      "status": "healthy",
      "reason": null,
      "age_bucket": "not_applicable",
      "latency_bucket": "not_applicable",
      "capacity_bucket": "normal",
      "failure_bucket": "none"
    },
    {
      "component": "provider",
      "status": "unhealthy",
      "reason": "provider_capability_mismatch",
      "age_bucket": "under_1_minute",
      "latency_bucket": "250_ms_to_1_second",
      "capacity_bucket": "not_applicable",
      "failure_bucket": "one"
    }
  ]
}
```

Wire enums are exact lower-snake-case strings selected by explicit switches, never a global serializer naming policy. `components` always contains all seven canonical entries. The body is source-generated UTF-8 JSON with a 4 KiB hard encoded ceiling; exceeding it is a tested internal contract failure, not truncation into invalid JSON.

Fixed bucket sets:

```text
age:       not_applicable | under_1_minute | 1_to_5_minutes | 5_to_15_minutes | stale
latency:   not_applicable | under_250_ms | 250_ms_to_1_second | 1_to_5_seconds | 5_to_20_seconds | over_20_seconds
capacity:  not_applicable | normal | elevated | critical | exhausted
failures:  none | one | two_to_three | four_or_more
```

`snapshot_at_utc` is the publisher time for this immutable state, not the current request time or a dependency's exact last-success time. Exact probe durations, queue counts, worker counts, disk bytes, retry-after values, timestamps per component, model/provider names, endpoints, directory values, storage paths, and release identity remain absent. Release identity belongs only to Slice 4's narrower deployment contract, so Slice 3 is complete and implementable without a later-slice producer.

Remove `QueryController`'s health action, its nested response DTO, `IClaudeService.CheckHealthAsync`, `ClaudeHealthResult`, and all dead health-only parsing/timing state. Remove `ClaudeHealthCheck` and `OrchestratorHealthCheck` in P20-F1. Architecture tests reject `HealthCheckResult` dictionaries, `ExceptionMessage`, `ErrorMessage`, raw exception logging, and ordinary controller health routes.

## Logging and metrics

Use P16's one structured pipeline and P13's local causal capture. Log state transitions, probe-loop start/stop, permanent latch, and unexpected coordinator failure; do not log successful polls or endpoint requests at application level. Event arguments are limited to component enum, prior/new condition, fixed reason, fixed bucket, and fixed probe phase. Do not pass an `Exception` object to Serilog.

Recommended metrics use a fixed `adquery.health` meter:

- readiness gauge with no tags;
- component condition gauge tagged only by the seven component enums;
- probe-attempt counter tagged by `provider|directory` and fixed outcome class;
- probe-duration histogram tagged only by component/result class;
- transition counter tagged by component/condition/reason from the closed catalog.

Do not tag release ID, model/provider ID, endpoint, LDAP value, path, identity, job/request/correlation ID, exception type/message, or exact capacity. Test every emitted tag key/value against a closed allowlist and seed forbidden canaries through all fake failures.

## Implementation sequence

Before each slice, re-ground against the landed predecessor APIs and current worktree. Implement the slices in this order, run the canonical verifier, perform the named mutation proof, restore green, and commit exactly the stated finding. Do not begin the next finding with an uncommitted finished slice.

### Slice 1 — P20-F1 constant-time route separation

**Commit:** `refactor(health): separate constant-time live and ready probes`

- Add the immutable seven-component initial snapshot, atomic publisher/read API, source-generated minimal endpoint contracts, no-store metadata/middleware, and exact liveness/readiness route mappings.
- Explicitly reserve `/health/{**remainder}` so `/health` and unknown children return bodyless 404 before SPA fallback.
- Remove `AddHealthChecks`, `MapHealthChecks`, `ClaudeHealthCheck`, and `OrchestratorHealthCheck`. Do not add a package merely to preserve ASP.NET HealthChecks abstractions.
- Keep the initial application readiness false until Slice 2's required contributors publish healthy; no intermediate commit claims readiness from missing sources.
- Add route/auth/body/status/cache/fallback tests and an endpoint dependency-call counter proving the handlers perform no provider, LDAP, compiler, store, filesystem, configuration binding, or health-logger work.
- Update the operational README to define liveness versus readiness and state that the old deploy script is not a supported consumer; P15 owns its removal.

Guard proof: temporarily resolve and invoke a throwing fake readiness contributor inside the `/health/ready` handler. The zero-dependency-call route test must fail, then restore the one-snapshot-read implementation and verify green.

### Slice 2 — P20-F2 authoritative bounded readiness

**Commit:** `feat(health): aggregate bounded dependency readiness`

- Add validated `HealthOptions` through P16's catalog and remove the legacy `HealthChecks` configuration section.
- Add the fixed contributor adapters for P16 runtime, P12 compiler self-test, P07 artifacts, P14 jobs, P17 feedback, P02/P13 provider, and P09 directory/scheduler state.
- Add the synchronous per-component local invalidation fence, token/source-generation recovery handshake, capacity-one positive-change signal, component-scoped atomic recomputation, monotonic valid-through guard, explicit stale publication, checked generations, and two independent tracked single-flight dependency loops.
- Add P09's narrow cache-bypassing RootDSE readiness operation and per-admitted-item retirement signal without changing scheduler capacity or physical ownership.
- Add safe transition logging and fixed-cardinality metrics.
- Add deterministic startup/unknown/success/failure/stale/drain/stop/store/capacity/provider/scheduler/concurrency/shutdown tests using fake time and barriers. Block the coordinator while every local source moves healthy-to-unsafe and require the endpoint to change synchronously to 503.

Required mutation proofs:

1. Remove P02's default sampling omission for the probe; the captured-payload test must fail on the presence of `temperature`.
2. Permit a second directory probe after caller timeout while the first fake work item remains unretired; the scheduler operation-log/max-count test must fail.
3. Advance fake time to the exact freshness deadline while suppressing the coordinator wake; the request-side valid-through test must fail if readiness remains 204.
4. Remove P14's synchronous drain invalidation (or move it after unsafe publication) and leave only the old signal while the coordinator is barrier-blocked; the fixture must observe a stale 204 and the immediate-503/operation-order tests must fail.
5. Remove either invalidation-token equality or required-source-generation comparison from positive recovery; the stale pre-invalidation projection race must publish ready and the barrier test must fail.

Restore each mutation and run the complete verifier green before committing.

### Slice 3 — P20-F3 protected bounded diagnostics

**Commit:** `security(health): protect bounded diagnostics`

- Add P16-bound `HealthDiagnosticsReaders`, the explicit `HealthDiagnostics` authorization policy, and test authentication handlers.
- Add the exact diagnostics v1 DTO/source-generated context, fixed component/reason/bucket catalog, deterministic ordering/priority, size ceiling, and protected route.
- Remove `QueryController`'s legacy health action/DTO, `IClaudeService.CheckHealthAsync`, `ClaudeHealthResult`, and unused health-only state.
- Reserve exact all-method `/api/query/health` as bodyless 404 before SPA fallback.
- Add unauthorized/forbidden/separate-role, golden JSON, 4 KiB, deterministic ordering, closed-enum, no-cache, and seeded privacy-canary tests.
- Update API/operations documentation without documenting configured group names or sensitive examples.

Required mutation proofs:

1. Authorize diagnostics with the ordinary query role; the separate-policy 403 test must fail.
2. Add a fake provider message/exception field to the diagnostic DTO; the serialized canary/closed-schema test must fail.

Restore both mutations and run the complete verifier green before committing.

### Slice 4 — P20-F4 immutable deployment identity

**Commit:** `feat(health): expose release-aware deployment probe`

- Add the conditional `AdQueryReleaseId` project assembly-metadata emission, strict startup parser, Development sentinel, immutable `ReleaseIdentity`, exact deployment v1 DTO, and protected route. Do not add a release-ID generator, packaging invocation, offline PE reader, or manifest comparison.
- Add P16-bound `DeploymentOperators` and the explicit `DeploymentProbe` authorization policy.
- Extend the final runtime contributor mapping with `release_identity_invalid`; no earlier slice advertises or consumes release identity.
- Make the route read identity and readiness once, apply the identical `aggregate ready && LocalUnsafeMask empty && monotonic-fresh` derivation used by `/health/ready`, and emit the exact 200/503 relationship.
- Add missing/duplicate/malformed/whitespace/non-Development/sentinel tests, exact JSON/size/cache/auth tests, and concurrent publication tests proving identity and readiness never mix across reads.
- Add a producer-side build fixture with a manually supplied valid `AdQueryReleaseId` and verify the resulting entry assembly contains exactly that metadata; this tests P20 emission only. Check in bounded ready/unready deployment-response golden fixtures for P15's separately owned consumer tests.
- Document the exact P15 handoff from amendment `1c42c69`: P15-F8/Slice 3 performs generation/property injection/offline metadata-manifest comparison and its omit/substitute-property guard; P15-F7 parses the bounded v1 body on 200/503 and requires exact ready identity; P15-F10 uses exact-ID 503 for rollback drain targeting. P20 implements none of those consumers or P15 transport timing.
- Update deployment documentation to require P15's package builder/property and forbid mutable runtime overrides.

Required mutation proofs:

1. Substitute a configuration value for assembly metadata; the no-runtime-override architecture/startup test must fail.
2. Remove `release_id` from the 503 envelope or emit 200 while `ready=false`; the P20 golden/status invariant test must fail. P15-F8's separately owned omitted/substituted-property guard remains required by its own commit and is not repeated here.

Restore both mutations and run the complete C#/Pester canonical verifier green before committing.

## Deterministic verification matrix

The canonical verification must cover at least these cases without sleeps or live dependencies:

### HTTP and routing

1. GET and HEAD liveness return empty 204 before any readiness observation and call no collaborator.
2. Readiness returns empty 503 for initial, unknown, unhealthy, stale, missing, generation-fault, draining, stopping, and host-shutdown states; it returns empty 204 only when all seven sources are healthy/fresh.
3. Every health response, including 401/403/404/503, carries `Cache-Control: no-store, max-age=0`; no response has an ETag.
4. `/health`, `/health/anything`, and `/api/query/health` return bodyless 404 rather than `index.html`; unsupported methods never reach SPA fallback.
5. Deployment and diagnostics reject unauthenticated callers, ordinary query users, and each other's sole role. Authorized fixtures receive only their contract.
6. Deployment 200/503 and diagnostics 200 bodies match byte-stable golden JSON apart from injected fixed timestamp/generation fixtures and remain below their ceilings.
7. HEAD responses have no body; no content negotiation or query parameter enables detail.

### Atomic state and time

8. Thousands of barrier-controlled reads during alternating publications match one complete known snapshot; no component array/generation/ready value is torn.
9. Public generation changes only on serialized state/aggregate change, while a newer same-reason unsafe transition advances only its internal safety epoch/target. Same-or-older invalidation is idempotent. Both counters reject overflow and never wrap.
10. Fake monotonic time just before success expiry remains ready; exactly at expiry is stale/unready even when the stale-publish loop is deliberately blocked.
11. Wall-clock jumps forward/backward change only the protected display timestamp, never the freshness deadline.
12. A capacity-one change signal coalesces a burst while the final authoritative projection is published.
13. With the coordinator barrier-blocked after the last healthy snapshot, P14 `BeginDrain`, P14 host stop, and each job store/reserve/receipt/shutdown invariant transition call `InvalidateLocal` before their unsafe projection/commit becomes visible; readiness and deployment immediately return 503 without releasing the coordinator.
14. Repeat the blocked-coordinator proof for P07 ownership/integrity/capacity/store failure, P16 root/storage/logger/host stop, P17 writer/schema/integrity/capacity/write failure, and P09 scheduler stopping/faulted/full/all-detached. The fixed operation log requires `reserve source generation -> InvalidateLocal -> publish/commit unsafe -> coalesced hint` for every source class.
15. Barrier both gate orderings. Recovery published before invalidation may return ready only before the transition linearizes, after which invalidation wins. Invalidation published before a paused pre-invalidation recovery changes the token/required generation, so releasing that recovery cannot clear the mask or return 204. A second invalidation between recovery read and publish rejects the first token.
16. An unsafe transition that aborts remains 503 until a healthy projection acknowledges the current token with a strictly newer source generation. Reusing the unsafe generation, acknowledging an older token, or publishing an older source generation is rejected; the valid newer recovery clears only its component.
17. Repeated same/older invalidation is idempotent; a genuinely newer same-reason invalidation advances the internal fence without changing public generation. Host-stop latch cannot be cleared. Endpoint reads during these races perform one immutable read/monotonic comparison and invoke no coordinator, source, lock, or I/O.

### Provider

18. The captured request uses P02's configured primary route, fixed non-user prompt, eight-token maximum, exact required wire fields, and no `temperature`; output content is discarded and P12 is not invoked.
19. Each P13 provider code maps exactly, with capability/auth/config/request failures latched and transient failures retried from completion.
20. Rate limit honors the greater bounded retry delay; host cancellation is not classified as provider failure.
21. Repeated fake-time advances never overlap provider calls; a blocked provider call does not prevent directory/local-state updates.
22. A deprecated-temperature provider body containing unique canaries yields only `provider_capability_mismatch`; body, parameter, model, endpoint, and exception canaries appear nowhere else.

### Directory

23. The readiness call uses one explicit P06 context/unit, P09 operation kind, global FIFO, and a cache-bypassing RootDSE adapter; it makes no search and discards the value.
24. Queue saturation adds no item and follows failure cadence. Queue timeout/tombstone, running timeout, and caller detachment hold the one-probe latch until the exact retirement signal.
25. A never-returning fake keeps its original worker occupied, creates no replacement, and does not block P20 shutdown; releasing it permits one later retry.
26. Scheduler stopping/faulted/full and all-workers-detached projections synchronously invalidate readiness without endpoint I/O. Ordinary bounded worker use does not require an empty scheduler.
27. Every P09 code is preserved exactly and no LDAP input/canary enters state, JSON, logs, or tags.

### Local contributors and diagnostics

28. P12's fixed valid/invalid fixtures execute exactly once per process; unexpected accept/reject/code results remain unready and no HTTP poll recompiles.
29. P07/P14/P16/P17 fake projections map every fixed state, capacity, integrity, lease, drain, and stop condition with deterministic priority and no store/file access; their unsafe cases also satisfy tests 13–17.
30. Reflection/catalog tests prove exactly seven components, all allowed statuses/reasons/buckets, no free-form dictionary, no default enum serialization, and no unregistered code.
31. Seeded query, prompt, provider body, model, API key, exception, LDAP DN/filter/value, Windows identity/SID/group, path, job/artifact/feedback ID, and exact-capacity canaries are absent from all response bytes, log properties/messages, and metric tags.
32. Diagnostics age/latency/capacity/failure buckets hit every boundary deterministically.

### Release and P15 handoff

33. Production/staging startup rejects absent, duplicate, malformed, uppercase-hash, whitespace-padded, and configuration-only identities before listener promotion.
34. Development alone accepts the missing-metadata `development` sentinel; P15 rejects it as a production release ID.
35. P20's manually parameterized build fixture proves project metadata emission. P15-F8's own fixture generates/passes the property, reads the published assembly offline, and requires exact manifest identity; omitted/substituted/mismatched metadata fails before archive/IIS mutation.
36. P15 accepts a qualifying observation only for schema v1, exact expected ID, live true, ready true, and HTTP 200. Redirect, TLS, auth, timeout, malformed, oversized, 503, and wrong-ID cases follow P15's own failure rules.
37. P15 can parse exact-ID 503 to target P14's new-release rollback drain, but never counts it as ready; unavailable/malformed/wrong-ID authority produces `RecoveryRequired` after `PoolStartRequested`.

## Acceptance criteria

- `/health/live` is bodyless, constant-time, and independent of application/dependency state.
- `/health/ready` is bodyless and derives only from one immutable snapshot, its synchronous local-unsafe mask, and a monotonic expiry comparison.
- No health request invokes provider, LDAP, compiler, SQLite, filesystem, configuration binding, lease acquisition, or store recovery.
- Every P07/P09/P14/P16/P17 and host-stop healthy-to-unsafe transition synchronously invalidates the matching component before its unsafe state becomes visible; a blocked coordinator cannot extend a 204 result past that linearization point.
- Positive local recovery cannot clear a newer invalidation: current safety token, required source generation, and strictly newer accepted source generation are all checked under the publisher gate. Aborted transitions require a newer recovery generation.
- Provider and directory probes are tracked, finite, single-flight, completion-scheduled, and independently cancellable at the logical boundary.
- Provider probes use P02's builder, omit `temperature` by default, cap output at eight tokens, discard content, and preserve only P13 codes.
- Directory probes use P06/P09 exactly once, bypass only RootDSE's success cache, receive no reserved capacity, and cannot self-amplify a queued/detached physical item.
- Readiness is false for every unknown/stale/required-source failure and for P14 drain/stop; there is no degraded-ready response.
- P12's local self-test runs once per startup from fixed fixtures and exposes no plan/diagnostic text.
- Diagnostics is separately authorized, bounded to 4 KiB, closed-schema, deterministic, and free of sensitive/arbitrary values.
- The deployment response is separately authorized, bounded to 512 bytes, parseable on both 200/503, and always carries the immutable serving release ID.
- Non-Development identity comes only from P20-emitted assembly metadata whose property injection/offline manifest comparison is owned and guarded by P15-F8.
- Legacy `/health` and `/api/query/health` cannot return ASP.NET health output, controller JSON, redirect aliases, or SPA success.
- P15 remains the sole owner of URI selection, credential use, transport policy, observation cadence/deadline, IIS mutation, and rollback; its post-start rollback drains the exact new release before mutation.
- Every admitted finding lands in its one named commit with canonical green verification and recorded red/green mutation proof.
- The worktree is clean after each implementation commit.

## Landing and production sequencing

1. Land P01/P02/P06/P07/P09/P12/P13/P14/P16/P17 and re-ground P20's adapter names/codes against their committed contracts.
2. Obtain all P20 owner decisions below. An approved plan authorizes only these four slices.
3. Land P15's non-probe package, journal, IIS, and exact-new-release rollback-drain foundations as their plan permits. Preserve committed plan amendment `1c42c69`, which assigns property injection/offline metadata-manifest verification to P15-F8.
4. Land P20-F1 through P20-F4 in order. Do not deploy an intermediate revision: F1 intentionally makes readiness fail closed until F2, and the legacy script is not a supported fallback.
5. Land P15's consumers without shifting producer ownership: P15-F8 generates/injects/cross-checks the P20-emitted metadata; P15-F7 parses/gates the deployment response; P15-F10 performs post-start rollback drain. Run its Pester fixtures against P20's producer and envelope goldens.
6. Retire `csharp/deploy.ps1` under P15 and update the last operational documentation references.
7. Run canonical verification, then the explicitly opt-in Windows/IIS integration test under the intended service/probe identities. The smoke test may exercise a real provider/RootDSE only with separate explicit credentials and must report fixed codes without values.
8. No production promotion is allowed until P15 and P20 are both complete and the exact package identity, 200/503 parsing, auth, no-redirect/TLS, readiness, and rollback-drain behavior pass together.

This coordinated window avoids a circular dependency without inventing compatibility aliases. P20 declares and implements the producer first; P15's fake client can compile against the documented schema, then its live consumer lands. The old repository is explicitly non-production, so no unsafe `/health` bridge is retained.

## Rollback

Use new revert commits; never rewrite reviewed history.

- Revert P15 consumers before removing or changing deployment schema/identity. Disable deployment rather than accepting an unknown response.
- Reverting P20-F4 removes production deployability. Keep assembly/manifest validation aligned and never fall back to a path, header, environment variable, or mutable configured release ID.
- Reverting P20-F3 disables protected diagnostics if necessary; never restore raw `/api/query/health`, exception bodies, or ordinary query-role access.
- Reverting P20-F2 leaves readiness fail-closed/503. Do not retain a ready snapshot after removing any local invalidation call/handshake, restore signal-only unsafe transitions, invoke request-time provider/LDAP/compiler/store checks, or mark missing contributors healthy.
- Reverting P20-F1 removes the new probe routes and blocks P15. Never restore the combined `/health` implementation or allow the SPA fallback to impersonate health.
- Configuration rollback removes `Health` values only after their consumers. Do not resurrect `HealthChecks:CheckIntervalSeconds` as a silent alias.

## Risks and mitigations

- **Active probes cost provider calls and directory operations.** Cap provider output at eight tokens, probe healthy dependencies every five minutes, run one call at a time, latch immutable failures, and expose metrics for tuning only after evidence.
- **A health system can worsen an outage.** Endpoints are reads; background retries schedule from completion; LDAP retirement prevents tombstone/detached amplification; no reserved worker or replacement hides saturation.
- **A stale success can route traffic to a broken instance.** External failures publish component state immediately; every local unsafe transition synchronously latches/publishes unready before its own unsafe publication; token/source-generation checks reject stale recovery; monotonic age independently caps old external success.
- **The synchronous fence can deadlock or create false negatives if integrated casually.** Enforce owner-lock-to-publisher-gate ordering, prohibit source locks/I/O/callbacks under the publisher gate, make repeated invalidation idempotent, and allow only a newer token-acknowledging projection to recover. An aborted transition remains conservatively unready until that bounded reconciliation.
- **Strict readiness can remove every instance during a shared dependency outage.** That is truthful because normal work requires those dependencies. Liveness stays independent so supervisors do not restart healthy processes merely to chase an external outage.
- **Protected diagnostics can still become an information source.** Use a separate least-privilege Windows policy, closed schemas/buckets, no raw values, no caching, bounded bodies, and seeded privacy tests.
- **IIS site authentication may precede ASP.NET `AllowAnonymous`.** P15 supplies credentials and validates the actual binding/auth state. Platform operators must configure infrastructure probes consistently; P20 does not weaken IIS authentication.
- **A release string alone can be spoofed by an unsafe package process.** Bind it into assembly metadata before publish and require P15's clean-source/hash/manifest/path verification. Do not describe it as signing.
- **New-release traffic can arrive before P15 commits.** P15 now journals pool start and requires exact new-release P14 drain/quiescence before any rollback mutation. P20 keeps traffic readiness truthful and supplies identity on unready 503 responses.
- **A coordinator or serializer bug could itself fail.** Initial/fault states are unready, liveness stays trivial, schemas are source-generated/bounded, and unexpected errors use fixed safe logging without exception destructuring.
- **Predecessor type names may differ when landed.** Adapt names only after re-grounding; preserve ownership, reason strings, nonblocking projections, and timing semantics. Stop if a predecessor lacks the required safe projection instead of reading its store directly.

## Open owner decisions

### Decision 1 — endpoint exposure and roles

Choose bodyless app-anonymous live/ready plus two separately authorized Windows policies, or require one credential for every route. Recommendation: keep binary live/ready unprivileged and require distinct deployment-operator and diagnostics-reader roles; it supports infrastructure checks while withholding identity and reasons. IIS may still require site credentials.

Blocked until decided: Slices 1, 3, and 4; P15 production probe configuration.

### Decision 2 — active dependency cadence and cost

Choose bounded active probes or passive-only observations. Recommendation: probe primary provider and RootDSE immediately, then every five minutes after success; transient failures back off from 15 seconds to five minutes, while permanent failures latch. Use a 15-second provider timeout, 15-minute freshness, and eight output tokens. Healthy steady state costs 288 calls per dependency/process/day.

Blocked until decided: Slice 2 and P15's external-dependency readiness gate.

### Decision 3 — strict traffic readiness

Choose strict all-required readiness or allow degraded 204 responses. Recommendation: require fresh runtime, compiler, artifact, job, feedback, provider, and directory health and P14 `Accepting`; any unknown, stale, drain, ownership, integrity, or exhausted required capacity returns 503. Binary routing should not claim normal work is safe when a required subsystem is not.

Blocked until decided: Slice 2 and P15's qualifying-observation semantics.

### Decision 4 — release identity source

Choose build-embedded P15 identity or a mutable runtime value. Recommendation: embed P15's timestamp-plus-commit ID as `AdQueryReleaseId` assembly metadata, require the production grammar outside Development, and cross-check the package manifest. This prevents stale IIS configuration from making an old binary claim the new release; it is integrity metadata, not signing.

Blocked until decided: Slice 4 and P15's package/probe integration.

## Advisory review

Record no more than three rounds here. Each round must include UTC time, Claude Code version, configured model from the structured invocation envelope, maximum effort, verdict, material findings, and every applied revision or retained disagreement. If the third round causes edits, state explicitly that those final edits were not independently re-reviewed.

### Structured-dispatch note — before round 1

An initial one-shot dispatch reviewed SHA-256 `9DDA61B92FF7CAFAA7A92B7C9DE159C51B22E3C0F7755BD1BFFF863A0845608A` with Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort. Its invocation envelope ended `structured_output_retry_exhausted` after five invalid structured-output attempts, with no permission denial and no usable verdict. It caused no edit and is not counted as an advisory round; the successful retry below used a simpler strict schema and a fresh non-persisted session.

### Round 1 — 2026-07-22T00:24:00Z

**Reviewed revision:** SHA-256 `9DDA61B92FF7CAFAA7A92B7C9DE159C51B22E3C0F7755BD1BFFF863A0845608A`

**Reviewer:** Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict:** Revisions required; one medium finding and three optional precision comments applied

- Confirmed the plan was materially correct on every challenged provider, LDAP, staleness, HTTP, privacy, deployment, and rollback point except one slice-order defect.
- Required resolution: diagnostics v1 referenced `release_id` in Slice 3 although immutable `ReleaseIdentity` did not land until Slice 4. Removed release identity from diagnostics v1 instead of moving F4's identity concern into F3; identity now has one first consumer and one owner in the deployment slice.
- Made the cache-bypassing readiness RootDSE call unconditional even when `ActiveDirectory:RootPath` is configured.
- Made `ObservationMaxAge` cross-option satisfiability and boundary fixtures explicit, including the maximum P09 queue-plus-operation wait.
- Clarified that `release_identity_invalid` gains its producer and final runtime mapping in Slice 4; no earlier slice advertises or consumes release identity.

**Resulting revision:** SHA-256 `FCC6DF45002ED6F000EB94C904FE4169A71D3DFA23706E43658C948BC07A8D14`

### Round 2 — 2026-07-22T00:32:00Z

**Reviewed revision:** SHA-256 `3A4C664F16EB5FB605F46C7EF8C76A4CE3B3BA65F8DDE543045E4ADE8FF73D40`

**Reviewer:** Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict:** Accepted; no material findings

- Confirmed all Round 1 repairs: diagnostics has no release identity; Slice 4 solely introduces identity and its runtime failure mapping; RootDSE readiness ignores configured-root suppression; and freshness option ranges are satisfiable.
- Confirmed exact P15 commit `4867812` compatibility: `PoolStartRequested`, parsing the deployment envelope on 200/503, exact-new-release P14 quiescence before rollback mutation, and `RecoveryRequired` when identity/drain authority is unavailable.
- Confirmed the deployment envelope always has a valid immutable identity when its listener is reachable, 200/503 truth is coherent, and request-path I/O, privacy, boundedness, concurrency, fallback, rollout, commit mapping, and cold implementability have no remaining material defect.
- Optional P09 comment retained as an implementation handoff: P20's Slice 2 must add/consume the narrow projection and retirement signal over P09's already-owned scheduler state; if that seam cannot land without weakening P09, the existing stop rule applies.
- Optional enum-timing comment required no change because Slice 4 now explicitly adds the `release_identity_invalid` producer/mapping and no earlier slice consumes it.
- Optional boundary comment is satisfied by the already-required cross-option boundary fixtures; implementation must include the tightest one-minute success-interval combination.

Round 2's acceptance was later superseded by the independent consistency audit below.

### Independent consistency audit after round 2 — 2026-07-22

- `P20-A1` (`high`): asynchronous-only local projection hints allowed the endpoint to keep returning a cached 204 after P14 drain/stop or P07/P09/P14/P16/P17/host readiness-critical failure while the coordinator was blocked. A naive direct invalidation could still be overwritten by a coordinator candidate read before the transition.
- Repair: P20-F2 now owns a synchronous per-component invalidation fence. Every unsafe source reserves its next source generation and publishes an atomic unready latch before its unsafe projection/commit becomes visible. Positive recovery requires the current token, required generation, and a strictly newer source projection under the same publisher gate. Idempotence, both gate orderings, a second invalidation, aborted transition, every local source, blocked coordinator, and host stop have deterministic barrier tests and mutations.
- `P20-A2` (`medium`): P20 emitted/parsed assembly metadata but did not bind P15's required property injection and metadata/manifest comparison to an exact P15 finding/commit/guard, leaving split ownership and an implementable package that omitted runtime identity.
- Repair: committed P15 amendment `1c42c69` assigns generation, checked property injection, offline published-assembly inspection, manifest equality, and omitted/substituted-property mutation to P15-F8/Slice 3. P20-F4 retains only conditional project metadata emission, runtime parsing, and producer/envelope fixtures. The dependency, slices, tests, landing, acceptance, and rollback text now preserve that single boundary.

These repairs are substantive and consume the final permitted Claude advisory round below.

### Round 3 — 2026-07-22T01:01:36Z

**Reviewed revision:** SHA-256 `160FE8E7E80CCC845C16D6924E16C50A946CC30835E6B3DAFA93FC4DD879BCD7`

**Reviewer:** Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict:** Accepted; no material findings

- Confirmed the synchronous local invalidation fence is linearizable: every local unsafe transition publishes 503 before the unsafe state becomes visible, stale coordinator candidates cannot recover readiness, and the lock ordering introduces no deadlock or unbounded request-path work.
- Confirmed every local source class, both publisher-gate orderings, repeated and aborted transitions, host stop, stale recovery, second invalidation, and the required mutation guards are covered deterministically.
- Confirmed the exact P15 boundaries: amendment `1c42c69` owns production identity generation/injection/offline verification while P20 owns conditional metadata emission and runtime consumption; amendment `4867812` consumes the 200/503 identity envelope and targets exact-ID rollback drain without duplicating P20 behavior.
- Optional precision comment applied after the reviewed snapshot: the freshness proof now distinguishes P09's recommended effective 30-second queue-plus-operation wait from its theoretical ten-minute configured ceiling and gives both satisfiability calculations explicitly.
- Optional precision comment applied after the reviewed snapshot: the deployment slice now spells out the same complete aggregate-ready, empty-local-unsafe-mask, and monotonic-fresh derivation as `/health/ready`.

The two optional precision edits above were made after Round 3 acceptance and were not independently re-reviewed. No fourth advisory round will be run.
