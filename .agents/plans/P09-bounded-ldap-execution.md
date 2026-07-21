# P09 — Bounded and Timeout-Aware LDAP Execution

Status: **Reviewed — implementation is not authorized**

Owner approval: Pending

Implementation dependencies: P01 verification foundation and P06 query-work budgets must land first. P08 template/filter optimization should land before P09 service wiring; if it does not, re-inventory the directory-operation call graph immediately before Slice 3 and preserve the ownership boundary below.

Review status: Round 3 revisions applied; final text was not re-reviewed because the three-round limit was reached

## Problem

`ActiveDirectoryService` presents asynchronous signatures but performs blocking ADSI work on the caller or thread-pool thread. `DirectorySearcher.FindAll()`, result enumeration, `DirectoryEntry.RefreshCache()`, schema/property reads, and RootDSE discovery have no process-wide admission bound. Search calls set neither `ServerTimeLimit`, `ServerPageTimeLimit`, nor `ClientTimeout`, cancellation is observed only before or between some blocking calls, and `LookupAsync` creates a separate four-way `Parallel.ForEach` for every caller. Concurrent synchronous requests, jobs, CSV enrichment, expansion, and traversal can therefore multiply blocking LDAP work, starve the managed thread pool, and remain stuck after the caller's deadline.

`System.DirectoryServices` does not offer reliable cooperative cancellation for every blocking ADSI operation. A caller-facing timeout cannot be treated as physical completion: releasing capacity or starting a replacement worker while the original call is still blocked would make the advertised concurrency limit false. The implementation needs an explicit containment model for that limitation.

## Repository evidence

- `csharp/Services/ActiveDirectoryService.cs:30-83` returns `Task.FromResult` around a synchronous `DirectorySearcher.FindAll()` and enumeration. Cancellation is checked only after `FindAll()` returns.
- The searcher at `csharp/Services/ActiveDirectoryService.cs:59-64` sets `PageSize = 500` but none of `ServerTimeLimit`, `ServerPageTimeLimit`, or `ClientTimeout`. The native contracts are distinct: total server search time, time spent producing an individual page, and client wait time, respectively.
- `ExpandGroupMembersAsync` performs blocking `DirectoryEntry.RefreshCache("member")` calls at `csharp/Services/ActiveDirectoryService.cs:95-126`; its broad catch can turn a future timeout or cancellation into best-effort partial success.
- `LookupAsync` at `csharp/Services/ActiveDirectoryService.cs:155-206` runs one `Parallel.ForEach` per invocation with `MaxDegreeOfParallelism = 4`. Multiple requests multiply that limit, consume thread-pool workers, and return result order nondeterministically.
- Lookup entry creation, `RefreshCache`, `SchemaClassName`, and property reads all happen inside that parallel loop and can bind or fetch data synchronously.
- `CreateDirectoryEntry` falls back to synchronous RootDSE discovery at `csharp/Services/ActiveDirectoryService.cs:313-335`; a search without `ActiveDirectory:RootPath` can therefore make one unbounded RootDSE call followed by an unbounded search.
- `DirectoryPlanExecutor` invokes all four `IActiveDirectoryService` methods, and `CsvEnrichmentService` invokes `SearchAsync`. Both synchronous and queued query paths eventually share the scoped `ActiveDirectoryService`, but there is no process-wide LDAP coordinator.
- `Program.cs` registers `IActiveDirectoryService` as scoped and the job infrastructure as singleton. `appsettings.json` has only `ActiveDirectory:RootPath`; no concurrency, admission, search, operation, or shutdown timeout is configured.
- The application targets Windows and references `System.DirectoryServices`; this plan does not introduce a cross-platform LDAP client.
- P01 establishes the deterministic test project and canonical verification command. Default tests must not require a domain, credentials, IIS, or network access.
- P06 establishes one explicit query execution context, a monotonic active deadline, and logical directory-operation accounting at the directory-service boundary. P06 expressly leaves global LDAP concurrency, blocking workers, native LDAP timeouts, physical-operation telemetry, and queue-wait telemetry to P09.
- A concurrent P08 draft now exists at `.agents/plans/P08-template-filter-complexity.md`, while implementation remains unauthorized. It assigns template compilation, correlation-preserving OR batching, final escaped-filter cost/shape enforcement, and reduction of one-search-per-combination to P08; it assigns scheduling, concurrency, and operation timeouts to P09. P09 implementation must consume P08's reviewed landing contract rather than freezing an unreviewed draft, and must not compensate for inefficient filters by silently changing query semantics.

## Goals

1. Put every production `System.DirectoryServices` network-bound operation behind one process-wide, finite scheduler.
2. Isolate blocking ADSI calls on a fixed number of named background MTA threads rather than ASP.NET or managed thread-pool workers.
3. Keep the physical concurrency limit true after caller cancellation or timeout, including when ADSI cannot be interrupted.
4. Bound queued work, reject overload predictably, and distinguish admission, queue-wait, execution-timeout, query-deadline, caller-cancellation, shutdown, and dependency-failure outcomes.
5. Apply finite native total-server, per-page-server, and client limits to every `DirectorySearcher` operation and a finite caller-observed operation deadline to ADSI calls without a native timeout.
6. Preserve P06's single execution context and charge every actual server-bound unit only after a worker claims the admitted item and immediately before invoking ADSI — never at submission or admission.
7. Remove per-call parallelism and prevent one multi-entry helper from filling or monopolizing the global queue.
8. Ensure ADSI objects are created, used, materialized, and disposed by the same worker; only plain application records cross threads.
9. Provide low-cardinality telemetry that makes saturation, timed-out-but-still-running calls, and tuning needs visible without exposing directory data.
10. Prove concurrency, timeout, cancellation, disposal, and shutdown behavior with deterministic fakes and synchronization gates.

## Non-goals

- Do not change filter meaning, template expansion, filter consolidation, direct-report batch shape, paging strategy, or server-side query efficiency; P08 owns those concerns.
- Do not change traversal semantics, nested-group identity, cycle handling, ranged retrieval of large `member` attributes, or recursion limits; P10 owns those concerns.
- Do not replace `System.DirectoryServices` with `System.DirectoryServices.Protocols`. A protocol-level asynchronous/cancellable rewrite may be proposed separately after this containment layer is measured.
- Do not redesign query-job queueing, job state transitions, retention, or application shutdown orchestration; P14 owns those concerns.
- Do not define HTTP problem-details, job-state, or UI error mapping. P13 consumes P09's typed lower-level failures and owns end-to-end cancellation/error contracts.
- Do not loosen P06 budgets, construct an execution context inside `ActiveDirectoryService`, or duplicate logical operation counters in the scheduler.
- Do not add a fallback or feature flag that restores direct unbounded ADSI execution.
- Do not require live Active Directory in the default verification command. An environment-gated domain smoke test may be documented, but it is not an automated acceptance gate.
- Do not capture LDAP filters, distinguished names, attribute values, usernames, or request text in metrics, queue item names, or exception messages.

## Required landing assumptions and ownership boundaries

### P01 — Verification foundation

- P09 adds its tests to P01's test project and uses P01's repository-root verification command.
- Tests use in-process fake blocking operations, fake time, and explicit synchronization gates. They never contact LDAP by default.
- Every test-bearing implementation slice records the required red/green guard proof by temporarily reverting or mutating the protected behavior, observing the focused test fail, restoring the implementation, and observing the full verification command pass.

### P06 — Query budgets and deadlines

- The P06 execution context is passed explicitly into every directory-service method. It must not be read from `AsyncLocal`, because execution-context flow onto dedicated threads is neither required nor relied upon.
- P06 remains the single authority for the query's active deadline and logical directory-operation budget. Submission checks the P06 deadline without consuming an operation unit. After a worker successfully claims an admitted RootDSE lookup, search, group-member read, or distinguished-name bind, it atomically consumes one P06 operation unit immediately before invoking ADSI.
- P09 owns physical scheduler counters and timings only. A retry or later P08 batch is charged once for every worker-claimed physical server-bound attempt. Saturation rejection, queued cancellation, queue timeout, and shutdown-before-start do not consume a P06 operation unit because no worker initiated directory work. Once a worker claims an item, its unit is not refunded even if cancellation wins the final pre-invocation race or the provider fails before emitting a network packet.
- Every caller-visible P09 queue or operation wait is clamped to P06's remaining active time. Finite native provider preferences may expire later solely to encourage physical cleanup after the caller has detached; they never extend a caller-facing task or replace the query deadline.
- P06 deadline expiry remains `query_budget_exceeded` and is not wrapped as an LDAP timeout. Caller cancellation remains cancellation. P09 supplies typed admission and LDAP-timeout failures only when neither higher-level condition caused the outcome.
- P06's post-operation deadline check still runs after a successful scheduler result. P09 returning promptly does not remove P06's end-to-end check.

### P08 — Template and filter complexity

- P08 owns how many searches are necessary, which templates are combined, exact escaped filter rendering/cost, maximum filter node/byte shape, direct-report batch cardinality, and whether equivalent searches are consolidated. Its final renderer gate runs before scheduler admission.
- P09 accepts P08's immutable rendered/prepared search and treats each emitted batch uniformly. It neither re-renders, splits, nor merges filters to satisfy scheduler limits and does not raise P08 cardinality limits to improve throughput.
- P08 requires each physical batch to consume exactly one P06 directory-operation unit. Once P09 lands, the scheduler's worker-claim gate is the sole physical charge site; P08 passes the original context and must not precharge the same batch. This is a location handoff, not a change in P08's call-count semantics.
- If P09 lands before the reviewed P08 implementation, the scheduler API must remain independent of filter structure. After P08 lands, re-run the call-site inventory and tests so every renderer-approved batch enters the same scheduler; do not keep a compatibility bypass.

### Adjacent plans

- P10 traversal and group expansion consume the same scheduler and P06 context. P10 may change the sequence of operations but cannot bypass admission or replace P09's physical limits.
- P13 maps P09 exceptions to stable API/job outcomes and must preserve the distinction between caller cancellation, P06 deadline, scheduler saturation, LDAP timeout, shutdown, and dependency failure.
- P14 may stop producing new directory work during application drain, but only P09 owns LDAP worker admission and physical occupancy. P14 must not start replacement tasks for P09 calls that have timed out to their callers.
- P20 readiness may submit a low-priority RootDSE/directory probe through this scheduler; it receives no reserved worker and must report saturation instead of bypassing the bound.

## Technical design

### 1. Validated finite configuration

Add a typed `LdapExecutionOptions` section and validate it at startup with `ValidateOnStart`. The checked-in values require owner approval; the proposed initial contract is:

```json
"LdapExecution": {
  "WorkerCount": 4,
  "QueueCapacity": 64,
  "MaxQueueWaitSeconds": 10,
  "OperationTimeoutSeconds": 20,
  "ServerPageTimeLimitSeconds": 25,
  "ServerTimeLimitSeconds": 25,
  "ClientTimeoutSeconds": 30,
  "ShutdownWaitSeconds": 5
}
```

Validation rejects missing, zero, negative, infinite, overflowed, or unreasonably large values. Enforce `WorkerCount <= 32`, `QueueCapacity <= 4096`, and every duration `<= 300 seconds`; enforce `OperationTimeout < ServerPageTimeLimit <= ServerTimeLimit <= ClientTimeout`. Configuration cannot select an unbounded mode. P06's proposed 120-second query deadline remains independent; the shorter P09 caller timeout detaches before a configured native limit can return accumulated partial search results, while the later native limits may help release the still-occupied worker.

`ActiveDirectory:RootPath` remains P16-owned configuration. Its presence avoids RootDSE discovery but does not bypass the scheduler for the subsequent search.

### 2. Blocking-operation boundary

Introduce a narrow internal blocking adapter (names may follow repository conventions):

```csharp
internal interface ILdapBlockingOperations
{
    string ResolveDefaultNamingContext(LdapNativeTimeouts timeouts);
    IReadOnlyList<DirectoryRecord> Search(PreparedDirectorySearch request, LdapNativeTimeouts timeouts, CancellationToken observationToken);
    IReadOnlyCollection<string> ReadGroupMembers(string groupDn, CancellationToken observationToken);
    DirectoryRecord? Lookup(string distinguishedName, DirectoryObjectType fallbackType, IReadOnlyList<string> attributes, CancellationToken observationToken);
}
```

The production adapter is the only class allowed to construct or access `DirectoryEntry`, `DirectorySearcher`, `SearchResultCollection`, `SearchResult`, or property collections. Each method creates, uses, fully materializes, and disposes those objects during one worker invocation. It returns only strings, immutable request data, and `DirectoryRecord` values. No ADSI object, lazy enumerable, property collection, or COM-backed value may cross the worker boundary; multi-valued properties are copied to plain strings or other existing stable application value types before completion.

Register this production adapter as a stateless singleton. It may be called concurrently by the fixed workers but never shares an ADSI object or mutable operation state between them. Register a separate singleton default-naming-context cache (or let the singleton scheduler own that cache); use an atomic/locked publication of a successful plain string. Concurrent initial misses may each perform a bounded, charged RootDSE operation, but only success is cached process-wide and failures never poison the cache.

The adapter must keep all potentially binding property reads inside the boundary, including `SchemaClassName`, `objectClass`, RootDSE properties, `RefreshCache`, `FindAll`, and result enumeration. It checks a work-item-owned observation token between records or values, but the scheduler never assumes that check can interrupt an in-progress native call. Disposal occurs in a `finally` on the same worker thread that created the object; neither cancellation callbacks nor caller threads dispose live ADSI objects.

Search request normalization, filter construction, and immutable preparation remain outside the blocking adapter. Preparation must not access directory-backed objects. Do not mutate the caller's `DirectorySearchRequest` after submission.

### 3. Process-wide bounded scheduler

Register one `ILdapOperationScheduler` singleton that is also hosted for start/stop lifecycle. It owns:

- exactly `WorkerCount` long-lived, named, `IsBackground = true` threads;
- `ApartmentState.MTA` set before each worker starts;
- one bounded FIFO channel with capacity `QueueCapacity`, single-item `TryWrite` admission, and no unbounded producer wait list;
- a closed work-item state machine and `TaskCompletionSource<T>` created with `RunContinuationsAsynchronously`;
- an injected `TimeProvider` for deadlines and metrics; and
- fixed-cardinality counters/gauges described below.

No production LDAP operation may use `Task.Run`, `Parallel.ForEach`, `Parallel.ForEachAsync`, raw thread-pool queueing, or a private semaphore as an alternate path. A full channel fails immediately with `LdapAdmissionRejectedException` (`ldap_queue_saturated`). Accepted FIFO items have a maximum queue-residence deadline; expired/cancelled queued items become tombstones that workers skip. Tombstones remain part of bounded channel occupancy until dequeued, deliberately applying conservative backpressure while workers are blocked.

The scheduler API accepts a fixed low-cardinality `LdapOperationKind`, the explicit P06 execution context, the caller token, and a synchronous delegate over the singleton blocking adapter. Admission copies/deep-freezes all operation input and creates a work-item-owned cancellation source that observes caller cancellation, P06 expiry, P09 operation timeout, and host stop. The raw caller token is used only to signal that source and is never handed to the blocking adapter. A running work item, not the request scope or scheduler-wide disposal path, owns the observation source until physical return and same-thread ADSI disposal. The work item does not capture the ambient `ExecutionContext`, scoped `ActiveDirectoryService`, request services, `HttpContext`, a logger scope containing user data, or an impersonation token. Only immutable safe correlation fields needed for logs may be copied.

The scheduler starts no replacement worker after a worker blocks, faults an item, or serves a caller that has already timed out. A worker loop catches item exceptions so one failed item does not kill the loop; unexpected loop termination is a critical scheduler fault and makes future admission fail closed rather than silently lowering or replacing capacity.

### 4. Work-item state and capacity truth

Use one atomic state transition path with these states:

```text
Created -> Queued -> Running -> Completed | Faulted
             |          |
             +-> Cancelled/QueueTimedOut
                        +-> CallerDetached -> PhysicallyCompleted/Faulted
```

- Before admission: caller cancellation is observed and P06 checks its active deadline without consuming a directory-operation unit.
- Admission: `TryWrite` either moves the item to `Queued` or fails immediately as saturated. A rejected item never runs.
- While queued: caller cancellation, P06 deadline, or `MaxQueueWait` may complete the caller-facing task. The item stays bounded in the channel and is skipped when read.
- At start: one worker atomically claims `Queued -> Running`, records queue wait, recomputes remaining P06 time, and fails without invoking ADSI if no positive time remains. It then atomically consumes one P06 operation unit, checks caller/deadline state once more, and invokes the blocking delegate only if that terminal gate succeeds. The consumed unit is not refunded after claim.
- While running: the caller-facing task may complete because of caller cancellation, P06 deadline, or `OperationTimeout`. The physical state becomes `CallerDetached`; the work-item observation token is cancelled, but neither it nor its registrations are disposed. The worker remains occupied and the active-operation gauge remains incremented until the delegate actually returns and same-thread disposal finishes.
- On late physical completion/failure: the result is discarded, the late exception is observed and logged with safe fields, same-thread ADSI disposal finishes, and only then does the worker dispose the work-item observation source, timers, and registrations. Scheduler/request-scope disposal never disposes resources still owned by a running item. The existing caller outcome is never overwritten; only then may that worker take another item.
- On normal completion: before publishing materialized data, the worker re-reads monotonic time and cause-specific deadlines. An already elapsed caller/P06/P09 deadline wins even if its timer callback was delayed. Otherwise materialized data wins the terminal transition exactly once; timer or cancellation callbacks that race afterward are no-ops. The worker then disposes item-owned resources.

The global physical concurrency invariant is therefore `active_blocking_operations <= WorkerCount` at all times. Caller-visible task count and worker occupancy are intentionally different measurements.

### 5. Deadline, cancellation, and timeout contract

Use monotonic `TimeProvider` timestamps and absolute deadlines. Do not implement timeout races with arbitrary `Task.Delay`, wall-clock `DateTime`, or blocking sleeps.

At submission and worker start, retain rather than collapse these cause-specific values:

- `p06ActiveDeadline`, including its P06 failure dimension;
- `ldapQueueDeadline = submittedAt + MaxQueueWait`;
- `ldapOperationDeadline = startedAt + OperationTimeout`;
- `effectiveQueueDeadline = min(ldapQueueDeadline, p06ActiveDeadline)` plus the binding source; and
- `effectiveCallerOperationDeadline = min(ldapOperationDeadline, p06ActiveDeadline)` plus the binding source.

If caller-visible remaining duration is non-positive, do not enter ADSI. A deadline record carries both absolute timestamps through every timer, worker-claim, completion, and race transition. If both causes are due at the same observation point, caller cancellation wins first, then P06 expiry, then the corresponding P09 queue/operation timeout. The single transition function therefore emits P06 `query_budget_exceeded` when `p06ActiveDeadline` caused expiry and emits `LdapTimeoutException` only when the applicable P09 deadline caused expiry.

Native ADSI timeout resolution is one second, so checked-in native values are positive whole seconds. Apply configured `DirectorySearcher.ServerPageTimeLimit`, `ServerTimeLimit`, and `ClientTimeout` before `FindAll()`; these finite provider cleanup preferences do not extend the earlier caller-visible deadline and are not reduced to its remaining duration. Because the current `PageSize = 500` search is paged, `ServerPageTimeLimit` bounds server effort for each page while `ServerTimeLimit` remains the documented total server search limit; `ClientTimeout` is still set as the documented client wait preference. The configured P09 operation deadline is strictly earlier than every configured native preference. Thus, if a configured server time limit returns accumulated rows, the worker's mandatory monotonic completion check sees the already elapsed P09/P06 deadline and discards them instead of publishing partial success. These preferences still are not reliable interruption or a hard end-to-end bound; fixed occupied workers remain authoritative if a provider ignores or exceeds them. A provider or domain policy that silently returns an incomplete set before any configured deadline remains a documented `System.DirectoryServices` residual, not something this adapter can prove complete.

Outcome precedence and typing are deterministic:

1. A caller token already cancelled at a decision point yields `OperationCanceledException` associated with that caller token.
2. Otherwise, an expired P06 active deadline is reported through P06's typed `query_budget_exceeded` contract.
3. Otherwise, a scheduler fatal state refuses admission or queued execution with `LdapSchedulerUnavailableException` and `ldap_scheduler_faulted`.
4. Otherwise, host stop before execution yields `LdapSchedulerUnavailableException` and `ldap_scheduler_stopping`.
5. Otherwise, a full queue yields `LdapAdmissionRejectedException` and `ldap_queue_saturated`, without consuming a P06 operation unit.
6. Otherwise, expiry before worker claim yields `LdapTimeoutException` and `ldap_queue_timeout`, without consuming a P06 operation unit.
7. After worker claim, P06 operation-budget exhaustion yields P06's typed `query_budget_exceeded` before ADSI.
8. Otherwise, expiry after worker claim yields `LdapTimeoutException` and `ldap_operation_timeout`; it records whether physical completion was still pending, but exposes no directory input.
9. Otherwise, a documented numeric ADSI server/page/client timeout returned before any caller/P06/P09 deadline yields `LdapTimeoutException` and `ldap_provider_timeout`; never classify by localized message text.
10. Otherwise, provider failures retain their original exception as an inner exception under `LdapDependencyException` and `ldap_dependency_failure`, unless an existing best-effort per-entry contract deliberately handles them.

One transition method evaluates this precedence from the retained cause-specific timestamps so cancellation/timer/worker races cannot produce contradictory outcomes and emits exactly one code from the seven-code registry for every P09-owned terminal cause. P13 later maps the types; P09 tests the lower-level distinctions. Broad catches in group and lookup helpers must explicitly rethrow caller cancellation, P06 exhaustion, scheduler admission, timeout, stopping, and dependency-level fatal failures. No timeout, cancellation, or P06 exhaustion returns successful partial rows.

### Stable P09 failure-code registry

P09 owns the closed lower-level LDAP failure-code registry and emits the code as a typed property; consumers never parse exception messages. P13 owns transport/job mapping but must preserve these exact strings and must not mint aliases for the same causes:

| Code | P09 cause |
|---|---|
| `ldap_queue_saturated` | Admission `TryWrite` failed because the bounded queue was full. |
| `ldap_queue_timeout` | An admitted item exceeded P09 `MaxQueueWait` before worker claim; caller cancellation and P06 expiry keep their higher-level outcomes. |
| `ldap_operation_timeout` | P09 `OperationTimeout` expired after worker claim; the worker may remain physically occupied. |
| `ldap_provider_timeout` | ADSI reported a recognized native server/page/client timeout before the P09/P06 caller deadline. Classification uses documented HRESULT/error codes, never localized message matching. |
| `ldap_scheduler_stopping` | Admission or queued execution was refused because host stop had begun. |
| `ldap_scheduler_faulted` | An unexpected worker-loop/scheduler infrastructure failure closed admission. |
| `ldap_dependency_failure` | An otherwise unclassified ADSI bind/search/property/provider failure escaped an explicitly characterized best-effort entry case. |

Caller cancellation has no P09 LDAP code; it remains cancellation. P06 deadline or operation-budget exhaustion remains P06-owned `query_budget_exceeded`. Individual lookup/group not-found cases that the characterized service contract skips are not terminal codes. Adding, removing, renaming, merging, or changing the cause of a registry entry requires a reviewed P09 contract change coordinated with P13.

### 6. Directory-service wiring and fairness

All paths use the singleton scheduler:

- **Search:** resolve a missing default naming context as its own charged and scheduled RootDSE operation, then schedule one charged search operation. Configure native total-server, per-page-server, and client limits, call `FindAll`, enumerate, map, and dispose entirely on one worker. Read and atomically publish only a successful default-naming-context string through the singleton thread-safe cache; concurrent cold misses may duplicate bounded work and failures are not cached. A configured root path still schedules the search.
- **Lookup:** remove `ConcurrentBag` and `Parallel.ForEach`. Deduplicate inputs while preserving first-seen order. Submit and await one charged distinguished-name bind at a time, append successful plain records in that order, and never place all entries from one request into the global queue. Existing best-effort handling of an individual not-found/provider failure may remain, but typed cancellation, deadline, saturation, timeout, stopping, and fatal scheduler failures propagate and discard the method's accumulated result.
- **Group members:** deduplicate groups in deterministic first-seen order and submit/await one charged `RefreshCache("member")` operation at a time. Keep traversal and ranged-member redesign out of P09. Accumulate locally and publish only after the method completes without a typed terminal failure.
- **Direct reports:** retain P08's final batch/filter shape and route the resulting search through `SearchAsync`; do not double-charge the wrapper in addition to the physical search.
- **CSV, synchronous API, and jobs:** because each ultimately uses `IActiveDirectoryService`, they share the same singleton workers and queue. Add an architecture guard that finds no production construction of the blocking adapter outside scheduler registration and no direct ADSI call outside the adapter.

Sequential per-invocation lookup/group submission is the initial fairness rule: one request can hold at most one running or queued entry operation at a time. Concurrency comes from independent requests sharing the global pool. Any future per-request window or priority queue requires a separate reviewed plan backed by measurements; it cannot exceed the same worker/queue limits.

### 7. Identity and thread-apartment contract

Dedicated workers bind with the application's process/service identity, matching the intended service-account model. Do not flow or impersonate the authenticated request user's Windows token. Capturing a request token across queue wait would introduce lifetime, delegation, and privilege-boundary risks.

Before implementation, run a non-default deployment smoke check under the intended IIS/service identity to confirm that default `DirectoryEntry` credentials resolve on an MTA worker. If the deployment actually requires per-user impersonation, stop the implementation: that is an owner-visible security architecture change and requires a separate credential-lifetime design rather than silently capturing `WindowsIdentity`.

### 8. Lifecycle and shutdown

On host start, create all configured workers before accepting items. On host stop:

1. atomically reject new admission as `Stopping`;
2. complete the channel writer;
3. complete queued caller tasks as scheduler unavailable/cancelled by shutdown and let workers skip those items;
4. allow already-running delegates up to `ShutdownWait` to return and dispose on their owning worker;
5. join workers only within the smaller of the configured shutdown wait and host stop token; and
6. if ADSI remains blocked, log one critical summary and return from stop. Background workers remain occupied; do not call `Thread.Abort`, perform cross-thread disposal, or start replacements.

Shutdown metrics report outstanding physical operations. `Dispose` is idempotent and must not dispose a running item's observation source, timers, adapter state, or metrics that its worker can still touch; worker-owned cleanup remains valid if a background thread returns after bounded `StopAsync`. P14 may coordinate job drain around this lifecycle, but P09's stop path remains bounded even when a provider never returns.

### 9. Telemetry and safe diagnostics

Add one `System.Diagnostics.Metrics` meter with documented units and fixed tags:

- observable gauges: queue depth, active workers, and caller-detached workers;
- counters: submissions, admission rejections, queue timeouts, operation timeouts, caller cancellations, P06 deadline expirations, completions, dependency faults, skipped tombstones, and shutdown-abandoned workers;
- histograms: queue residence, physical blocking duration, and caller-observed duration.

Allowed tags are fixed enums such as operation kind (`search`, `root_dse`, `lookup`, `group_members`, `readiness`) and result class. Never tag DN, LDAP path, filter, requested attributes, user, job ID, exception text, or raw endpoint. Logs use the same safe fields and aggregate stuck-operation counts rather than logging inputs.

## Deterministic verification design

### Scheduler tests

Use a fake blocking adapter whose delegates signal `ManualResetEventSlim`/`TaskCompletionSource` gates on entry and wait on explicitly released gates. Use the P01 test framework's bounded test timeout only as a hang guard, never as the mechanism that decides ordering. Use `FakeTimeProvider` (or a small deterministic `TimeProvider` test implementation if P01 does not select the extensions package) to advance deadlines without sleeps.

Prove:

- with `WorkerCount = N`, at most N delegates enter before gates are released across at least 100 concurrent submitters and every operation kind;
- a channel accepts exactly its configured queued capacity behind N blocked workers and the next submission fails immediately as saturated;
- queued cancellation and queue timeout complete the caller once, never invoke the delegate, and are skipped when a worker becomes available;
- caller cancellation, P06 deadline, and operation timeout after start complete promptly while the delegate stays blocked, active occupancy stays N, and no replacement delegate enters;
- a detached delegate can observe/register against its work-item token after the external caller source and request scope are disposed; only the owning worker disposes item resources after physical completion;
- releasing a detached delegate performs cleanup, suppresses late value/fault publication, decrements detached/active counts, and permits exactly one next item;
- delayed timer callbacks cannot publish a result after the corresponding absolute P06/P09 deadline, and a shorter P06 deadline yields `query_budget_exceeded` rather than an LDAP timeout;
- completion racing cancellation selects one terminal outcome and invokes continuations asynchronously;
- unexpected item faults do not kill a worker, while an injected worker-loop fatal state closes future admission;
- stopping rejects new work, settles queued work, returns after fake shutdown time, and reports still-blocked background workers without cross-thread disposal;
- queue and physical-duration measurements use monotonic fake time and all metric tags belong to the fixed allowlist.
- every terminal P09 failure path emits exactly one code from the closed registry; caller cancellation and P06 exhaustion emit none of those codes, and native timeout recognition uses numeric fake provider errors rather than message text.

### Blocking-adapter and service tests

Keep production ADSI types behind `ILdapBlockingOperations`. A recording fake verifies:

- search receives positive whole-second native preferences with `caller operation < page server <= total server <= client`; the caller/P06 deadline remains independently earlier when it is the binding cause;
- no adapter call occurs when caller cancellation or P06 deadline is already effective;
- RootDSE resolution is separately charged/scheduled, uses a singleton thread-safe success-only cache, tolerates bounded duplicate cold misses, and a configured root bypasses RootDSE but not search admission;
- search publishes no partially enumerated records on timeout/cancellation/deadline;
- lookup/group inputs are deduplicated in first-seen order, maintain at most one outstanding entry per service invocation, and propagate typed terminal failures instead of returning accumulated partial data;
- individual lookup/group not-found behavior remains characterized and does not swallow terminal scheduler exceptions;
- direct-report search is charged exactly once;
- every call receives the same P06 context instance supplied by its caller.

Add a narrow factory/options-applier unit test around the production searcher configuration so deleting any of `ServerPageTimeLimit`, `ServerTimeLimit`, or `ClientTimeout` fails without contacting a domain. Assert that paged searches receive the effective per-page limit and that no native value rounds to the zero/infinite sentinel. If `DirectorySearcher` cannot be safely instantiated in a domain-free test, isolate assignment behind a minimal search-command factory and test the produced timeout values plus a Windows-only construction smoke test that does not call `FindAll`.

Add a source/architecture test limited to production code that rejects `new DirectoryEntry`, `new DirectorySearcher`, `FindAll`, `RefreshCache`, `Parallel.ForEach`, and `Task.Run` for LDAP outside the approved blocking adapter/scheduler files. This guard is intentionally structural; update its allowlist only through review when a new server-bound primitive is introduced.

### Optional deployment smoke test

Document an opt-in, credentialed Windows smoke test that executes RootDSE and a size-limited base search through the scheduler under the intended service identity, verifies native timeouts are positive, and emits no directory values. It must be skipped unless explicit environment variables and credentials/domain context are present and must never run in the canonical CI command.

## Implementation slices

Each numbered slice is one commit. Do not start the next slice until the current slice is verified, its red/green guard proof is recorded, and the slice is committed. Do not combine findings across commits.

### Slice 1 — Add finite LDAP execution options and typed outcomes

Commit intent: `feat: define bounded ldap execution contracts`

- Add `LdapExecutionOptions`, startup validation, the approved checked-in defaults, operation-kind/timeout-phase enums, and typed scheduler exceptions.
- Define the scheduler and blocking-adapter interfaces without routing production calls yet.
- Implement the closed failure-code registry and document outcome precedence and safe diagnostic fields in code contracts.
- Add options and exception-contract tests.

Guard proof:

- Temporarily accept zero/infinite-equivalent timeouts and an inconsistent server/client/operation ordering; confirm focused startup-validation tests fail.
- Temporarily include a DN/filter field in a typed exception payload; confirm the safe-contract test fails.
- Rename one registry string or classify caller/P06 exhaustion as an LDAP code; confirm the registry-contract test fails.
- Restore the implementation and run the full P01 verification command.

### Slice 2 — Implement the fixed worker scheduler

Commit intent: `feat: bound blocking ldap workers and admission`

- Implement the singleton hosted scheduler, fixed named MTA background threads, bounded FIFO admission, work-item state machine, fake-time deadlines, bounded shutdown, and scheduler metrics.
- Give each item its own observation source and immutable input; keep running-item resources valid through detachment and bounded host/scope disposal.
- Keep the production directory service unwired so the slice can be verified entirely with fake blocking delegates.
- Add the scheduler concurrency, saturation, queue-timeout, detachment, late-completion, race, fault, shutdown, and metric tests described above.

Guard proof:

- Temporarily release active occupancy when the caller times out; confirm the no-replacement/global-cap test fails while the original fake remains blocked.
- Temporarily use an unbounded channel or allow one extra item; confirm the exact-capacity test fails.
- Temporarily publish a late result after detachment and confirm the exactly-once test fails.
- Temporarily pass the raw caller token or dispose an item source at caller detachment; confirm the detached-token lifetime test fails.
- Collapse P06/P09 deadlines to one untyped timestamp; confirm the shorter-P06 classification test fails.
- Temporarily wait indefinitely during stop and confirm the fake-time bounded-shutdown test fails.
- Restore each behavior and run the full P01 verification command.

### Slice 3 — Contain RootDSE and directory search

Commit intent: `feat: schedule timeout-aware ldap searches`

- Implement the production blocking adapter for RootDSE and search.
- Move all searcher/entry/result lifetime inside the adapter and apply effective native limits before `FindAll`.
- Route RootDSE discovery and `SearchAsync` through the scheduler with explicit P06 context, charging each worker-claimed physical operation once and no rejected/queued tombstone.
- Register the stateless adapter and thread-safe default-naming-context cache as singletons; cache only successful plain strings.
- Add adapter, service, P06 propagation, no-partial-result, and architecture tests.

Guard proof:

- Remove `ServerPageTimeLimit`, `ServerTimeLimit`, and `ClientTimeout` assignments one at a time; confirm the timeout-configuration guard fails for each mutation.
- Bypass the scheduler for RootDSE or search; confirm the architecture/call-count guard fails.
- Return an enumerated prefix after an injected timeout; confirm the no-partial-result guard fails.
- Restore the implementation and run the full P01 verification command.

### Slice 4 — Contain entry lookup and group-member reads

Commit intent: `feat: route ldap entry reads through global workers`

- Move lookup, schema/property access, group-member `RefreshCache`, materialization, and disposal into the blocking adapter.
- Remove `ConcurrentBag` and `Parallel.ForEach`.
- Deduplicate in first-seen order and allow only one outstanding entry operation per service invocation.
- Preserve current characterized best-effort not-found behavior, but propagate typed cancellation/deadline/admission/timeout/stopping failures and discard accumulated partial results.
- Route direct reports through the already scheduled search without double charging.
- Add deterministic order, fairness, exception, disposal-thread, and exact-accounting tests.

Guard proof:

- Restore per-call `Parallel.ForEach` or enqueue all DNs concurrently; confirm the one-outstanding-entry/global-cap test fails.
- Swallow a typed timeout in the existing broad catch; confirm the no-partial-success test fails.
- Dispose a fake ADSI lease on the caller/cancellation thread; confirm the same-thread disposal test fails.
- Double-charge the direct-report wrapper; confirm the P06 accounting guard fails.
- Restore the implementation and run the full P01 verification command.

### Slice 5 — Close all production bypasses and document operations

Commit intent: `docs: operationalize bounded ldap execution`

- Re-inventory all `IActiveDirectoryService` call sites after P08 and later landed prerequisites; prove every physical call uses the scheduler and P06 context.
- Finalize low-cardinality metric documentation, timeout tuning guidance, overload behavior, shutdown residuals, and the opt-in service-identity smoke procedure.
- Run the deterministic 100-submitter load characterization in Release and record counts, not machine-specific elapsed-time claims.
- Update repository verification guidance only if P01's canonical command or project inventory actually changes.

Guard proof:

- Add a temporary forbidden direct ADSI call in a production fixture/file and confirm the architecture guard fails; remove it and confirm the full verification command passes.
- Change a metric tag to a high-cardinality placeholder and confirm the metric allowlist test fails; restore it and re-run verification.

## Acceptance criteria

- Missing, zero, negative, infinite-equivalent, overflowed, inconsistent, or out-of-range LDAP scheduler configuration fails startup.
- Exactly one scheduler instance owns exactly the configured number of named MTA background workers and one finite queue.
- No production LDAP path performs blocking ADSI work on an ASP.NET request thread, job executor thread, or managed thread-pool worker.
- `active_blocking_operations` never exceeds `WorkerCount`, including after every caller has timed out or cancelled.
- The queue admits at most `QueueCapacity` items and rejects the next item immediately with the typed saturation outcome.
- An accepted item cannot wait in the queue beyond the smaller of P09 queue wait and P06 remaining time from the caller's perspective.
- A started item cannot hold its caller beyond the smaller of P09 operation timeout and P06 remaining time; a non-interruptible native call continues to occupy its original worker until it physically returns.
- Cause-specific P06 and P09 deadlines remain distinct through queue, run, timer, and completion races; a P06-caused expiry is never reported as an LDAP timeout.
- No replacement worker, early capacity release, cross-thread ADSI disposal, `Thread.Abort`, `Task.Run`, or per-call `Parallel.ForEach` weakens the physical bound.
- A work-item-owned observation source remains valid after caller/request/scheduler detachment and is disposed only by the owning worker after physical completion and ADSI cleanup.
- Every paged `DirectorySearcher` has positive `ServerPageTimeLimit`, `ServerTimeLimit`, and `ClientTimeout` values applied before `FindAll`; the caller deadline is strictly earlier than configured native limits and none is described as a hard interruption guarantee.
- Every `DirectoryEntry`, searcher, result collection, result, and property collection is created, accessed, materialized, and disposed on its owning worker; only plain application data crosses threads.
- RootDSE, search, group-member read, and distinguished-name bind operations use the caller's existing execution context and consume P06 accounting exactly once after worker claim and immediately before ADSI; saturation and queued tombstones consume zero units.
- P06 deadline, caller cancellation, queue saturation, queue timeout, operation timeout, shutdown, and provider failure remain distinguishable typed outcomes.
- P09 emits exactly `ldap_queue_saturated`, `ldap_queue_timeout`, `ldap_operation_timeout`, `ldap_provider_timeout`, `ldap_scheduler_stopping`, `ldap_scheduler_faulted`, or `ldap_dependency_failure` for its owned terminal causes; P13 maps but does not rename them, and caller/P06 outcomes remain outside this registry.
- Cancellation, P06 exhaustion, admission rejection, timeout, and shutdown never produce a successful partial directory result.
- Multi-entry helpers deduplicate deterministically and keep at most one operation queued or running per service invocation.
- All synchronous, queued-job, CSV, traversal, and readiness directory work shares the same global admission path.
- Scheduler metrics expose queue/worker/detached utilization and result classes using only fixed safe tags.
- Shutdown stops admission and returns within its configured bound even when fake ADSI work never returns.
- Default tests deterministically prove saturation, cancellation/timeout detachment, no replacement, late cleanup, same-thread disposal, outcome races, and bounded shutdown without live LDAP or timing sleeps.
- The optional service-identity smoke test is documented but excluded from default CI.
- Each test-bearing slice has a recorded mutation-based red/green guard proof, the full P01 verification command passes, and each slice is committed separately.
- P08 filter/batching, P10 traversal, P13 API mapping, P14 job orchestration, and a future protocol-client rewrite remain outside this plan.

## Rollback

Use new revert commits; do not rewrite history.

- Revert slices in reverse order. Do not leave some directory methods on the scheduler and others on direct ADSI after rollback; that would make the advertised global bound false.
- Slice 5 documentation may be reverted independently only when it no longer describes deployed behavior. The architecture guard must remain whenever any scheduler integration remains.
- Slice 4 can be reverted only together with its service wiring/tests or to another globally scheduled implementation; do not restore per-call `Parallel.ForEach` while claiming bounded execution.
- Reverting Slice 3 restores unbounded production search/RootDSE behavior and therefore requires explicit owner approval and an incident note. Prefer reducing `WorkerCount`, `QueueCapacity`, or timeouts within validated positive ranges over bypassing the scheduler.
- Reverting Slice 2 requires first reverting all production consumers. Never leave an interface fallback that runs the delegate inline.
- Slice 1 types/options may be removed last after no consumer remains. Do not retain configuration keys that falsely imply enforcement.
- A stuck ADSI provider is not recovered by rollback while the process is running. Operational recovery is a controlled process recycle after admission is stopped; never force-abort or dispose the blocked call from another thread.

## Risks and mitigations

- **ADSI may outlive every configured timeout.** Caller latency is bounded, but a worker can remain physically stuck. Fixed workers, no replacement, conservative queue occupancy, detached-worker metrics, alerts, and controlled process recycling contain rather than conceal the risk.
- **All workers can become permanently occupied.** The queue then fills and new work fails fast. This is deliberate load shedding; P20 readiness should report degraded/unready and operators should investigate/recycle rather than expand the pool automatically.
- **Sequential entry reads can reduce single-query throughput.** It prevents one request from monopolizing a global dependency. Measure queue/physical durations before proposing a bounded per-request window or protocol-level bulk operation.
- **A finite queue can reject legitimate bursts.** Emit utilization/rejection metrics and tune only from observed service capacity. Increasing the queue delays failure but does not increase LDAP throughput.
- **Native timeout properties may not cover bind, property refresh, or disposal.** The outer caller deadline and physical worker containment remain required; tests and docs must not describe native limits as hard interruption.
- **Moving work to dedicated threads can expose identity assumptions.** The plan explicitly chooses service/process identity and requires an opt-in deployment smoke check. Per-user impersonation is a separate security decision.
- **MTA/ADSI provider behavior may differ by environment.** Run the credentialed smoke check under the supported Windows/IIS hosting model before rollout; keep no automatic fallback to request-thread execution.
- **Queued tombstones temporarily occupy capacity.** This is a bounded, conservative trade-off that avoids a complex removable queue. Workers drain tombstones when they become available; metrics distinguish them from live work.
- **Broad existing catches can hide terminal failures.** Characterization tests and explicit catch filters preserve only intended per-entry best effort and force typed terminal outcomes to propagate.
- **P08 may change operation granularity after P09 is drafted.** Landing order and the Slice 5 re-inventory prevent new batch/search paths from bypassing the scheduler; P09 does not prescribe P08's shapes.
- **P06 and P09 timers can race.** Retain both absolute monotonic deadlines and their causes; one terminal-transition function, completion-time recheck, explicit precedence, and fake-time race tests keep classification deterministic.
- **Native server limits can return accumulated rows.** Configure the caller deadline earlier, recheck it before result publication, and discard late rows. A silently incomplete response caused by an earlier server/domain policy remains a documented ADSI residual and motivates a future protocol-level client if completeness proof is required.
- **Metrics can leak directory or user data.** Fixed enum tags and allowlist tests prohibit inputs and identifiers.
- **Background threads can survive bounded host stop.** `IsBackground` prevents them from holding process termination; the host reports outstanding physical operations and never claims graceful LDAP completion when they remain stuck.
- **A scheduler abstraction can become a generic work queue.** Keep its API internal and LDAP-specific, forbid arbitrary async delegates, and enforce the ADSI-only architecture boundary.

## Open owner decisions

### Decision 1 — Physical containment model

Approve a fixed process-wide pool of four dedicated background MTA threads with no replacement for blocked workers. Caller timeouts return promptly, but a timed-out native call keeps its worker until it really exits. Recommendation: approve; it is the only design here that keeps the LDAP concurrency claim true when ADSI cannot be cancelled.

Blocked until decided: Scheduler implementation in Slice 2 and all production wiring.

### Decision 2 — Admission and fairness

Approve a 64-item FIFO queue with immediate rejection when full and at most one queued/running lookup or group-entry read per service invocation. Recommendation: approve; this bounds memory and prevents one large query from occupying every slot, at the cost of lower single-query lookup throughput.

Blocked until decided: Queue behavior in Slice 2 and multi-entry wiring in Slice 4.

### Decision 3 — Initial timeout values

Approve 10 seconds maximum queue residence, 20 seconds caller-observed operation time, 25 seconds server time per page, 25 seconds total server search time, 30 seconds client wait time, and 5 seconds shutdown wait. Caller waits remain clamped to P06; the later native limits only encourage physical cleanup after detachment. Recommendation: approve as conservative pre-production defaults, then tune from low-cardinality metrics and the deployment smoke test.

Blocked until decided: Checked-in configuration in Slice 1 and native timeout wiring in Slice 3.

### Decision 4 — Directory bind identity

Approve service/process identity for all dedicated LDAP workers and explicitly reject per-request Windows-token capture. Recommendation: approve; it matches a service-oriented deployment and avoids queued impersonation-token lifetime and delegation risks. If production requires caller impersonation, stop and design that security boundary separately.

Blocked until decided: Production adapter rollout and deployment smoke check in Slices 3–5.

### Decision 5 — RootDSE caching

Approve process-lifetime caching of only a successful `defaultNamingContext` string when `ActiveDirectory:RootPath` is absent; failures are retried through normal admission. Recommendation: approve because the value is effectively deployment configuration and this removes a redundant server-bound call from every search. Restart is the invalidation mechanism.

Blocked until decided: RootDSE integration in Slice 3.

### Decision 6 — Stable LDAP failure codes

Approve the seven-code P09 registry exactly as written and require P13 to map it without message parsing or aliases. Recommendation: approve; one closed lower-level taxonomy keeps synchronous, queued, CSV, and future readiness behavior aligned while preserving caller cancellation and P06 budget exhaustion as separate owners.

Blocked until decided: Typed outcome implementation in Slice 1 and P13's dependent mapping.

## Advisory Review

Record no more than three headless Claude review rounds. Each round must identify material findings, the resulting revision or retained disagreement, and the reviewer's final assessment. If Round 3 requires changes, apply them, record that those final revisions were not re-reviewed, and stop.

### Round 1 — 2026-07-21T22:15:02Z

**Reviewer:** Headless Claude Code 2.1.217 / configured model / maximum effort

**Verdict:** Revisions required

- Added a finite, clamped `ServerPageTimeLimit` to the paged-search configuration, tests, option ordering, owner decision, and acceptance criteria. Retained a documented disagreement with the stronger claim that paging makes `ServerTimeLimit`/`ClientTimeout` inert: Microsoft's API documentation describes total-server, per-page-server, and client-wait preferences separately, and the runtime source submits all three. The plan now makes no hard-interruption claim for any native preference; fixed occupied workers remain the real containment mechanism.
- Moved P06 operation charging from pre-admission to the worker's final pre-ADSI gate. Saturation, queued cancellation/timeout, and shutdown tombstones consume no operation unit; a claimed attempt consumes exactly once and is not refunded.
- Made the production blocking adapter stateless singleton and added a separate singleton, thread-safe, success-only naming-context cache. Concurrent cold misses may duplicate bounded/charged work rather than sharing a caller-owned P06 context.

### Round 2 — 2026-07-21T22:28:04Z

**Reviewer:** Headless Claude Code 2.1.217 / configured model / maximum effort

**Verdict:** Revisions required

- Added a work-item-owned observation source and immutable-input lifetime contract. The raw caller token only signals the item; caller/request/scheduler detachment cannot dispose resources still used by a blocked worker, and the owning worker disposes them only after same-thread ADSI cleanup. Added deterministic detached-token and shutdown-lifetime guards.
- Retained separate absolute P06 and P09 deadlines plus a binding-cause record through all transitions. Added explicit precedence, mandatory completion-time recheck, and a shorter-P06 test that must produce `query_budget_exceeded`, not an LDAP timeout.
- Re-grounded the P08 boundary against the newly appeared unauthorized draft: P08 renders and bounds batches before scheduling, while P09 is the sole post-landing physical operation charge site at worker claim. P09 will consume the reviewed landing contract rather than freezing draft details.
- Author follow-up: moved the caller deadline earlier than all configured native search preferences and required a monotonic check before publishing. This prevents a configured server/page timeout's accumulated rows from winning a delayed timer race; silent early incompleteness imposed outside these settings remains an explicit ADSI residual.
- Pre-Round-3 cross-plan clarification: made P09 the explicit owner of a closed seven-string LDAP failure registry for P13 to consume without aliases or message parsing.

### Round 3 — 2026-07-21T22:44:55Z

**Reviewer:** Headless Claude Code 2.1.217 / configured model / maximum effort

**Verdict:** Revisions required

- Corrected the stale goal that still required pre-admission charging. The goal now matches the worker-claim gate: rejected, queued-cancelled, timed-out, and shutdown-before-start items consume no P06 operation unit.
- Extended the deterministic precedence list to cover all seven P09-owned codes. Scheduler fault and stopping are ordered before saturation, and a numeric provider timeout is ordered before generic dependency failure after caller/P06/P09 deadline checks.
- These revisions were applied after the third and final permitted advisory round and were not re-reviewed. No fourth round is permitted.
