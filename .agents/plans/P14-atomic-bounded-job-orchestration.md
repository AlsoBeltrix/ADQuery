# P14 — Atomic, Bounded Query-Job Orchestration

Status: **Reviewed — implementation is not authorized**

Owner approval: Pending

Implementation dependencies: P01 supplies canonical verification; P02, P06, P07, P09, P12, and P13 supply the provider, budget, artifact, LDAP, compiler, and typed-failure contracts consumed here. P03 must audit and pin the new SQLite dependency against the landed target framework. P16 must supply `IDataPaths.StateRoot`, immutable configuration, and the logical protection projection before durable storage is enabled; P15 applies/verifies the production host ACL and storage controls. P14 must land before P15 relies on drain/quiescence, P17 accepts feedback against its private terminal receipt, and P19 consumes versioned polling, cancellation, and retry behavior.

Review status: Round 3 revisions applied; final repair not independently re-reviewed under the three-round limit

## Problem

The asynchronous query path has separate mutable job, queue, worker, result-cache, retry, cancellation, and retention authorities. `ConcurrentDictionary` protects dictionary operations but not fields on the mutable `QueryJob`; state updates have no expected version, transition table, or execution lease. The queue is unbounded, per-owner admission is a count-then-insert race, and storing a job then awaiting enqueue can strand it when the request disconnects.

The hosted service polls once per second, adds a semaphore around already dequeued work, launches untracked `Task.Run` operations, and can stop without awaiting them. Cancellation can arrive before the mutable cancellation source is attached, cancellation and completion can overwrite one another, duplicate queue entries can execute the same job, and a published result can outlive or lose the job transition that should reference it.

The same surface also stores raw exception text, embeds a model override directive in user context, uses a hard-coded alternate-model fallback, authorizes by a domain-stripped account name, retains only completed jobs, and loses all queued/running state on process recycle. These failures are observable as duplicate executions, exceeded limits under concurrent admission, stuck queued jobs, stale or missing downloads, incorrect terminal status, job loss on restart, excessive memory/database growth, and exposure of sensitive query or provider details.

## Repository evidence

- `csharp/Models/QueryJob.cs` exposes setters for identity, query/context, plan, state, timestamps, progress, result metadata, aggregation, warnings, error text, and a `CancellationTokenSource`; callers receive the same mutable object.
- `csharp/Services/IQueryJobStore.cs` exposes independent `StoreJob`, `UpdateProgress`, `UpdateStatus`, and `SetCompleted` operations with no expected version, allowed source state, or lease.
- `csharp/Services/InMemoryQueryJobStore.cs` stores mutable objects in a `ConcurrentDictionary`, mutates their fields in place, permits arbitrary status replacement, and cleans up only `Completed` jobs.
- `csharp/Services/InMemoryQueryJobQueue.cs` uses `Channel.CreateUnbounded<string>` and treats the channel count as queue state.
- `csharp/Services/QueryJobManager.cs` checks per-user active work by reading a list before separately storing and enqueueing. Concurrent requests can all observe capacity, and cancellation between store and enqueue leaves a durable-looking `Queued` record with no work item.
- The same manager moves `Queued` to `Running` without compare-and-swap, assigns the cancellation source afterward, writes progress on every callback, publishes a mutable result in `IMemoryCache`, and then unconditionally marks the job completed. Cancel, stale workers, failures, and completion can overwrite one another.
- Retry appends `[FORCE_MODEL: ...]` to `Context`, reparses it with a regular expression, carries no lineage or idempotency record, and can fall back to a hard-coded stale provider identifier.
- `csharp/Services/QueryJobExecutorHostedService.cs` polls, dequeues ahead of its semaphore, launches `_ = Task.Run(...)`, and never owns a tracked task set it can drain before returning.
- `csharp/Controllers/QueryController.cs` passes `RequestAborted` through the non-atomic store/enqueue sequence, performs authorization after retrieving a mutable record, exposes raw query data in status, and maps cancel/retry through check-then-act controller logic.
- `GetSamAccountName` strips domain information, so equal account names from different domains are not an authorization identity. P07 already requires canonical Windows SID ownership.
- Current job configuration has three workers, ten active jobs per user, and a 24-hour completed-only retention, but no global queue cap, retained-record cap, queue-age limit, progress-write bound, shutdown contract, or idempotency window.
- The checked-in deployment guidance says async jobs are in memory and disappear on recycle. P15 requires P14 to close admission and prove `accepting_work = false`, `queued = 0`, and `running = 0` before IIS cutover.

## Goals

1. Make one durable state machine the only writer of job state, queue membership, idempotency records, counters, and execution leases.
2. Publish deeply immutable, schema-versioned snapshots whose monotonic `Version` changes on every observable mutation.
3. Bound global queued work, per-owner active work, retained jobs, command bytes, retry attempts, database bytes, and progress-write rate.
4. Use a durable FIFO queue and fixed, tracked worker loops with no polling, semaphore/dequeue gap, untracked task, or fire-and-forget execution.
5. Give each active attempt one opaque lease; only the current lease may publish progress or a terminal outcome.
6. Make admission, idempotency, queue insertion, and the request-disconnect acceptance point one transactionally defined operation.
7. Make user cancellation, P06 deadline, dependency timeout, host stop, restart recovery, failure, success, and artifact publication races deterministic.
8. Consume P07 prepare/publish/remove semantics so no terminal job references an unpublished artifact and every lost completion race compensates a published artifact.
9. Persist P13 typed outcomes, including a distinct terminal `Interrupted` state for host/restart causes, without raw exception/provider text.
10. Replace prompt-embedded model directives with trusted structured model selection and bounded retry lineage.
11. Authorize every job command inside the orchestration boundary by canonical Windows SID and minimize public/logged/persisted sensitive data.
12. Supply P15 with an authenticated, idempotent drain/resume and exact quiescence contract.
13. Recover queued work after restart without automatically rerunning an attempt that might already have performed external work.
14. Retain a separate private, SID-authorized, bounded 24-hour terminal receipt for P17 feedback submission without extending public job or artifact availability.
15. Add deterministic concurrency, crash-window, mutation, and retention guards with no live provider, LDAP, IIS, or wall-clock sleeps.

## Non-goals

- Do not change the Claude wire request or decide whether a model accepts `temperature`; P02 owns the one compatible request builder and capability behavior.
- Do not add automatic provider or job retries. P13 retry disposition is advice; only an authenticated explicit retry creates a new attempt.
- Do not redefine P06 budgets, reset a tracker between phases, recompute aggregation, or turn budget failure into partial success.
- Do not revalidate mutable plans or persist executable plans across attempts/configuration versions; P12 owns compile-once immutable execution.
- Do not implement P07 artifact bytes, exporters, capacity accounting, leases, or filesystem recovery.
- Do not replace P09's bounded LDAP scheduler or start replacement LDAP work after a logical timeout.
- Do not redesign browser polling beyond the minimum compatibility needed to send idempotency and consume the new job contract; P19 owns the browser architecture.
- Do not store feedback, compute the P16-keyed query HMAC, define feedback consent/idempotency, or expose feedback analytics. P17 owns those operations and consumes only P14's authorized private terminal receipt.
- Do not define the portable data root, logical IIS ACL projection, secret sources, or volume-encryption deployment policy. P16 owns paths/projection, P15 applies and verifies host controls, and P14 consumes them.
- Do not add transparent database-level encryption or a key-management scheme. The initial database relies on P16's non-web-root state path/logical protection plus P15-applied least-privilege ACL and operator-managed encrypted volume; encryption beyond that requires a separately approved design.
- Do not support multiple application processes sharing one job database. P07/P15 require a single owning application process; SQLite transactions still protect accidental concurrent access and startup must fail if the process/root ownership contract is not met.
- Do not implement cross-machine/distributed scheduling, priority classes, or per-owner fair queuing. Durable FIFO plus finite per-owner admission is the approved initial scheduler.

## Accepted dependency contracts

### P01 — verification

Add focused tests to `tests/AdQueryOrchestrator.Tests` and run:

```powershell
pwsh -NoLogo -NoProfile -File scripts/verify.ps1
```

If P01 has not landed, P14 does not create a second verification convention. Land P01 first.

### P02 — provider and model requests

P02 remains the only request builder. P14 selects a trusted effective model and passes it through P02's internal override/selection seam; P14 never builds provider JSON, emits `temperature`, guesses capabilities from a slug, parses provider prose, or falls back to a hard-coded model. A missing configured alternate model rejects retry before admission.

### P06 — finite active execution

Create exactly one P06 `QueryExecutionContext` when a durable queued job is successfully leased into `Running`. Queue time does not consume its active deadline; `MaxQueueAge` is P14's separate capacity policy. The same tracker follows planning/execution as required by the landed P06 signatures and is never recreated per step. `query_budget_exceeded` produces `Failed` with no artifact. Executor aggregation is authoritative and P14 neither recomputes it nor turns groups into primary rows.

### P07 — artifacts

P07 supplies `PrepareAsync`, `PublishAsync`, `AbortAsync`, `OpenAsync`, and `RemoveAsync` over opaque references. P14 stores no `IMemoryCache` key or physical path. Publication precedes the durable `Completed` transaction; if a cancellation, failure, host stop, or stale lease wins before that transaction, P14 aborts an unpublished preparation or removes a published artifact. P07 owns leases, quarantine, charged failed deletion, and committed-orphan recovery.

### P09 — bounded LDAP work

P14 awaits P09's logical operation result and never starts replacement tasks for timed-out physical LDAP work. P09 owns truthful physical occupancy and its bounded stop; P14 owns only the surrounding job lease and terminal outcome.

### P12 — authoritative compilation

Inside each execution lease, generate one raw provider plan, compile it once, and execute only the returned deeply immutable executable plan. Keep the executable object private to that attempt. Persist only P12's already-safe SHA-256 plan fingerprint, compiler schema version, and policy snapshot version in the terminal feedback receipt; no raw or compiled plan is written to the job database. A retry is a new attempt and recompiles against the current policy/configuration snapshot.

### P13 — causes and outcomes

Persist P13 `FailureDescriptor`/`CancellationDescriptor` values in bounded versioned columns, never `Exception`, provider body, raw diagnostic prose, or `ex.Message`. P13's one first-observed stop claim governs deadline/dependency/user/host causality. P14 supplies the durable state-transition publication gate, terminal status, and exact cancel endpoint outcomes. Add P14-owned registered codes for admission, queue expiry, state-store capacity, and restart interruption without renaming P13-owned codes.

### P16/P15 — protected storage and host application

The production SQLite file is `jobs.db` directly beneath P16's typed `IDataPaths.StateRoot` (`<DataRoot>/state`), never beneath a P14-invented child, hard-coded drive, or web root. P16 owns the fixed state path, process-lifetime DataRoot lease, immutable options, and logical ACL projection. P15 discovers the actual application-pool identity and applies/read-backs the approved production ACL and host/volume controls. P14 owns only database schema, transactional behavior, quotas, cleanup, and recovery; it must not invent a second root or protection model.

### P17 — private feedback target receipt

P17 receives one narrow internal `IQueryJobFeedbackTargetReader`, not a public job endpoint, SQLite connection, mutable entity, or general command-store reader. P14 authorizes the caller by canonical SID in the same bounded read and returns an immutable terminal receipt retained for 24 hours. P17 immediately derives its P16-keyed query HMAC from the transient query bytes and never stores/logs the raw query or owner. P14 supplies exact lineage/retry, effective P02 provider/model, P12 fingerprint/version, P13 outcome, P07 publication/count, and server duration facts; P17 neither trusts browser echoes nor reconstructs them. P17 owns feedback consent, idempotency, storage, event retention, and analysis.

## Core invariants

- SQLite is the source of truth for accepted commands, queue order, versions, leases, lineage, idempotency, and terminal outcomes. Memory contains only active attempt handles, immutable read projections, metrics projections, and wake hints.
- Successful transaction commit is the only state linearization point. No controller, worker, artifact callback, or cleanup loop mutates a job object directly.
- A job has exactly one immutable command and a sequence of immutable snapshots. The current snapshot version starts at 1 and increases with checked arithmetic on every persisted observable change.
- Exactly one status is current. Terminal states are `Completed`, `Failed`, `Cancelled`, and `Interrupted`; no terminal state transitions to another state.
- Every active worker owns a random lease ID plus persisted process/worker identity. A stale, absent, or revoked lease cannot publish progress, artifact reference, or outcome.
- Queue rows are bounded durable data. The in-memory wake primitive contains no job IDs and is never authoritative.
- A committed queued job is discoverable after response loss or restart. A job not committed is not accepted.
- A previously `Running` attempt is never re-executed automatically after restart because external effects might already have occurred. It becomes `Interrupted(service_restarted)` and may be explicitly retried as a new child attempt.
- Request disconnect is not job cancellation. After admission commits, `RequestAborted` is never linked to job execution.
- Explicit cancellation intent that wins the `Running -> CancellationRequested` transaction prevents later successful completion for that lease.
- A successful job references exactly one durably published P07 artifact. Every published artifact whose completion transaction loses is removed through P07.
- All terminal states receive the same finite client-visible maximum retention policy unless a later owner-approved schema version says otherwise. A completed public snapshot is additionally clamped to its P07 descriptor's absolute artifact expiry, so it can expire slightly earlier but never deliberately outlive its result. Access never extends expiry.
- The terminal transaction also creates one private immutable P17 feedback receipt. It is not a public snapshot, result lease, retry authorization, or extension of artifact availability; it expires independently 24 hours after terminal time and counts against dedicated global/per-owner receipt capacity from admission onward.
- Owner identity is one normalized Windows SID. Display names, SAM names, UPNs, and opaque job IDs are never authorization substitutes.
- No metric tag, normal log, public job snapshot, idempotency record, or error descriptor contains query/context text, LDAP values, provider bodies, raw model output, physical paths, or account names.

## Durable data model

### SQLite dependency and database controls

Add an exact, lock-file-pinned `Microsoft.Data.Sqlite` version compatible with the landed target framework. Apply P03's resolved-graph and vulnerability audit in the same package commit. Do not use a floating version or a native provider from an unreviewed feed.

Use one versioned schema with `PRAGMA user_version`, foreign keys enabled, a fixed page size selected before schema creation, bounded busy timeout, `auto_vacuum=INCREMENTAL`, and an explicit WAL checkpoint/storage budget. `MaxDatabaseBytes` caps the main database through `max_page_count`; `MaxWalBytes` is the checkpoint/truncation threshold and `journal_size_limit`; `MaxStateStorageBytes` covers the main database, WAL, SHM, bounded next transaction, and `StateEmergencyReserveBytes`. Treat `SQLITE_FULL`, bounded busy exhaustion, migration mismatch, or an unsupported newer schema as typed unavailable/capacity failures, never as a fallback to volatile memory.

Every write transaction is short, parameterized, and touches only one job or at most `MaintenanceBatchSize` cleanup/recovery rows. No provider, LDAP, filesystem, artifact, logger, metrics exporter, or awaitable external operation runs inside a transaction. Reads materialize immutable values and close their transaction/reader before returning; no long-lived reader may pin WAL growth. Configure automatic checkpointing at no more than 2 MiB of 4 KiB page frames and attempt `wal_checkpoint(TRUNCATE)` whenever the WAL reaches `MaxWalBytes`.

Admission charges the maximum encoded terminal receipt plus row/index allowance for every nonterminal receipt reservation, not merely the current queued-command bytes. Before any nonterminal write, require projected main + WAL + SHM + the write + every outstanding terminal allocation to remain below `MaxStateStorageBytes - StateEmergencyReserveBytes`; a terminal transition converts its already charged allocation instead of discovering new logical capacity demand. The emergency reserve may be consumed only by guaranteed terminal transactions: active-lease completion/failure/interruption, one-row queued owner cancellation, and bounded queue-expiry/startup-recovery batches. Validate main-database allocation plus worst-case write/WAL overhead so every simultaneously admitted nonterminal reservation can convert without a successful intervening checkpoint, including all active leases and every queued job terminalized through individual cancellation or `MaintenanceBatchSize` expiry/recovery batches. If checkpoint cannot reclaim space within the busy bound, reject admission/progress/maintenance, preserve the charged terminal allocations and emergency headroom, and fail readiness before the absolute state-storage budget is crossed. A tracked maintenance pass also performs bounded incremental vacuum after terminal cleanup. Startup validates the main database plus WAL/SHM paths remain below the P16 root and reports each allocation separately.

Schema migrations are forward-only, transactional, idempotently versioned, and tested from an empty database and every prior P14 schema fixture. A failed migration leaves the prior database intact and readiness unavailable. A newer unknown schema fails closed; code never deletes or recreates it automatically.

### Tables and bounds

Use normalized columns for control flow rather than deserializing arbitrary CLR objects:

```text
job_service_state
  singleton_key, mode, drain_generation, drain_token_hash,
  draining_release_id, updated_at

jobs
  job_id, snapshot_schema_version, command_schema_version, version,
  owner_sid, status, created_at, queued_at, started_at, terminal_at, client_expires_at,
  query_utf8, context_utf8, requested_result_limit,
  model_route, resolved_model_id,
  root_job_id, parent_job_id, attempt_number, retry_reason,
  progress_phase, progress_sequence, progress fields, progress_at,
  lease_id, lease_process_id, lease_worker_ordinal, lease_epoch,
  outcome_schema_version, failure_json, cancellation_json,
  artifact_reference, artifact_expires_at, total_rows,
  feedback_receipt_reserved,
  cleanup_lease_id, cleanup_lease_process_id, cleanup_lease_at

job_queue
  enqueue_sequence INTEGER PRIMARY KEY AUTOINCREMENT,
  job_id UNIQUE REFERENCES jobs(job_id), enqueued_at

job_idempotency
  owner_sid, operation_kind, key_hash, request_fingerprint,
  job_id REFERENCES jobs(job_id), client_expires_at,
  UNIQUE(owner_sid, operation_kind, key_hash)

job_feedback_receipts
  job_id PRIMARY KEY, terminal_job_version, owner_sid,
  lineage_id, attempt_number, predecessor_job_id, retry_kind,
  query_utf8, query_received_at_utc,
  effective_provider, effective_model_id, model_selection_route,
  plan_fingerprint_sha256, compiler_schema_version, policy_snapshot_version,
  terminal_status, terminal_outcome_json,
  result_row_count, artifact_published, duration_milliseconds,
  terminal_at_utc, feedback_expires_at_utc
```

Store query/context as bounded UTF-8 bytes rather than relying on character counts. They are private command data needed for execution and explicit retry; only the exact query bytes are copied into the private receipt for P17's transient HMAC input. Neither value enters public snapshots or logs. Bound every serialized descriptor, model identifier, phase, retry reason, SID, ID, and artifact reference before insertion. Check constraints enforce closed status/route values, all-or-none effective provider/model/route, nonnegative counts/duration, terminal-field consistency, and exactly one terminal payload shape: Completed has no P13 failure/cancellation outcome, requires `artifact_published=true` and a row count; Failed, Cancelled, and Interrupted require their P13-allowed sanitized outcome, set `artifact_published=false`, and have no row count. `job_feedback_receipts` deliberately has no cascading foreign key to `jobs`, because it must survive client-visible job deletion; only the state machine can insert or delete it.

Indexes cover FIFO queued claim, owner plus active status, client terminal expiry, feedback expiry, receipt owner lookup, current direct children through a covering `jobs(parent_job_id, attempt_number, job_id)` index, retained direct children through `job_feedback_receipts(predecessor_job_id, attempt_number, job_id)`, lineage attempts, and idempotency lookup. Query plans must be inspected in tests so admission/claim/cleanup/feedback resolution do not degrade to an unbounded table scan within configured retained-job and receipt limits.

### Immutable contracts

Replace mutable `QueryJob` with separate internal and public immutable records:

```text
QueryJobCommand
  JobId, OwnerSubject, QueryBytes, ContextBytes, RequestedResultLimit,
  ResolvedModelSelection, RetryLineage, CreatedAt

QueryJobSnapshot
  SchemaVersion, JobId, Version, Status,
  CreatedAt, StartedAt, TerminalAt, ClientExpiresAt,
  Progress, ModelRoute, RetryLineage,
  TerminalOutcome, CompletedResult

JobExecutionLease
  JobId, ExpectedVersion, LeaseId, LeaseEpoch,
  ProcessInstanceId, WorkerOrdinal, private command

IQueryJobFeedbackTargetReader
  ResolveOwnedTerminalAsync(OwnerSubject caller, JobId jobId, CancellationToken)

QueryJobFeedbackTargetSnapshot (internal, non-serializable, disposable)
  JobId, TerminalJobVersion,
  LineageId, AttemptNumber, PredecessorJobId, RetryKind,
  AcceptedRetryCount, LatestAcceptedChildJobId, LatestAcceptedRetryKind,
  QueryUtf8, QueryReceivedAtUtc,
  EffectiveProvider, EffectiveModelId, ModelSelectionRoute,
  PlanFingerprintSha256, CompilerSchemaVersion, PolicySnapshotVersion,
  TerminalStatus, TerminalOutcome,
  ResultRowCount, ArtifactPublished, DurationMilliseconds,
  TerminalAtUtc, FeedbackExpiresAtUtc
```

Use sealed records, immutable arrays/value objects, and defensive construction. Do not expose raw command bytes, SQLite entities/connections, cancellation sources, mutable plans, dictionaries, or lists through `QueryJobSnapshot`. P07's descriptor remains the authority for preview, aggregation, warnings, and schema; the completed job stores only its opaque reference and bounded summary fields.

The public DTO is a deliberate projection of the authorized immutable snapshot. It includes `schemaVersion`, `jobId`, `version`, status/timestamps, safe progress, logical model route, lineage IDs/attempt, sanitized P13 outcome, and completed artifact URLs/row count. It excludes owner SID, raw query/context, exact model ID, plan, filter, provider response, filesystem details, and cleanup/lease internals. Status responses set `Cache-Control: no-store`, return an ETag derived from job ID/version, and honor `If-None-Match` with `304`.

The P17 reader is an internal capability registered only for the feedback workflow. Its SQL includes `owner_sid = caller` and `feedback_expires_at_utc > now` in the same short read transaction. Missing, foreign, or feedback-expired returns one indistinguishable `NotFound`; an owned active job returns `NotTerminal`; only a receipt returns `Found`. An owned client-retained terminal job without its required receipt is a typed invariant/store-unavailable result that fails readiness, never `NotTerminal` or a fabricated projection. The snapshot does not return owner SID because authorization already succeeded. `QueryUtf8` is an owned bounded zeroable buffer, never a generated-record `ToString`; P17 computes its HMAC and disposes/clears it immediately. All other fields are immutable safe scalars/descriptors.

In that same read transaction, derive direct accepted retry facts from the bounded union of current jobs and retained receipts where `parent_job_id`/`predecessor_job_id` equals the target. `AcceptedRetryCount` counts distinct direct child jobs; latest is highest `AttemptNumber`, then canonical `JobId` as an unreachable-tie breaker. This dynamic lineage projection does not change `TerminalJobVersion`, and it replaces the browser's untrusted `user_requested_retry` claim.

## State machine and transition ownership

Create one `IQueryJobStateMachine` implementation over SQLite. It is the only production type allowed to insert/update/delete `jobs`, `job_queue`, `job_idempotency`, `job_feedback_receipts`, or `job_service_state`. Every mutating method returns a closed result (`Applied`, `AlreadyApplied`, `NotFoundOrUnauthorized`, `Conflict`, `StaleLease`, or a typed capacity/unavailable result); callers never infer success from affected-object mutation.

Allowed public status transitions are:

```text
admission commit                    -> Queued (version 1)
Queued + oldest transactional claim -> Running
Queued + explicit owner cancel      -> Cancelled(job_cancelled)
Queued + queue-age expiry           -> Failed(job_queue_timeout)
Running + accepted owner cancel     -> CancellationRequested
Running + successful publication    -> Completed(artifact reference)
Running + typed ordinary failure    -> Failed(failure descriptor)
Running + host stop                 -> Interrupted(service_stopping)
CancellationRequested + worker ack  -> Cancelled(job_cancelled)

startup recovery:
  persisted Queued                  -> remains Queued and is woken
  persisted Running                 -> Interrupted(service_restarted)
  persisted CancellationRequested   -> Cancelled(job_cancelled)

Completed | Failed | Cancelled | Interrupted -> no further status
```

`CancellationRequested` is active/nonterminal and counts as running for all limits and drain reports. Queue expiry is a typed capacity failure, not user cancellation or P06 execution-time exhaustion. Startup interruption is a new attempt-safe P13 registry entry with host origin and `retry_new_attempt`; it does not reuse `service_stopping` or silently requeue work.

Every terminal transition atomically updates the job and inserts exactly one `job_feedback_receipts` row while converting the job's pre-reserved receipt slot to a retained receipt. The receipt's `TerminalJobVersion` is the final job version and never changes. Copy the immutable command's root/parent/attempt/retry kind and exact query bytes; copy effective provider/model/route only after P02 actually established the request (all three remain null for a pre-provider terminal outcome); copy P12's safe fingerprint/compiler/policy versions only after compilation; copy P13 terminal outcome and P07 row/publication facts. Measure `DurationMilliseconds` from active-start to terminal using the injected monotonic clock, checked/clamped to the bounded schema. A queued cancellation or queue-age failure never reached active-start or P02, so duration and the provider/model/route tuple are null; restart recovery also leaves duration null when the prior process cannot prove it. Raw/compiled plan, context, provider body, exception, artifact reference, and owner display data never enter the receipt.

Receipt insertion is part of terminal correctness, not optional telemetry. Admission reserves capacity for one future receipt, so normal terminal publication cannot discover that the 24-hour receipt quota is already full. A duplicate receipt, missing reservation, invalid cross-field tuple, or exhausted emergency storage is an invariant/readiness failure; it never commits a terminal job without its authoritative receipt. Direct accepted-child fields are deliberately not persisted into the immutable receipt because later explicit retries can be admitted; the reader computes those bounded facts transactionally without changing the terminal version.

All worker writes use `WHERE job_id = ... AND lease_id = ... AND lease_epoch = ... AND status IN (...)` plus the expected version where appropriate. The update and resulting version/read snapshot occur in one transaction. Checked version overflow is an invariant failure that makes readiness unavailable; it never wraps.

P13's `OperationCancellationContext` remains the single stop-cause arbiter. Each active lease has one short in-process publication gate shared by its P13 cancellation/timeout callbacks and the durable completion/failure transaction. A callback acquires the gate, claims P13's slot, then requests the matching durable transition; successful completion acquires the same gate, verifies no stop claim, and commits `Completed`. This prevents a stop claim from appearing between the final check and commit without creating a competing cause slot. The gate contains no external await. Explicit owner cancellation and host stop additionally rely on the SQLite status predicate, so a request from another thread cannot overwrite a transactionally completed job.

## Atomic admission, idempotency, and queueing

### Admission transaction

`AdmitAsync` receives an authenticated owner subject, bounded private command, trusted resolved model selection, and mandatory idempotency key. Before opening the final transaction it validates byte/field limits and computes a domain-separated, length-prefixed SHA-256 request fingerprint over every execution-affecting value.

Inside one `BEGIN IMMEDIATE` transaction:

1. Require service mode `Accepting`.
2. Look up `(owner SID, operation kind, key hash)`.
3. Return the existing job when key and fingerprint match; return `job_idempotency_conflict` when the fingerprint differs.
4. Count durable queue rows, owner active rows (`Queued`, `Running`, `CancellationRequested`), global/client-retained rows, owner/client-retained rows, and global/per-owner retained receipts plus nonterminal receipt reservations through indexes.
5. Reject at the first exceeded finite limit with a typed outcome and no partial record.
6. Insert the immutable command/snapshot with one receipt reservation, FIFO queue row, and client-window idempotency row.
7. Commit once.

The commit is the acceptance point. Only after a successful commit does P14 pulse workers and return `202 Accepted` with stable `Location`, job ID/version, and whether the response is an idempotent replay. A same-key/same-fingerprint replay returns the same response and never consumes another capacity slot.

Use `RequestAborted` only for validation and pre-commit work. Immediately before commit, check it once; perform the short commit with an internal bounded store token so cancellation cannot turn a committed job into an apparent rollback. If commit returns an ambiguous provider/connection outcome, close that connection and resolve the key through a fresh bounded read: a matching record is accepted, absence is unaccepted, and mismatch is conflict. Once accepted, never link `RequestAborted` to provider, compiler, directory, artifact, or job cancellation. A client that loses the response repeats the same key to discover the committed job.

### Durable queue and wake signal

`job_queue` is the bounded FIFO queue. Remove `IQueryJobQueue` as a second source of truth. An in-memory `AsyncVersionedSignal` carries only a monotonically increasing generation and completes current waiters; it stores no IDs and has constant memory.

After every committed enqueue and after startup recovery, increment/pulse the signal. Each fixed worker:

1. Captures the observed signal generation.
2. Transactionally tries to claim the oldest eligible queued row.
3. If a lease is returned, executes it and immediately tries to claim again; it also pulses peers so all fixed workers become work-conserving under a burst.
4. If none is returned, waits until the signal generation differs from the observed value.

The compare-and-wait operation is atomic inside the signal, closing the check/reset/wait race. A commit before the wait changes the generation and returns immediately; a commit after the wait completes it. A process crash after database commit but before pulse is repaired by startup recovery. No timer, `Task.Delay`, channel count, or periodic database query is used to discover work.

Transactional claim selects by `enqueue_sequence`, verifies queue age and current `Queued` status, deletes the queue row, writes `Running`, creates a cryptographically random lease ID/epoch tied to process and worker, increments version, and commits. If the oldest row expired, fail it with `job_queue_timeout` and continue within a bounded number of rows before yielding; never retain an expired queue row indefinitely or scan an unbounded batch.

## Fixed tracked workers and execution pipeline

Replace the polling/semaphore/`Task.Run` hosted service with exactly `MaxConcurrentJobs` async worker loops created once by the hosted service and retained in an array. Calling an async worker method is sufficient; do not wrap it in `Task.Run`. `ExecuteAsync` awaits `Task.WhenAll`, and `StopAsync` coordinates the same tasks. A separately tracked maintenance loop owns retention/checkpoint intervals; it does not poll for work.

For each lease, a worker creates one DI scope, registers its active attempt handle, and executes:

1. Capture the injected monotonic active-start timestamp, create the P13 cancellation context, and create exactly one P06 execution context after `Running` commits; queue wait is excluded.
2. Call P02 with the command's trusted resolved model selection. Do not mutate context or insert a model directive. Once P02 establishes the actual request, retain its fixed effective integration kind/model ID/selection route in the active attempt metadata; a failure before that boundary leaves all three absent.
3. Compile the returned raw plan once through P12, retain only the immutable executable in the active lease, and retain P12's safe SHA-256 fingerprint/compiler schema/policy snapshot versions as active attempt metadata.
4. Execute through the P06/P09 contracts and forward safe progress through the coalescer.
5. Reuse P06/P11 authoritative rows and aggregation without a second pass.
6. Prepare and publish the P07 canonical artifact.
7. Attempt the lease-checked `Completed` transaction with the opaque artifact reference, server-measured duration, and atomic P17 terminal receipt.
8. Dispose the execution context/scope and unregister the active lease exactly once.

The outer worker boundary translates expected failures exactly once through P13 and passes whatever safe attempt metadata was established into the same atomic terminal-receipt transaction. An unexpected exception is logged locally with safe job correlation and stored only as `internal_error`. Every exit resolves/cleans a prepared or published artifact, cancellation registrations, P06 context, DI scope, active metadata, and lease. A stale worker may finish local cleanup but can never mutate the current job.

## Progress coalescing

Replace arbitrary phase strings and per-callback database writes with a closed `JobProgressPhase` and immutable `JobProgressSnapshot`. Publish only nonnegative, internally generated counts whose semantics are owned by the executor. Remove the current estimated-total/percentage heuristic unless P06/P10 supplies a truthful bounded estimate.

Each worker owns a synchronous, allocation-bounded coalescer:

- Assign a strictly increasing local progress sequence.
- Persist immediately on a closed phase change.
- Otherwise retain only the latest value and persist at most once per `ProgressMinInterval` when another callback arrives.
- Atomically fold the latest pending progress into any terminal transition.
- Reject a stale lease, lower/equal sequence, invalid value, or late callback without changing the snapshot.
- Create no timer/task per progress event and log no event per node/record.

Use `TimeProvider` and monotonic elapsed time for coalescing tests. Snapshot `Version` increments only when progress is actually persisted, not when an update is coalesced. Metrics count received, persisted, coalesced, invalid, and stale updates without job/owner tags.

## Cancellation, completion, and artifact races

### Explicit cancellation

The cancel endpoint sends `(owner SID, job ID)` directly to the state machine; it does not fetch then compare a record.

- `Queued`: one transaction removes the queue row and writes terminal `Cancelled(job_cancelled)`.
- `Running`: while holding the attempt publication gate when local, P13 claims `UserJobCancelled`; one transaction writes `CancellationRequested`. After commit, signal the active token outside the transaction.
- `CancellationRequested`: return the same accepted result and signal idempotently.
- `Cancelled`: return the existing terminal snapshot idempotently.
- `Completed`, `Failed`, or `Interrupted`: return `409 job_not_cancellable`; never overwrite it.
- Missing or wrong owner: return the same `404` shape.

If completion commits before cancellation obtains the status predicate, completion wins and the cancel command reports terminal conflict. If cancellation commits first, `TryComplete` cannot match `Running`; the worker removes any published artifact and acknowledges `Cancelled`. This is transaction order, not timestamp guessing.

### Publication protocol

Track the worker-local artifact state as `None`, `Prepared`, or `Published`:

1. On execution success, call P07 `PrepareAsync` while the lease is current.
2. Recheck the publication gate/lease and abort the preparation if a stop already won.
3. Call P07 `PublishAsync`. P07's valid commit marker is the artifact durability point.
4. Attempt `Running + matching lease -> Completed(reference)` in SQLite.
5. If it succeeds, transfer ownership of the artifact reference to the durable completed job.
6. If it fails for cancellation, interruption, failure, or stale lease, call `RemoveAsync` with an internal bounded cleanup token, never the disconnected request token.

A failure before publication calls `AbortAsync`. A failure after P07's durable marker but before job completion calls `RemoveAsync`; if removal fails, P07 keeps the object quarantined/charged and its own cleanup retries. P14 records a low-cardinality compensation failure but never attaches the reference to a non-completed job. A process crash in this window leaves a committed orphan that P07 recovers and expires. A crash after the SQLite completion commit retains both the job reference and P07 artifact.

Normal failure publication also holds the attempt gate and checks P13's winning stop claim. User cancellation maps to `Cancelled`, query deadline and dependency causes map to their P13 `Failed` descriptor, and host stopping maps to `Interrupted`. A late local exception, cancellation callback, progress event, or artifact completion sees a revoked/stale lease and cannot rewrite a terminal row.

## Structured retry and model selection

Replace `retry-with-alternate-model`, manual `QueryJob` construction, hard-coded fallback, and `[FORCE_MODEL: ...]` with an authenticated command such as:

```text
POST /api/query/jobs/{parentJobId}/retries
Idempotency-Key: <opaque bounded value>

RetryJobRequest
  ModelRoute: ConfiguredAlternate
  Reason: UserRequestedAlternateResult
```

The public request may select only closed server-supported routes, initially `ConfiguredAlternate`; it cannot supply a provider/model slug. A trusted P02 model resolver snapshots:

```text
ResolvedModelSelection
  Route: Primary | ConfiguredAlternate
  ExactConfiguredModelId
```

Resolve and validate the selection before admission, persist it immutably with the child command, and pass that exact model ID to P02 when the child runs. A model configuration change after admission does not silently change a queued attempt; P02's other validated provider/request settings remain its process-lifetime snapshot and P14 does not invent a second configuration-version counter. A missing/invalid alternate produces P13's typed configuration failure and no child job; there is no default or hard-coded fallback.

Every job carries:

```text
RetryLineage
  RootJobId
  ParentJobId (null only for root)
  AttemptNumber (1 for root, checked increment for child)
  RetryReason (closed enum)
```

The retry transaction is a second admission path and uses the same `BEGIN IMMEDIATE` idempotency-first and capacity protocol as `AdmitAsync`. It authorizes and reads the still client-retained parent, returns an existing same-key/same-fingerprint child before capacity checks, validates terminal/retry policy and `MaxAttemptsPerLineage`, then checks global queue, per-owner active, global/per-owner client-retained, database, and global/per-owner receipt-plus-reservation capacity inside that transaction. The first exceeded limit returns the same typed rejection as initial admission with no partial row. An accepted retry copies only the parent's bounded private query/context/requested limit, inserts the child command, FIFO queue row, idempotency row, and exactly one future-receipt/storage reservation, then commits atomically.

Retry does not reuse an artifact, executable plan, failure descriptor, provider response, or P06 tracker. The accepted child is immediately visible to P17's direct-child projection even if the HTTP response is lost or the child is not terminal. `Completed` may be retried for an explicitly requested alternate result; `Failed`/`Interrupted` may be retried only when P13 permits a new/delayed attempt and any server delay has elapsed. `Cancelled`, `never`, `retry_same_operation`, and `narrow_request` do not create an unchanged child. A private feedback receipt does not extend retry authorization after public job expiry, and no status polling/transport retry creates a new job.

## Authorization, privacy, and HTTP contract

Introduce one fail-closed `OwnerSubject` factory that reads the authenticated Windows SID and normalizes its canonical string. If no SID exists, job creation, lookup, cancel, preview, download, and retry fail; there is no SAM/UPN/display-name fallback. Store the SID only where authorization, quota, artifact ownership, and recovery require it.

Every user-facing state-machine read/write accepts the caller's `OwnerSubject` and includes `owner_sid = ?` in the same indexed query/transaction. Controllers never receive an unauthorized internal record to compare later. For job routes, missing and non-owner IDs return the same bounded `404` to reduce enumeration. Opaque random 128-bit job IDs remain correlation identifiers, not bearer credentials. P07 independently rechecks the same SID when opening the completed artifact.

Require TLS/Windows authentication through the existing deployment boundary. Status/command responses contain no raw query/context; ordinary logs contain opaque job ID, fixed transition/outcome, and safe P13 codes only. Database statements are parameterized, database contents are never dumped to logs or health responses, and telemetry never tags owner/job/model slug. P16 places the database in its fixed non-served state root and defines the logical least-privilege projection; P15 applies and verifies the actual ACL and host encrypted-volume prerequisite because SQLite stores the bounded private query/context needed for retry. Database-level encryption/key rotation remains out of scope unless separately approved.

The P17 receipt reader is the sole exception to public query minimization and remains entirely in-process. It returns query bytes only after the same indexed SID authorization and 24-hour window check, never through MVC serialization, health, logging, tracing, metrics, exception text, cache, or a general repository interface. P17 may use the bytes only as immediate input to P17's HMAC component through P16's secret-source seam and must clear the disposable snapshot; P14 never logs access arguments or the receipt. This preserves P16's prohibition on plaintext query/model/plan transcript logging.

API outcomes are closed and P13-rendered:

- Admission/retry success or same-key replay: `202`, stable `Location`, snapshot version, replay flag.
- Per-owner active limit: `429 job_owner_limit` with clamped `Retry-After`.
- Global queue/retained/database capacity: `503 job_queue_full` / `job_state_full` with clamped `Retry-After`.
- Drain/stopping: `503 job_service_draining` / `service_stopping`.
- Idempotency key reused for different input: `409 job_idempotency_conflict`.
- Invalid/missing key or bounded command violation: `400 request_invalid`.
- Cancel accepted/already requested/already cancelled: idempotent typed success; other terminal state: `409 job_not_cancellable`.
- Missing/non-owner job: identical `404 job_not_found`.

Register these finite codes/argument schemas in P13 rather than returning controller strings. Preserve P07's artifact-specific authorization/failure codes after a job has yielded its authorized reference.

## Public retention, feedback receipts, and database maintenance

All four terminal statuses receive `TerminalAt` and a finite client-visible maximum `TerminalAt + ClientTerminalRetention`. For `Completed`, the same completion transaction stores the P07 descriptor's absolute `artifact_expires_at` and sets `ClientExpiresAt = min(TerminalAt + ClientTerminalRetention, artifact_expires_at)`. Failed, Cancelled, and Interrupted use the maximum directly. Validate the configured public maximum is no longer than P07 canonical retention as a policy check, but rely on the descriptor clamp—not equal duration arithmetic—to guarantee that an unexpired completed public snapshot never deliberately outlives its artifact.

The same terminal transaction sets the separate receipt's `FeedbackExpiresAtUtc = TerminalAtUtc + FeedbackProjectionRetention`. The approved initial contract is exactly 24 hours for every terminal class, and while feedback is enabled P17's `FeedbackSubmissionWindowHours` must equal this value rather than reconstructing or extending it. This receipt window does not keep status, retry, idempotency, preview, download, artifact, context, or public error access alive. Client idempotency records expire with `ClientExpiresAt`; after that, create/retry replay may create a new job and the finite guarantee is documented. P17 feedback replay uses P17's separate idempotency contract while the receipt remains authorized.

Client cleanup is lease-based and never holds a SQLite transaction across P07 I/O:

1. In a short transaction, claim one client-expired terminal `jobs` row with a random cleanup lease/process identity. Public lookup treats it as not found even while physical cleanup retries.
2. For `Completed`, call P07 `RemoveAsync` outside the transaction. For other terminal states there is no artifact work.
3. On confirmed removal/already absent, transactionally delete the public/command job plus cascading client idempotency row under the cleanup lease. The independent receipt remains and contains no artifact reference or context.
4. On failure, retain the hidden job/reference, release/age the cleanup lease, and retry on the next bounded maintenance pass. Never free logical counts before deletion commits.

Receipt cleanup independently deletes `job_feedback_receipts` whose feedback expiry passed, in deterministic bounded batches with no external I/O. P17 resolution treats an expired receipt as not found even while deletion waits. Failed receipt deletion remains counted/charged and retries; it never reopens feedback access. At startup, clear only job cleanup leases owned by dead process instances and retry them. Public retained global/per-owner limits include hidden jobs pending artifact cleanup; receipt/reservation global/per-owner limits separately include every retained receipt or admitted future receipt. Either exhausted cap causes fail-fast admission when no expired row can be reclaimed.

Run bounded checkpoint and incremental-vacuum work only after cleanup batches and never while a request/worker transaction is open. Database allocation, WAL allocation, free-list pages, cleanup lag, and `SQLITE_FULL/BUSY` outcomes are observable. No maintenance path deletes the whole database, blindly recreates a corrupt schema, or follows a path outside P16's root.

## Startup recovery, operator drain, and host shutdown

### Startup recovery

After P16 has acquired the process-lifetime DataRoot lease and supplied `StateRoot`, migrate/validate the schema before opening admission or starting workers. Then reconcile in deterministic transactions of at most `MaintenanceBatchSize` rows per phase; each terminal row and its required receipt commit atomically within its batch:

- Change prior-process `Running` jobs to terminal `Interrupted(service_restarted)`, clear their leases, and retain them normally. Never requeue them.
- Change prior-process `CancellationRequested` jobs to terminal `Cancelled(job_cancelled)` because accepted user intent already won.
- Leave valid `Queued` rows queued in original FIFO order; expire those older than `MaxQueueAge` as `Failed(job_queue_timeout)` in the same bounded-batch discipline.
- Preserve every existing terminal row/version/outcome.
- Preserve a durable operator `Draining` mode; otherwise replace a prior host `Stopping` mode with the new instance's normal `Accepting` mode.
- Reconcile retained/active/queued metrics from the database and pulse workers only after readiness opens.

P07 separately recovers committed artifacts. A published artifact not attached before a crash is an unreachable owner-protected orphan that expires through P07; recovery never guesses a job/artifact relationship from paths or timestamps.

### Operator drain/resume for P15

Expose an authenticated operator contract, not a user job route:

```text
BeginDrain(expected release ID) -> drain generation/token + snapshot
GetDrainStatus(generation)      -> release ID, accepting_work, queued, running,
                                   cancellation_requested, quiescent
ResumeDrain(generation/token)   -> accepting snapshot or conflict
```

`BeginDrain` transactionally changes `Accepting -> Draining`, is idempotent for the same release/generation, and rejects new admissions immediately. Workers continue claiming and finishing all already queued/running jobs; drain never silently cancels them. `quiescent` is true only when durable queue count is zero and no `Running`/`CancellationRequested` lease exists. Because claiming is transactional, there is no hidden dequeued-but-not-running item.

P15 polls this bounded status and proceeds only with `accepting_work=false`, `queued=0`, `running=0`, and `quiescent=true`. If P15's external deadline expires before IIS mutation, `ResumeDrain` may return to `Accepting` only with the matching generation/token and only while host state is not stopping. An old token, wrong release, concurrent operator, or host stop returns conflict. Operator authorization and release identity come from the approved P15/P16/P20 boundary; P14 does not accept an arbitrary caller-supplied identity as trusted.

### Host shutdown

Host stopping is irreversible for that process:

1. Transactionally set service mode `Stopping` and reject admission/claims.
2. Leave durable `Queued` rows unchanged for the next process; do not misclassify or lose them.
3. Under each active attempt publication gate, preserve any earlier P13 claim; otherwise claim `HostStopping`, transition `Running` to `Interrupted(service_stopping)`, revoke its lease, and then signal cancellation outside the transaction. Finalize already `CancellationRequested` rows as `Cancelled` rather than rewriting user intent.
4. Complete/wake the in-process wait primitive and await every fixed worker and maintenance task through one bounded join.
5. Any late worker output fails its lease and performs P07 compensation only.

The shutdown join is sized against the landed P02/P06/P09/P07 logical cancellation bounds and validated at startup. No task is abandoned by application code and no `w3wp` process is killed. If a dependency violates its bounded logical-stop contract and the host's own stop token expires, record a fixed critical shutdown-timeout outcome and rely on startup recovery to mark any still-persisted lease `Interrupted(service_restarted)`; planned deployment must never use this as a bypass because P15 requires quiescence before stopping IIS.

## Configuration

Add validated P14 options under the P16 configuration model. Recommended initial values, pending owner approval:

```json
"Jobs": {
  "MaxConcurrentJobs": 3,
  "MaxQueuedJobs": 64,
  "MaxActiveJobsPerOwner": 10,
  "MaxRetainedJobs": 512,
  "MaxRetainedJobsPerOwner": 64,
  "MaxFeedbackReceipts": 512,
  "MaxFeedbackReceiptsPerOwner": 64,
  "MaxAttemptsPerLineage": 4,
  "MaxQueueAgeMinutes": 30,
  "ClientTerminalRetentionMinutes": 120,
  "FeedbackProjectionRetentionHours": 24,
  "ProgressMinIntervalMilliseconds": 500,
  "MaintenanceIntervalMinutes": 5,
  "MaintenanceBatchSize": 32,
  "StateBusyTimeoutMilliseconds": 2000,
  "ShutdownJoinSeconds": 30,
  "MaxQueryUtf8Bytes": 16384,
  "MaxContextUtf8Bytes": 49152,
  "MaxIdempotencyKeyBytes": 128,
  "MaxOutcomeUtf8Bytes": 16384,
  "MaxDatabaseBytes": 134217728,
  "MaxWalBytes": 16777216,
  "StateEmergencyReserveBytes": 8388608,
  "MaxStateStorageBytes": 167772160
}
```

Validate on startup:

- Every count, duration, byte limit, batch, and concurrency value is finite and positive.
- `MaxConcurrentJobs <= MaxQueuedJobs <= MaxRetainedJobs`.
- `MaxActiveJobsPerOwner <= MaxRetainedJobsPerOwner <= MaxRetainedJobs`.
- `MaxActiveJobsPerOwner <= MaxFeedbackReceiptsPerOwner <= MaxFeedbackReceipts`; receipt capacity counts both retained receipts and reservations for every accepted nonterminal job.
- Command/outcome/query receipt bounds fit the main-database budget at simultaneous maximum client rows and receipt rows with documented schema/index overhead; reject arithmetic overflow.
- Client terminal retention is no longer than P07 canonical retention and longer than maintenance interval. Feedback projection retention is at least the client maximum; while feedback is enabled P17's positive `FeedbackSubmissionWindowHours` must equal it (24 hours for both approved initial values).
- Queue age is longer than the busy timeout and does not consume P06 active time.
- Progress interval is within a documented operational range, for example 100 ms through 10 seconds.
- Idempotency keys use a bounded printable ASCII grammar and minimum entropy/length policy; store only a domain-separated hash.
- The fixed page size, `max_page_count`, 2 MiB automatic-checkpoint threshold, `MaxWalBytes`, per-transaction row/batch allowance, every outstanding terminal-receipt allocation, SHM allowance, `StateEmergencyReserveBytes`, and `MaxStateStorageBytes` satisfy the explicit storage equation. Seeded maximum-row tests must prove that all simultaneously admitted active and queued reservations can convert through active completion, individual queued cancellation, and bounded expiry/recovery batches without a successful checkpoint while staying below the absolute budget. P16's resource validator must accept the configured state budget and P15's host preflight must verify the required free-space/control projection.
- Shutdown join is consistent with the landed logical cancellation bounds; an unsafe production combination fails readiness rather than silently shortening dependency work.

Zero never means unlimited. Checked-in defaults remain finite.

## Metrics and structured events

Use `System.Diagnostics.Metrics` and P13-safe structured logs. Add:

```text
adquery.job.admission                 counter
adquery.job.idempotency               counter
adquery.job.transition                counter
adquery.job.queue_depth               observable gauge
adquery.job.active                    observable gauge
adquery.job.retained                  observable gauge
adquery.job.feedback_receipts         observable gauge
adquery.job.feedback_target           counter
adquery.job.queue_wait                histogram (ms)
adquery.job.execution_duration        histogram (ms)
adquery.job.progress                  counter
adquery.job.lease_rejected            counter
adquery.job.artifact_compensation     counter
adquery.job.retention_cleanup         counter
adquery.job.restart_recovery          counter
adquery.job.drain_duration            histogram (ms)
adquery.job.state_transaction         histogram (ms)
adquery.job.state_bytes               observable gauge
```

Allowed tags are closed status/outcome, transition, admission rejection reason, progress disposition, model route (`primary`/`alternate` only), feedback-target result (`found`, `not_found`, `not_terminal`), cleanup result, recovery result, and service mode. Never tag owner SID/name, job/root/parent ID, idempotency key/hash, exact model/provider ID, query/context/fingerprint, failure detail, LDAP values, or path. Queue/running/client-retained/receipt gauges read a nonblocking metrics projection updated only after committed transactions and reconciled from SQLite at startup; metric callbacks do not execute SQL.

Emit one info event for admission/terminal/drain milestones and one warning/error for typed abnormal outcomes at their owning boundary. Do not log every progress callback, database statement, status poll, or retry lookup. P16 owns sinks/redaction/export; P14 supplies only safe fields.

## Deterministic verification

Use file-backed SQLite databases beneath unique test temporary directories; in-memory SQLite alone cannot prove restart, commit, WAL, file-size, or migration behavior. Inject `TimeProvider`, job/process ID factories, worker ordinal, P13 contexts, and bounded fakes for P02/P06/P07/P09/P12. Use `TaskCompletionSource`/barriers for races and never `Thread.Sleep`, live network, LDAP, IIS, or provider credentials.

Required tests:

### Schema, immutability, and configuration

1. Empty database migrates to the exact schema; each prior fixture migrates once; a newer/failed/corrupt schema fails without deletion or volatile fallback.
2. Snapshot records and nested collections are deeply immutable; mutating source DTOs/results cannot change a stored or returned snapshot.
3. Every status/route/terminal check constraint and version increment is enforced; unknown values and overflow fail closed.
4. Invalid options, zero-as-unlimited, unsafe DataRoot path, retention mismatch, database-size arithmetic, and package/schema mismatch fail startup.
5. Query/context/outcome/idempotency/model/SID byte bounds are exact: exactly at limit succeeds and the next encoded byte fails before insertion.

### Atomic admission, idempotency, and persistence

6. Hundreds of gated concurrent submissions cannot exceed global queue, global/client retained, per-owner active/client retained, or global/per-owner feedback receipt-plus-reservation limits; rejected transactions leave no job/queue/idempotency/reservation row.
7. Concurrent same-owner/same-key/same-fingerprint submissions return exactly one job; a different fingerprint returns conflict; another owner/operation is independently scoped.
8. Cancellation before the pre-commit gate leaves no job. Disconnect after commit leaves one queued job, and same-key replay returns it.
9. Inject a commit-response ambiguity; fresh-key lookup distinguishes committed from rolled-back without creating a duplicate.
10. Crash/reopen preserves queued FIFO order and idempotency. A commit-before-wake crash is discovered on startup.
11. Query-plan assertions use the intended indexes for admission, FIFO claim, owner lookup, lineage/direct children, client expiry, feedback expiry, and receipt resolution.

### State machine, leases, and workers

12. Exhaustively test every allowed transition and every forbidden source/target pair, including terminal immutability.
13. Only one of many concurrent claims obtains a lease; duplicate/stale lease progress/completion/failure updates affect zero rows.
14. Exactly `MaxConcurrentJobs` tracked worker loops run; observed execution never exceeds it and available workers become work-conserving under a burst.
15. After the durable queue becomes empty, no claim/query occurs until the versioned signal changes. Commit at each check/wait boundary cannot strand work.
16. Removing the signal or crashing after commit still recovers on restart; the signal itself retains no job ID and remains constant-memory.
17. Hosted stop does not return while a controllable worker/cleanup task remains active inside the configured logical bound; no `Task.Run`, discarded task, work polling delay, or semaphore/dequeue gap remains.

### Cancellation, failures, progress, and artifacts

18. Deterministically race owner cancel and completed transaction in both orders. Exactly one wins; cancel-first removes a published artifact and completion-first retains it.
19. Cancel before claim removes queue membership; cancel immediately after claim reaches `CancellationRequested`; repeat cancel is idempotent; other terminal states return conflict.
20. P06 deadline, dependency timeout, explicit user cancel, host stop, restart, compiler failure, artifact failure, and internal failure produce distinct exact P13 status/descriptors.
21. P13's first stop claim cannot appear between the publication-gate check and terminal commit; a later claim/fault cannot overwrite the winner.
22. Prepare failure aborts with no reference; publish failure produces no completion; published-then-stale removes once; removal failure stays quarantined/charged without attaching a reference.
23. Simulated crash after P07 marker but before SQLite completion yields `Interrupted(service_restarted)` plus a P07-recoverable orphan; crash after SQLite completion preserves the reference.
24. Budget/compiler/execution failure publishes no artifact and P14 never recomputes P06/P11 aggregation.
25. Ten thousand progress callbacks under fake time persist only the configured bound; phase changes are immediate, terminal transition folds the latest update, and late/stale/invalid callbacks are ignored.

### Retry, authorization, retention, and lifecycle

26. Retry stores exact root/parent/attempt/reason, copies bounded private input, resolves the configured alternate once, and recompiles a new attempt.
27. Missing alternate config creates no job; no hard-coded model, context directive, raw client model ID, or model-name capability heuristic remains.
28. Retry eligibility honors P13 disposition/delay, completed-user choice, cancelled/narrow/never rejection, idempotency, and lineage attempt cap. Gated concurrent retries cannot exceed global queue, per-owner active, global/per-owner client-retained, database, or receipt-plus-reservation caps; every accepted child has exactly one terminal storage/receipt reservation and every rejection is row-free.
29. Two principals with the same SAM account name but different SIDs cannot read/cancel/retry/open one another's jobs; missing SID fails closed; non-owner and missing shapes match.
30. Golden public snapshots, P13 problems, logs, and metric tags contain none of seeded query/context/provider/LDAP/path/SID/model-ID/idempotency secrets.
31. Every terminal class inserts exactly one immutable receipt in the same transaction/version; forced receipt failure rolls back the terminal update and retains its reservation.
32. Receipt fixtures preserve exact P02 effective provider/model/route, P12 fingerprint/compiler/policy versions, P13 outcome, P07 row/publication fact, monotonic duration, lineage, and UTC times; pre-provider/restart nullable tuples are exact and no raw plan/context/body/reference enters.
33. `ResolveOwnedTerminalAsync` returns indistinguishable NotFound for missing/foreign/expired, NotTerminal for an owned active job, and Found only for the exact SID-authorized current receipt. It performs one bounded indexed transaction and never exposes owner SID.
34. Seeded query bytes exist only in the private disposable receipt, are cleared after the fake P17 HMAC consumer, and never appear through `ToString`, serialization, public API, cache, logs, metrics, health, or exception fixtures.
35. Concurrent direct retries, including active jobs and terminal jobs present in both tables, produce a distinct direct-child count and deterministic highest-attempt/latest-ID result without changing `TerminalJobVersion`; a lost retry response still appears after idempotent recovery.
36. At two hours, all public terminal routes/retry/client idempotency and P07 artifact access are gone while the authorized private receipt remains resolvable; at 24 hours the same lookup is NotFound. Access extends neither clock.
37. Completed, Failed, Cancelled, and Interrupted public rows all expire from `TerminalAt`; client access does not extend expiry and client idempotency deletes only with the public row.
38. Completed client cleanup calls P07 removal before public row deletion, respects artifact leases, retains failed cleanup, and safely resumes an abandoned cleanup lease after restart without deleting the independent receipt.
39. Expired receipt cleanup is bounded and independent; failed deletion remains counted but inaccessible. Global/per-owner client and receipt caps include pending cleanup/reservations and prevent logical growth during repeated failures.
40. Seeded maximum job-plus-receipt rows prove main/WAL/SHM thresholds and conversion of every simultaneously admitted active/queued reservation through completion, individual cancellation, and bounded expiry/recovery without checkpoint, while `SQLITE_FULL/BUSY` maps to typed capacity/unavailable outcomes.
41. Startup processes every recovery class in deterministic transactions of at most `MaintenanceBatchSize`, keeps valid queued work, marks prior Running as `Interrupted(service_restarted)`, finalizes prior cancellation request as Cancelled, atomically creates their receipts, and never automatically executes the interrupted attempt.
42. Drain closes admission, drains durable queue/running work to exact quiescence, survives status polling, and resumes only with matching release/generation/token. Host stop cannot resume.
43. Host stop leaves queued rows durable, makes running work Interrupted unless an earlier P13 cause won, preserves user cancellation, atomically creates receipts, revokes leases, compensates late artifacts, and awaits tracked loops.

## Red/green and mutation proof

For every test-bearing slice:

1. Add the focused test and confirm it fails against the current or deliberately incomplete behavior.
2. Implement the smallest slice and confirm the focused test passes.
3. Temporarily mutate/revert only the protected behavior without rewriting history.
4. Confirm the focused guard fails for the intended reason.
5. Restore the implementation, run focused tests, then run P01's canonical verification.
6. Commit only the restored single slice. Leave no mutation or test database in the worktree.

Mandatory mutations include:

- Replace the transactional capacity predicate with count-then-insert; concurrent exact-cap test exceeds the limit.
- Remove the retry path's shared capacity predicate or future-receipt reservation; concurrent retry guard exceeds a cap or accepts a child that cannot terminalize.
- Commit job and queue/idempotency in separate transactions; injected failure exposes an orphan or duplicate.
- Remove the fresh lookup after ambiguous commit; disconnect/idempotency guard cannot determine acceptance.
- Make the wake signal an unbounded ID channel or omit the race-closing generation check; bounded/lost-wake test fails.
- Remove lease ID/epoch or expected-status predicate; stale-worker test overwrites a terminal/newer state.
- Restore `Task.Run`, polling delay, or dequeue-before-capacity logic; tracked/no-poll architecture and behavior guards fail.
- Let `CancellationRequested` complete; cancel-first race retains an artifact or reports Completed.
- Mark host/restart as Cancelled or requeue prior Running; typed recovery guard fails.
- Move SQLite completion before P07 publication; crash/publication guard exposes an invalid result reference.
- Omit P07 removal after a lost completion race; compensation guard finds a retained orphan beyond P07's intentional crash case.
- Persist every progress callback; fake-time write-count guard exceeds the exact bound.
- Reintroduce context model directives or hard-coded fallback; structured-selection fixture fails.
- Authorize by SAM name or fetch-then-compare; SID collision/TOCTOU guard succeeds incorrectly.
- Clean only Completed or delete the row before artifact removal; all-terminal retention/order guard fails.
- Commit a terminal job without atomically inserting/converting its reserved feedback receipt; receipt/rollback guard finds a terminal metadata gap.
- Return the private receipt without the SID predicate, expose its generated `ToString`, or retain query bytes after disposal; P17 authorization/privacy guards fail.
- Trust a browser retry flag or count a terminal child twice across job/receipt tables; direct-child projection guard fails.
- Delete the feedback receipt with the two-hour public job or let it authorize public status/retry/artifact access; split-window guards fail.
- Link `RequestAborted` after commit; post-accept disconnect wrongly cancels the job.

## Implementation slices

Each slice is one commit. Run focused and canonical verification and record the mutation proof before starting the next. Do not amend, squash, or combine slices.

### Slice 1 — Immutable contracts and validated options

Commit intent: `refactor: define immutable query job contracts`

- Add closed statuses, command/snapshot/progress/lineage/model/lease value types, public projection, and internal disposable feedback-target contract.
- Add finite P14 options and startup cross-option validation without wiring current behavior.
- Add immutability, serialization, byte-bound, and invalid-configuration tests.

### Slice 2 — Audited durable SQLite state machine

Commit intent: `feat: persist versioned query job state`

- Add the P03-audited pinned SQLite package and lock-file changes.
- Consume P16's state path, add job/queue/idempotency/feedback-receipt schema, migration/version checks, constraints/indexes, transactional transition APIs, and size controls.
- Add migration, transition-matrix, lease, query-plan, full/busy, and restart persistence tests.

### Slice 3 — Atomic admission and idempotency

Commit intent: `feat: make job admission atomic and idempotent`

- Implement transactionally bounded global/per-owner admission, future-receipt reservation, queue insertion, request fingerprints, key replay/conflict, and acceptance-point recovery.
- Remove scan-then-store admission and zero-as-unlimited behavior.
- Add concurrent caps and disconnect/ambiguous-commit guards.

### Slice 4 — Durable queue and fixed tracked workers

Commit intent: `refactor: run jobs on fixed durable workers`

- Add the constant-memory versioned wake signal and transactional FIFO claims.
- Replace unbounded ID channel, polling, semaphore, and fire-and-forget tasks with fixed tracked loops.
- Add lost-wake, FIFO, concurrency, no-poll, scope disposal, and tracked-stop tests.

### Slice 5 — Lease-bound execution and progress

Commit intent: `refactor: bind job execution to one lease`

- Integrate P02/P06/P09/P12 execution inside one attempt lease and capture only the safe effective provider/model, compiler fingerprint/version, and monotonic-duration facts required by the terminal receipt.
- Remove mutable plan/cancellation fields and duplicate aggregation behavior.
- Add P13 publication gate and coalesced closed progress.
- Add stale lease, typed execution outcome, one-context/compiler, and fake-time progress tests.

### Slice 6 — Atomic artifact completion and cancellation

Commit intent: `fix: order job completion and cancellation atomically`

- Integrate P07 prepare/publish/abort/remove with lease-checked completion.
- Implement queued/running cancellation outcomes, first-winner semantics, and one receipt insertion/reservation conversion in every terminal transaction.
- Add both race orders, crash windows, artifact compensation, and late-callback tests.

### Slice 7 — Structured retries and model routing

Commit intent: `refactor: persist structured query retry lineage`

- Add retry transaction, lineage/attempt limit, P13 eligibility, trusted P02 model resolution, and indexed direct-child facts.
- Remove context directives, old manual job construction, and hard-coded alternate fallback.
- Add lineage, configuration snapshot, eligibility, cap, and retry-idempotency tests.

### Slice 8 — SID-scoped versioned job APIs

Commit intent: `fix: authorize query jobs by canonical subject`

- Move owner checks into state-machine commands, use canonical SID, return minimized versioned DTOs/ETags, and register P13 job codes.
- Migrate status, cancel, retry, preview, and download; remove mutable controller check-then-act and raw query status output.
- Make the minimum current browser/API compatibility change needed to supply idempotency; defer polling redesign to P19.
- Add HTTP golden, SID collision, TOCTOU, cache-header, and privacy tests.

### Slice 9 — Private P17 terminal receipt

Commit intent: `feat: authorize retained feedback targets`

- Implement `IQueryJobFeedbackTargetReader`, exact SID/window predicates, disposable query bytes, immutable receipt projection, and bounded direct-child resolution.
- Split two-hour public/client/idempotency/artifact availability from the private 24-hour all-terminal receipt without exposing a new public route.
- Add exact field-provenance, same-SID, missing/foreign/active/expired, query-clearing, direct-child de-duplication/latest, and two-hour/24-hour timeline tests.

### Slice 10 — Retention, restart recovery, and database maintenance

Commit intent: `feat: bound query job retention and recovery`

- Add public job and receipt expiry, cleanup leases, P07 removal ordering, bounded independent batches, client/receipt caps, checkpoint/vacuum, and startup reconciliation.
- Recover queued work, interrupt prior Running, finalize prior cancellation intent, atomically insert recovery receipts, and pulse workers.
- Add every terminal/cleanup failure/restart/database-bound guard.

### Slice 11 — Drain, shutdown, and telemetry

Commit intent: `feat: drain query jobs before host shutdown`

- Add persistent service modes, operator drain/status/resume generation contract, host-stop transitions, tracked join, and P15 handoff.
- Add safe metrics/events and nonblocking committed-state projections.
- Add quiescence/resume/host-race/shutdown-bound/metric-cardinality tests and update operational guidance.

## Acceptance criteria

- One SQLite-backed state machine is the sole production writer and survives process restart; there is no volatile fallback.
- Immutable schema-versioned snapshots and checked monotonic versions replace mutable `QueryJob` references.
- Global queue, per-owner active, global/per-owner client rows, global/per-owner receipt reservations/receipts, command/outcome/query, retry, progress, and database bounds are finite, validated, and concurrency-proven.
- Admission, queue insertion, and idempotency are one transaction with a precise post-commit disconnect contract.
- The database is the queue; the constant-memory wake signal cannot lose committed work and workers perform no polling.
- Exactly the configured number of tracked worker loops exist; no fire-and-forget job/cleanup task or semaphore/dequeue gap remains.
- Every progress and terminal write requires the current lease; stale/late work cannot mutate a snapshot.
- User cancel, success, P06 deadline, dependency failure, host stop, and restart races have deterministic P13 outcomes; `Interrupted` is distinct and prior Running work is never auto-retried.
- P07 publication always precedes `Completed`; every lost race aborts/removes the artifact under the documented crash-recovery contract.
- Completed, Failed, Cancelled, and Interrupted public jobs all expire under the two-hour client policy; their independent private receipts expire at 24 hours and every cleanup failure remains bounded/charged.
- Every terminal transition atomically creates one P17 receipt with exact P02/P12/P13/P07/server facts. The internal reader authorizes by SID, distinguishes active from not-found without owner leakage, returns disposable query bytes only for HMAC, and reports bounded direct accepted children without changing terminal version.
- Retry has immutable root/parent/attempt/reason and trusted configured model selection; no prompt directive, arbitrary client model, or hard-coded fallback remains.
- Every job operation is authorized inside the state boundary by canonical Windows SID, and public/log/metric outputs pass seeded secret tests.
- P15 can close admission, observe exact quiescence, and safely resume only before host stop; graceful host stop leaves queued work durable and revokes active leases.
- File-backed restart, race, crash-window, mutation, and canonical verification all pass without live external systems.
- Every implementation slice is separately committed and the worktree is clean.

## Rollback

Use new revert commits; never rewrite reviewed history.

- Revert consumers in reverse order before removing a schema/API they use; disable/revert P17 before removing its receipt reader/table/window.
- Retain the SQLite database and P07 artifacts during code rollback. Never delete, recreate, or downgrade durable state automatically.
- A rollback binary must declare the highest schema it understands. If the database is newer, fail readiness and restore a compatible binary; do not fall back to the old in-memory store.
- Package rollback must retain a non-vulnerable P03-audited graph and matching lock files.
- If API migration is reverted temporarily, keep SID authorization, typed P13 errors, and P07 opaque references; never restore SAM-name authorization, raw error text, context model directives, or mutable result caching.
- If worker wiring is reverted, stop admission and drain first. Never run old and new schedulers against the same database simultaneously.
- Operational rollback uses P15/P16's compatible release/data backup procedure. Git revert alone is not a database rollback.

## Risks and mitigations

- **SQLite adds a native dependency and schema lifecycle.** Pin/audit it through P03, test publish/startup on the supported Windows runtime, version every migration, and fail closed on unknown schema.
- **The database contains sensitive query/context needed for durable retry and query fingerprinting.** Public command/context data expires after two hours, but the exact query remains in the private receipt for 24 hours. Keep strict byte/receipt bounds, disposable in-process access, P16's non-web-root state path/logical projection, P15-applied least-privilege ACL and encrypted-volume prerequisite, no dumps/logging, and explicit owner approval if database-level encryption is required.
- **WAL can grow under long readers.** Materialize/close every read, checkpoint at 2 MiB and truncate at the 16 MiB threshold, preflight the 160 MiB total budget, reserve 8 MiB for bounded active terminal writes, measure each file separately, and fail admission/readiness when reclamation cannot preserve that headroom.
- **SQLite is a single-writer bottleneck.** Transactions are short/indexed and progress is coalesced; measure transaction latency/busy outcomes before considering a more complex store.
- **A committed job may lose its HTTP response.** Mandatory idempotency plus fresh post-ambiguity lookup makes the outcome recoverable.
- **A process can crash after artifact publication but before job completion.** Never guess completion; interrupt the attempt on restart and let P07 recover/expire the owner-protected orphan.
- **A restart cannot know whether external directory work had effects.** Directory queries are intended read-only, but prior Running is still never auto-executed; explicit retry creates lineage and a new attempt.
- **Queue FIFO is not weighted fairness.** Per-owner active/retained limits prevent one owner from unbounded admission; add fair scheduling only from observed starvation evidence.
- **Retention cleanup can fail.** Keep rows/artifacts charged, cap retained state, expose metrics, and fail admission instead of deleting unexpired data.
- **A 24-hour receipt window can become the admission bottleneck.** Reserve one receipt at admission, cap receipts globally/per owner, include the worst-case duplicate public-row/receipt footprint in database sizing, and tune only from safe capacity/rejection metrics; never omit a terminal receipt after accepting a job.
- **Shutdown bounds depend on lower layers.** Consume P02/P06/P09/P07 finite cancellation contracts, require P15 drain before planned IIS stop, and surface a critical timeout instead of abandoning a task silently.
- **P16/P15 contracts may land with different names.** Adapt type/route names while preserving DataRoot/ACL/single-process and close-admission/quiescence ownership; do not create duplicate policies.

## Open owner decisions

### Decision 1 — Durable job store

Choose transactional SQLite under P16's protected `DataRoot` or retain a corrected volatile in-memory coordinator. Recommendation: SQLite. It makes accepted queue/idempotency/terminal state recoverable and closes the store-then-enqueue crash gap; the cost is one audited native dependency, migrations, and protected-at-rest query data.

Blocked until decided: Slices 2–11.

### Decision 2 — Idempotency requirement

Choose mandatory idempotency keys for create/retry or preserve optional unkeyed calls. Recommendation: require a bounded key because the application is not yet in production; otherwise response loss can never distinguish an accepted job from a safe retry. The current browser adds a generated key in Slice 8.

Blocked until decided: Slices 3, 7, and 8.

### Decision 3 — Initial capacity and retention

Approve three workers, 64 queued globally, ten active per owner, 512/64 client-retained globally/per owner, 512/64 feedback receipts globally/per owner, four lineage attempts, 30-minute queue age, two-hour client-visible terminal retention, 24-hour private feedback receipt retention, 500 ms progress writes, a 128 MiB main database, 16 MiB WAL threshold, 8 MiB terminal reserve, and 160 MiB total state-storage budget. Recommendation: begin with these finite conservative values and tune only from low-cardinality utilization/rejection metrics.

Blocked until decided: Slices 1–4, 9, and 10.

### Decision 4 — Cancellation versus completion

Choose transaction-order first winner or allow a cancellation request to rewrite a concurrently completed result. Recommendation: first committed state wins; cancel-first prevents completion and removes its artifact, while completion-first returns a terminal conflict. This is linearizable and matches P13's first-cause rule.

Blocked until decided: Slices 5 and 6.

### Decision 5 — Explicit retry policy

Choose retries from every terminal state or restrict unchanged attempts. Recommendation: allow Completed for a user-requested alternate result and Failed/Interrupted only when P13 permits a new/delayed attempt; reject Cancelled, `never`, `retry_same_operation`, and `narrow_request`, with four attempts per lineage.

Blocked until decided: Slice 7.

### Decision 6 — Restart and shutdown behavior

Choose automatic resumption of prior Running work or conservative interruption. Recommendation: resume only queued commands; mark prior Running `Interrupted(service_restarted)`, never auto-retry it, and on graceful stop leave queued durable while interrupting/revoking active leases. P15 drain prevents this during planned deployment.

Blocked until decided: Slices 10 and 11.

### Decision 7 — At-rest protection boundary

Choose P16's logical protection plus P15-applied ACL/encrypted-volume protection, or require application/database-level encryption before storing query/context. Recommendation: use the approved least-privilege ACL and encrypted production volume initially; database encryption introduces a provider and key lifecycle that needs a separate security design. If those host controls cannot be verified, block durable job storage rather than write plaintext under weak permissions.

Blocked until decided: P16/P15 production promotion and Slice 2 deployment readiness.

### Decision 8 — Feedback submission projection

Choose a private 24-hour terminal receipt or extend the complete public job/artifact surface to the feedback window. Recommendation: retain the minimal SID-authorized P17 receipt independently for 24 hours. It supports authoritative feedback and retry-child facts without extending status, retry, idempotency, preview/download, artifact, context, or public error exposure beyond two hours.

Blocked until decided: Slices 1–3, 5–7, 9, and 10, plus the P17 handoff.

## Advisory review

Record no more than three headless Claude review rounds. Each round records the configured model reported by the invocation envelope, maximum effort, material findings, the applied revision or retained disagreement, and final assessment. If round 3 still requires changes, apply them and explicitly record that the final repair was not independently re-reviewed.

### Round 1 — 2026-07-21T23:13:02Z

**Reviewer:** Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict:** Accepted; three optional precision revisions applied

- Confirmed transactional admission/idempotency, constant-memory lost-wake closure, lease/P13 publication arbitration, P07 publication compensation, SID privacy, bounded workers, and P15 drain/host-stop ownership have no material correctness or implementability defect.
- Clamped completed snapshot expiry to P07's returned absolute artifact expiry instead of relying on equal retention durations.
- Removed the undefined provider-configuration version and retained the exact resolved configured model ID as the durable selection contract.
- Made main database, WAL threshold, per-transaction allowance, SHM, terminal emergency reserve, and total state-storage validation explicit. These optional edits proceed to round 2 because they changed durable-storage detail after the accepted verdict.

### Round 2 — 2026-07-21T23:17:54Z

**Reviewer:** Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict:** Accepted; no findings

- Confirmed completed-job expiry is consistently clamped to P07's real absolute descriptor expiry and cleanup ordering remains coherent.
- Confirmed the undefined provider-configuration version has no dangling reference and the persisted exact configured model ID fully defines queued model selection.
- Confirmed the main database/WAL/SHM/write/reserve equation, finite option values, startup validation, and seeded terminal-reserve proof are internally consistent with typed no-fallback capacity behavior.
- Found no material correctness, atomicity, boundedness, dependency, shutdown/recovery, security, or cold-implementability defect in the reviewed revision.

After that verdict, the concurrently drafted P16 plan established `IDataPaths.StateRoot` (`<DataRoot>/state`) plus a logical protection projection, with P15 applying/verifying actual host controls. P14 was aligned from its provisional `<DataRoot>/jobs` wording to that exact ownership boundary. P17 then established a 24-hour submission window and an internal authoritative feedback-target contract; P14 added the independently retained SID-authorized receipt, exact P02/P12/P13/P07/server fields, disposable query bytes, direct-child retry projection, capacity accounting, and split public/private expiry. Because these changes followed round 2, final round 3 reviews both cross-plan alignments and their adjacent storage, security, atomicity, retention, and boundedness statements.

### Round 3 — 2026-07-21T23:40:33Z

**Reviewer:** Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict:** Revisions required; all four required revisions and both optional precision edits applied

- Confirmed the P14/P17 reader signature, immutable field set, authorization outcomes, exact two-hour/24-hour split, and P16 `StateRoot`/lease/logging boundaries match their dependency plans.
- Required retry to use the same transactional idempotency-first capacity checks as root admission and reserve one future receipt/storage allocation for every accepted child. Added the corresponding concurrent-cap and mutation guards.
- Required storage admission and emergency-reserve proofs to cover receipt writes from active leases, queued cancellation, and bounded expiry/recovery batches. Added charged terminal allocations, a stronger no-checkpoint proof for conversion of every simultaneously admitted reservation, and explicit non-lease reserve consumers.
- Replaced the unbounded startup `run one transaction` wording with deterministic transactions of at most `MaintenanceBatchSize` per recovery phase, each atomically creating terminal receipts.
- Added explicit covering indexes for direct children in both current jobs and retained receipts.
- Clarified null duration/provider/model fields for jobs terminalized before active execution and that P16, not P14, acquires the outer DataRoot lease.
- No fourth review is permitted. The final repair above is therefore not independently re-reviewed; implementation must prove it through the named deterministic capacity, storage, query-plan, and recovery guards before any slice can be accepted.
