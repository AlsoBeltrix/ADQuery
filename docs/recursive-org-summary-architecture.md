# Recursive Org Summary - Async Job Architecture

## Purpose
- Bridge the workflow guidance in `docs/recursive-org-summary-workflow.md` with concrete component design.
- Resolve the confidence gaps Claude called out: dependency injection wiring, job lifecycle, progress callbacks, LDAP OR-filter batching, and large-result performance.
- Provide a handoff-ready reference so future contributors do not need to rediscover these decisions.

## System Context
- ASP.NET Core host (`Program.cs`) already wires controllers, validators, and `DirectoryPlanExecutor`.
- The async flow inserts a job orchestration layer between controllers and executor while keeping LDAP access inside `ActiveDirectoryService`.
- Version 1 relies on in-memory infrastructure for queues and job state but is designed so a persistent store can drop in later.

## Component Model

### API Layer
- `QueryController.ExecuteAsync` enqueues work instead of running the plan synchronously.
- New endpoints:
  - `GET /api/query/jobs/{jobId}` returns a `QueryJob` DTO (status, progress, warnings, download links).
  - `DELETE /api/query/jobs/{jobId}` issues cancellation (optional for v1).
  - `GET /api/query/download/{jobId}.{csv|json}` streams cached payloads.

### Job Infrastructure
| Component | Lifetime | Responsibility |
|-----------|----------|----------------|
| `IQueryJobStore` (default `InMemoryQueryJobStore`) | Singleton | Thread-safe storage for metadata, progress, warnings, and cache keys. |
| `IQueryJobQueue` (default `InMemoryQueryJobQueue`) | Singleton | `Channel<QueryJobWorkItem>` wrapper that enforces FIFO and cancellation. |
| `QueryJobManager` | Scoped | Creates jobs, persists them to the store, enqueues work, exposes fetch/cancel helpers to controllers. |
| `QueryJobExecutorHostedService` (`BackgroundService`) | Hosted singleton | Pulls jobs from the queue, runs them, updates progress, records completion or failure. |

### Execution and Progress
- Extend `IDirectoryPlanExecutor` with an overload:  
  `Task<PlanExecutionResult> ExecutePlanAsync(DirectoryQueryPlan plan, IProgress<PlanProgressUpdate> progress, CancellationToken token);`
- `PlanProgressUpdate` includes:
  - `int NodesProcessed`
  - `int CurrentDepth`
  - `int? EstimatedRemainingNodes`
  - `string? Phase` (values like `enumerating-level`, `aggregation`, `finalizing`)
- Retain the existing overload for simple queries; the new overload funnels into it via an internal `NullProgress` implementation.

### Results Cache
- Reuse the existing `IMemoryCache`.
- After a job completes, `QueryJobManager` writes the serialized payload under a deterministic `job.ResultsCacheKey` with a two hour sliding expiration.
- Download endpoints stream directly from cache to avoid re-materializing large results in controller memory.

## Dependency Injection Wiring
- Register new services in `Program.cs`:
  ```csharp
  builder.Services.AddSingleton<IQueryJobStore, InMemoryQueryJobStore>();
  builder.Services.AddSingleton<IQueryJobQueue, InMemoryQueryJobQueue>();
  builder.Services.AddSingleton<QueryJobManager>();
  builder.Services.AddHostedService<QueryJobExecutorHostedService>();
  builder.Services.AddScoped<IJobStatusService, JobStatusService>(); // optional read-only facade for controllers
  ```
- Inject `QueryJobManager` and `IJobStatusService` into `QueryController`.
- `QueryJobExecutorHostedService` receives an `IServiceScopeFactory` to create a scope per job so scoped dependencies (`IDirectoryPlanExecutor`, `IActiveDirectoryService`, validators) respect lifetimes.
- Use Serilog enrichment to stamp `jobId` on log scope for all background execution logs.

## Job Lifecycle
1. Controller validates the query, produces a `DirectoryQueryPlan`, and asks `QueryJobManager.CreateJobAsync` to enqueue it.
2. Manager writes the new job (status `Queued`) to the store, pushes a lightweight work item onto the queue, and returns `202 Accepted` plus `jobId`.
3. `QueryJobExecutorHostedService` drains the queue, marks the job `Running`, timestamps `StartedAt`, and creates a linked cancellation token.
4. The hosted service resolves `IDirectoryPlanExecutor` within a fresh DI scope and calls the new overload with a progress adapter.
5. Each progress update refreshes `NodesProcessed`, `CurrentDepth`, `EstimatedTotal`, and `Warnings` inside the store.
6. On success, the executor streams rows to disk, promotes them into `IMemoryCache`, sets `ResultsCacheKey`/`TotalRows`, flips status to `Completed`, and records `CompletedAt`.
7. On failure, capture the exception message (details remain in logs), set status `Failed`, populate `ErrorMessage`, and keep partial diagnostics.
8. Cancellation flows through a `CancellationTokenSource` stored per job; controllers call `CancelJobAsync`, the hosted service observes cancellation, and executor unwinds gracefully (status `Cancelled`).

## Progress Callback Design
- `DirectoryPlanExecutor` emits `PlanProgressUpdate` after each breadth-first level, after aggregation, and at completion.
- Throttle updates to avoid lock contention: publish when either the depth changes or `NodesProcessed` increases by at least 250.
- The hosted service wraps `IProgress<T>` so that each update becomes `IQueryJobStore.UpdateProgress`.
- Non-recursive plans can reuse the callback by emitting one update with the final record count (maintains consistent API shape).

## LDAP Batching Strategy
- `System.DirectoryServices.DirectorySearcher` already supports OR filters via the existing `BuildCompoundFilterClause`, which outputs patterns like `(|(...))`.
- Manager expansion loop:
  1. Collect unique manager distinguished names for the current breadth-first level.
  2. Chunk the list in groups of at most 50 to keep each LDAP filter below roughly 10 KB (safe for AD).
  3. For every chunk, build a filter such as `(&(objectCategory=person)(objectClass=user)(|(manager=CN=Hernandez\, Jose,...)(manager=CN=Smith\, Ann,...)))`.
  4. Execute chunked searches sequentially within the level; allow at most two concurrent `DirectorySearcher` instances overall to avoid binding storms.
- Identity lookups support aliases by expanding the allow list to include `mail`, `userPrincipalName`, and `proxyAddresses` while defaulting to `sAMAccountName`. Each attribute remains explicitly enumerated to avoid broad wildcard searches.
- Unit tests parse the generated filters with `System.DirectoryServices.Protocols.SearchRequest` to prove correctness and guard against malformed syntax.

## Performance and Limits
- Breadth-first traversal ensures only the current frontier plus visited set remain in memory; result rows stream to a temporary file which is then promoted to cache.
- Default limits from the workflow document are enforced:
  - `maxDepth = min(requested ?? 10, config.MaxRecursionDepth)`
  - `maxNodes = min(requested ?? 10000, config.MaxNodesPerRecursion)`
- For 40K node requests:
  - Guard with a `SemaphoreSlim(3)` inside the hosted service to cap concurrent heavy jobs.
  - Write rows to `Path.GetTempFileName()` and expose a streaming reader when the user downloads results, avoiding 40K-row lists in RAM.
  - Perform aggregation incrementally as each level completes so there is no O(n) post-processing spike.
- Instrument jobs with EventCounters (nodes processed, AD call count, elapsed time) so stress tests can confirm assumptions.

## Integration Concerns and Mitigations
- **Scoped lifetime safety**: Each job runs inside a DI scope; `IServiceScope` is disposed even when cancellation or faults occur.
- **Job ownership**: `QueryJobStore` persists `UserName`; read/cancel endpoints verify the caller matches to prevent cross-tenant leakage.
- **Concurrency**: `InMemoryQueryJobStore` uses `ConcurrentDictionary<string, QueryJob>` and `AddOrUpdate` to avoid coarse locks while still providing atomic updates.
- **App recycle**: Jobs vanish on process restart; README must call this out and list durable store migration as a follow-up.

## Testing Strategy
- **Unit tests**: enqueue/dequeue semantics, progress throttling, LDAP filter chunking, cancellation propagation.
- **Integration tests**: use an in-memory LDAP harness (or lightweight stub) to validate breadth-first traversal and aggregation logic end to end.
- **Load tests**: simulate 40K nodes with stubbed LDAP responses to measure throughput, queue depth, and memory footprint.
- **Contract tests**: verify new endpoints surface in Swagger and the UI polling logic matches response shapes.

## Workflow Linkage
- Phase 2/3 in the workflow doc: DI wiring and hosted service contract are now explicit.
- Phase 4: progress callback interface and throttling strategy are specified.
- Phase 9: LDAP batching syntax and guardrails are detailed with concrete limits.
- Keep this document in sync as implementation lands; it is the reference blueprint for recursion infrastructure.

**Version**: 2025-10-20  
**Author**: Codex assistant  
**Status**: Draft (ready for review)
