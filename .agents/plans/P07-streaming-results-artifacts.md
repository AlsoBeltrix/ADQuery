# P07 — Streaming Results, Exports, and Artifact Caching

Status: **Reviewed — implementation is not authorized**

Owner approval: Pending

Implementation dependencies: P01 verification foundation and P06 finite query-work budgets must land first. P07 must land before P11 and P14 consume its row-source and artifact interfaces. P16 later moves the interim validated artifact root beneath its broader portable `DataRoot` contract; P16 is not a prerequisite.

Review status: Advisory review complete after 3 rounds; the final-round required change is applied without a fourth review

## Problem

Successful query and CSV-enrichment paths currently retain and copy complete row collections, build complete exports in managed memory, and cache mutable rows in `IMemoryCache`. Downloads either call `ReadAllBytes` or regenerate a complete CSV, Excel, HTML, or text payload before returning it.

P06 makes row count, aggregation groups, directory work, and execution time finite, but intentionally assigns output-byte limits to P07. A bounded logical result can still exist simultaneously as executor rows, preview clones, cache clones, a complete `StringBuilder` or workbook, a complete byte array, and a filesystem copy. Wide rows and large cell values therefore remain capable of disproportionate memory and disk pressure.

Artifact ownership, expiry, publication, cleanup, and format generation are also split across controllers, the job manager, memory cache, and direct filesystem calls. There is no shared byte quota, backpressure, single-flight export generation, immutable reference, or atomic staging contract.

## Repository evidence

- `QueryController.ExecuteQuery` receives the complete `PlanExecutionResult.Data` list, clones preview rows, builds a complete CSV byte array, writes it with `File.WriteAllBytes`, and clones all rows again into a 30-minute memory-cache entry.
- `CachedQueryResult` is a controller-private mutable row cache containing owner, query, context, log path, output path, and rows.
- Synchronous download calls `File.ReadAllBytes` for an existing CSV or regenerates the full requested format from cached rows.
- `GenerateFileContent` returns `byte[]` for every format.
- CSV, HTML, and plain-text generation use whole-document `StringBuilder` instances.
- Excel generation builds a complete ClosedXML workbook, saves it to `MemoryStream`, and calls `ToArray`.
- CSV enrichment repeats complete CSV generation, disk write, and row caching.
- `QueryJobManager` stores a mutable `PlanExecutionResult` in `IMemoryCache` for two hours.
- Async preview and download retrieve that full object; async download again generates a complete byte array, writes it, and returns it.
- Sync results expire after 30 minutes, async result cache entries after two hours, and job metadata after 24 hours.
- `QueryLogHelper.OutputRoot` is hard-coded to `E:\WWWOutput`. P16 owns the broader portable storage-root and logging design.
- P06’s accepted plan makes final rows and aggregation groups finite, makes executor aggregation authoritative, forbids successful partial results after hard-budget failure, and explicitly delegates encoded-output limits to P07.

## Goals

1. Establish one versioned, immutable, format-neutral canonical result artifact.
2. Stop caching complete mutable row collections in memory.
3. Stop cloning full result sets in controllers.
4. Bound canonical bytes, export bytes, cell bytes, metadata bytes, artifact counts, retained bytes, and pending export work.
5. Write canonical rows and every export format incrementally with a fixed-size buffer.
6. Await every read and write so slow disk and HTTP consumers apply backpressure.
7. Generate format variants lazily and single-flight by artifact, format, and formatter version.
8. Publish only closed, validated artifacts through atomic same-volume moves.
9. Remove staging files after cancellation, failure, or lost publication.
10. Centralize owner authorization and never treat an opaque ID as a bearer credential.
11. Keep active readers safe while cleanup expires artifacts.
12. Make sync, queued, retry, and CSV-enrichment paths use the same artifact interface.
13. Provide prepare, publish, lease, and remove operations for P14.
14. Preserve P06 row, aggregation, and no-partial semantics without charging them twice.
15. Provide deterministic backpressure, publication, ownership, cleanup, parity, and allocation guards.

## Non-goals

- Do not change any P06 work or row ceiling.
- Do not redesign projection or aggregation; P11 owns those algorithms.
- Do not implement atomic job transitions; P14 consumes this interface.
- Do not define the repository-wide portable data/log root or ACL policy; P16 later absorbs the interim root.
- Do not change CSV ingestion; P18 owns it.
- Do not add cloud object storage, a database, or a distributed cache.
- Do not expose or accept physical artifact paths.
- Do not retain a successful partial artifact after a byte, cancellation, or write failure.
- Do not claim directory execution itself is streaming before P11 changes its producer.
- Do not require live AD, IIS, or deployed storage in automated tests.

## Accepted P06 boundary

P07 implements against these accepted P06 facts:

- Every query has finite `MaxOutputRows` and `MaxAggregationGroups`.
- Executor aggregation is authoritative and is not recomputed by controllers or queued jobs.
- Controller-side post-execution truncation is removed.
- Hard-budget failure returns no successful partial rows.
- P06 deliberately does not bound encoded output bytes.

Initial P07 adapts the P06-bounded materialized result without cloning it. P11 later produces the same P07 row-source contract directly and may remove the remaining executor materialization. P07 must not hard-depend on P11.

## Invariants

- One successful execution publishes exactly one canonical artifact.
- Exactly one application process owns an artifact root at a time; a second IIS worker fails startup rather than bypassing capacity accounting.
- The canonical artifact is the only post-publication source for preview and exports.
- Published artifacts are immutable.
- Artifact IDs are opaque identifiers, not paths or authorization.
- Physical files are invisible until closed, validated, and atomically published.
- Actual encoded bytes, including separators, escaping, container overhead, and metadata, count toward limits.
- Exactly at a byte limit succeeds; the next byte fails before it is written.
- Canonical and variant capacity is reserved atomically before staging writes; abandoned reservations are always released.
- Canonical failure fails the query and publishes nothing.
- One export-format failure leaves the canonical artifact and other formats usable.
- No exporter buffers the complete document or advances an unbounded number of rows.
- Absolute expiry is not extended by access.
- Cleanup never deletes an actively leased artifact.
- P06 output rows and aggregation groups are not recounted or recomputed.
- Canonical string values remain exact; CSV and Excel presentation neutralize spreadsheet formulas without changing canonical data.
- A published artifact always has a final commit marker whose hashes cover the manifest and canonical rows.

## Proposed configuration

Add an interim validated section:

```json
"Artifacts": {
  "RootPath": "E:\\WWWOutput\\artifacts",
  "CanonicalRetentionMinutes": 120,
  "CleanupIntervalMinutes": 5,
  "StagingRetentionMinutes": 15,
  "MaxCanonicalArtifactBytes": 67108864,
  "MaxExportArtifactBytes": 134217728,
  "MaxManifestBytes": 1048576,
  "MaxCommitMarkerBytes": 4096,
  "MaxPreviewRows": 20,
  "MaxPreviewBytes": 524288,
  "MaxCellUtf8Bytes": 65536,
  "MaxColumns": 64,
  "WriteBufferBytes": 65536,
  "MaxConcurrentCanonicalPreparations": 2,
  "MaxPendingCanonicalPreparations": 8,
  "MaxConcurrentExportGenerations": 2,
  "MaxPendingExportGenerations": 16,
  "MaxArtifactsPerOwner": 50,
  "MaxBytesPerOwner": 536870912,
  "MaxTotalArtifactBytes": 2147483648,
  "MinimumFreeDiskBytes": 1073741824
}
```

These exact initial ceilings are 64 MiB for all canonical files together, 128 MiB per export, 1 MiB for the complete manifest, 4 KiB for the commit marker, 20 preview rows and 512 KiB of retained preview cells, 64 KiB per encoded cell, 64 KiB buffers, two active/eight pending canonical preparations, 512 MiB per owner, 2 GiB total retained-or-reserved bytes, and 1 GiB free-disk headroom.

Add `ArtifactOptions`, bind it, validate it, and call `ValidateOnStart()`.

Reject:

- A missing or non-absolute root.
- A drive root, filesystem root, repository root, or regular file as the root.
- Zero or negative bytes, counts, concurrency, retention, queue, preview, or intervals.
- Buffers outside 4 KiB through 1 MiB.
- Per-owner bytes above global bytes.
- Export bytes below canonical bytes unless explicitly approved.
- Manifest or preview bytes above the canonical-artifact ceiling.
- A commit-marker bound too small for the fixed schema or greater than the canonical-artifact ceiling.
- Preview rows above P06's final output-row ceiling.
- Column capacity below the authoritative validated schema maximum.
- Overflowing duration or byte conversions.

Tests always use a dedicated temporary directory.

P16 later replaces `Artifacts:RootPath` with an artifacts child under its validated `DataRoot`, preserving all P07 quotas and interfaces.

At startup, acquire a process-lifetime exclusive root lease using a create-new/open-with-`FileShare.None` lock file beneath the validated root. Hold the handle until graceful shutdown. If another worker owns the root, fail startup/readiness with `artifact_root_in_use`; do not fall back to a process-local quota view. This makes the capacity coordinator authoritative even during IIS overlap. P15/P16 must later configure and verify a single-writer application-pool lifecycle before production promotion.

## Core contracts

### Finalized row source

Add:

```csharp
public interface IResultRowSource
{
    ResultSchema Schema { get; }
    long? KnownRowCount { get; }
    IAsyncEnumerable<ResultRow> ReadRowsAsync(CancellationToken cancellationToken);
}
```

Initial P07 uses a one-pass adapter over P06’s bounded `PlanExecutionResult.Data` and does not clone it. P11 later emits this interface directly.

### Schema and cells

Use a versioned ordered schema and normalize current `object?` cells once at the artifact boundary into:

```text
null
string
boolean
integer
decimal
finite floating point
UTC date/time
ordered scalar list
```

Rules:

- Preserve JSON booleans and numbers.
- Encode dates as UTC ISO 8601.
- Reject non-finite floating-point values or use one documented string representation.
- Convert unsupported provider objects to invariant strings once.
- Preserve list order.
- Enforce cell bytes after canonical encoding and after format-specific escaping.
- Enforce column count before row enumeration.
- Preserve authoritative column spelling and order with ordinal-ignore-case uniqueness.
- Do not infer numeric values with current-culture parsing.

Canonical string values are never formula-sanitized or otherwise changed. Formula neutralization is a presentation rule applied only by spreadsheet-oriented exporters, so later exporters can still reproduce the exact canonical value.

### Artifact descriptor

The immutable descriptor contains:

```text
ArtifactReference
OwnerSubject
SchemaVersion
CreatedAt
ExpiresAt
RowCount
CanonicalBytes
RowsSha256
ManifestSha256
OrderedSchema
BoundedPreviewRows
AuthoritativeAggregation
BoundedWarnings
SafeQueryMetadata
```

Safe metadata may include display query and model only where current download behavior requires them. It never includes secrets, raw model response, full plans, credentials, or physical paths.

`MaxManifestBytes` covers the complete encoded `manifest.json`, including schema, owner subject, preview rows, authoritative aggregation, warnings, safe query metadata, counts, and row digest. `MaxPreviewRows` and `MaxPreviewBytes` additionally bound preview retention before manifest serialization; once either preview bound is reached, stop retaining preview cells without affecting canonical rows. Aggregation and warnings have no side channel around the manifest cap: serialize them through a counting `Utf8JsonWriter` and fail canonical preparation before publishing if the complete manifest would exceed the cap.

P06's `MaxAggregationGroups` is a logical-work ceiling, not a promise that every maximum-sized group encoding fits P07's output ceiling. The 1 MiB manifest cap intentionally bounds descriptor/preview opens; a P06-valid result whose aggregation, warnings, or metadata exceed it fails atomically with `artifact_manifest_too_large` and no successful result. Record manifest-size failures and tune the owner-approved P07 ceiling from evidence rather than truncating, dropping, or moving authoritative aggregation behind an unbounded side channel.

`MaxCanonicalArtifactBytes` covers the exact file lengths of `rows.ndjson`, `manifest.json`, and `commit.json` together. `RowsSha256` covers the exact canonical row-file bytes. After the manifest is closed and hashed, preparation serializes the fixed-shape `commit.json` payload into a bounded buffer no larger than `MaxCommitMarkerBytes`; it records `RowsSha256` plus `ManifestSha256`. Preparation succeeds only when `rows length + manifest length + exact commit-buffer length <= MaxCanonicalArtifactBytes`. Publication writes those exact prechecked commit bytes last. These hashes detect corruption; they are not signatures and do not establish authenticity against an attacker who can rewrite the artifact root.

### Store interface

Add:

```csharp
public interface IResultArtifactStore
{
    Task<PreparedResultArtifact> PrepareAsync(
        ResultArtifactWriteRequest request,
        IResultRowSource rows,
        CancellationToken cancellationToken);

    Task<ResultArtifactDescriptor> PublishAsync(
        PreparedResultArtifact prepared,
        CancellationToken cancellationToken);

    Task AbortAsync(
        PreparedResultArtifact prepared,
        CancellationToken cancellationToken = default);

    Task<ResultArtifactLease?> OpenAsync(
        ArtifactReference reference,
        string ownerSubject,
        CancellationToken cancellationToken);

    Task<bool> RemoveAsync(
        ArtifactReference reference,
        CancellationToken cancellationToken);
}
```

P14 consumes this interface and never receives an `IMemoryCache` key or physical path.

## Canonical representation and publication

Use a versioned manifest plus UTF-8 newline-delimited canonical JSON rows:

```text
<root>/
  .staging/<random-staging-id>/
    manifest.json
    rows.ndjson
  results/<opaque-prefix>/<artifact-id>/
    manifest.json
    rows.ndjson
    commit.json
    exports/<formatter-version>/
      result.csv
      result.xlsx
      result.html
      result.txt
```

The layout is private. Staging files already use their final inner names; the staging directory and absence of `commit.json` identify incompleteness. A directory move therefore cannot leave `.tmp` names that readers do not recognize.

### Atomic capacity admission

Within the process that holds the exclusive root lease, one `IArtifactCapacityCoordinator` owns canonical-preparation slots, pending count, canonical artifact counts, and all retained-or-reserved byte totals. It uses one short lock or equivalent linearizable update; no filesystem operation or await occurs inside that update.

Before creating staging or reading the first row, canonical admission runs these phases:

1. Enter the bounded canonical preparation queue; more than `MaxPendingCanonicalPreparations` returns `artifact_generation_busy` without retaining another queued row source.
2. Await one of exactly `MaxConcurrentCanonicalPreparations` active slots outside the coordinator's reservation lock.
3. Query current free space outside the lock.
4. Inside one short atomic update, recheck pending/active ownership and admit only if adding this ticket keeps the owner's provisional-plus-published artifact count at or below `MaxArtifactsPerOwner`, owner retained-or-reserved bytes at or below `MaxBytesPerOwner`, global retained-or-reserved bytes at or below `MaxTotalArtifactBytes`, and `captured available - other outstanding reservations - MaxCanonicalArtifactBytes >= MinimumFreeDiskBytes`; then reserve one owner artifact count plus the full `MaxCanonicalArtifactBytes`.
5. Return one idempotently disposable reservation ticket and leave the lock before filesystem work.

Queue entries are references to already P06-bounded results, but their count remains finite. Cancellation while pending removes the entry and retains no ticket. The active ticket is released exactly once on abort, cancellation, serialization failure, write failure, or admission rollback. On successful publication, atomically convert the provisional maximum reservation to the artifact's exact canonical bytes and release the difference.

Every export variant similarly reserves its full `MaxExportArtifactBytes` before creating variant staging, within the already bounded export worker/queue. Convert it to exact bytes on publication and release it on every failure. Owner/global byte totals include published canonical files, published variants, active canonical reservations, and active export reservations. Canonical artifact count includes published artifacts plus provisional canonical tickets; variants do not consume another artifact-count unit.

Before every chunk write, recheck actual free-space headroom to detect unrelated processes consuming the volume. A failed recheck aborts and releases the ticket. Logical reservations coordinate this application; they do not claim to reserve filesystem space from other processes.

Preparation:

1. Acquire the atomic canonical capacity ticket before staging work.
2. Generate independent opaque artifact and staging IDs.
3. Resolve and verify paths beneath the normalized root.
4. Create staging with create-new semantics.
5. Stream rows one at a time.
6. Incrementally count bytes and rows, hash bytes, verify schema, and retain preview rows only within both preview bounds.
7. Check canonical total, cell, manifest, reservation, and actual free-space capacity before every write.
8. Write and byte-check the complete manifest through the counting writer.
9. Flush, close, and verify row/manifest lengths and hashes.
10. Serialize the exact commit-marker payload into its bounded buffer and verify the three-file canonical total before any move.
11. Return a prepared handle that owns the live reservation ticket and exact commit-marker bytes.

Publication:

1. Revalidate the live ticket, exact staged lengths, commit-buffer length, and the prechecked three-file total.
2. Move the closed staging directory to its final same-volume location.
3. Write the exact prepared `commit.json` bytes with create-new semantics only after the manifest and row files are final; readers require it. Successful marker creation is the durable commit point.
4. Atomically convert the provisional ticket to exact retained bytes and register the immutable descriptor in the rebuildable in-memory index.
5. Return only the opaque reference.

If atomic directory move is not proven on the target filesystem, move files with create-new same-volume renames. The final commit marker is required in either case, so readers never infer completeness from directory visibility alone.

Track publication state as `Staging`, `MovedUncommitted`, `DurablyCommitted`, or `Registered`. Cancellation, overflow, serialization error, disk error, or failed publication before the durable commit point removes the staging or moved-uncommitted final directory and exposes no success.

Release the provisional ticket only after deletion is confirmed. If moved-directory deletion fails, convert the ticket to a conservative orphan reservation of `MaxCanonicalArtifactBytes`, quarantine the path from readers, and leave it charged until cleanup succeeds.

After marker creation, publication never rolls the directory back merely because volatile registration or response delivery fails. Reconcile the live ticket to exact retained bytes and register or queue the committed directory for in-process reconciliation and normal expiry even when no caller received its reference. A process crash after marker creation leaves the same valid on-disk state as any other committed artifact; startup therefore recovers it. In-memory registration is never used as a durable discriminator, and a valid marker is never deleted merely because no prior registry entry exists.

`OpenAsync` validates the bounded commit-marker schema and length, then hashes and validates the complete bounded manifest before returning its descriptor or preview. It deliberately does not scan `rows.ndjson`, so preview remains bounded. The first full sequential canonical read, including every export generation, hashes the row bytes while reading and must match the marker's row length and `RowsSha256` before a variant can commit. On mismatch, delete variant staging, mark the canonical artifact corrupt, reject later opens, and let cleanup remove it after active leases drain. No unverified canonical row bytes or derived variant are served.

## Ownership and leases

- Parse references through one strict type; reject separators, traversal, roots, and alternate stream syntax.
- Verify every resolved path stays under the normalized root.
- Do not put owner names in paths.
- Store the normalized Windows SID string in the hashed manifest and registry as `OwnerSubject`. Do not store a domain-stripped account name as the authorization key.
- `OpenAsync` requires the authenticated principal's Windows SID and compares canonical SID strings ordinally. If a SID is unavailable, fail creation/opening rather than fall back to display name, SAM account name, UPN, or artifact opacity.
- Do not follow reparse points inside the artifact root without an explicit hardened policy.

`ResultArtifactLease` is `IAsyncDisposable`. Cleanup marks expired content pending deletion, rejects new opens, waits for active leases, then deletes. Controllers keep a lease alive until ASP.NET completes response copy.

## Streaming exporters

Add one `IResultExporter` per supported format. Each reads canonical rows sequentially and writes to a destination stream with the configured buffer.

Every exporter:

- Awaits every read and `WriteAsync`.
- Checks cancellation between rows and chunks.
- Counts actual bytes.
- Checks before a write that would exceed `MaxExportArtifactBytes`.
- Preserves schema order and canonical values.
- Includes bounded metadata, aggregation, and warnings.
- Never returns a complete `string` or `byte[]`.
- Never uses `MemoryStream.ToArray`.

CSV writes metadata comments, headers, rows, aggregation, and warnings incrementally with stable RFC-compatible quoting. Quoting is not formula protection. For every canonical string cell whose first non-whitespace character is `=`, `+`, `-`, or `@`, or whose first character is tab or carriage return, emit a leading apostrophe inside the quoted CSV field. Apply the same rule to headers and string-valued metadata. Canonical data remains unchanged, and exports document this presentation transformation.

Text writes each section and row incrementally with invariant formatting.

HTML writes a fixed wrapper and encoded metadata, summaries, headers, cells, and footer incrementally. Every untrusted value is HTML-encoded.

Excel replaces the ClosedXML object model with a forward-only Open XML writer using an explicit audited direct package. It uses fixed or bounded-sample widths, not full-data auto-fit, and preserves canonical booleans, numbers, UTC dates, strings, and blanks. Every canonical string, including a formula-looking string, is emitted as an explicit text cell with no formula element and no formula data type. Numeric/date/boolean canonical cells retain their safe typed forms.

Formula-safety fixtures include leading whitespace before `=`, `+`, `-`, and `@`; leading tab and carriage return; DDE-style strings; benign negative numeric values represented as canonical numbers; and ordinary strings. Tests inspect CSV bytes and the raw worksheet XML, proving dangerous strings are neutralized and no `<f>` formula node is present.

## Lazy export caching and backpressure

Do not generate all formats at query completion. Generate a variant on first request and cache it only until the canonical artifact’s absolute expiry.

Single-flight key:

```text
(artifact ID, canonical version, format, formatter version)
```

Use exactly `MaxConcurrentExportGenerations` tracked workers and a bounded channel of `MaxPendingExportGenerations` unique keys. Do not use detached `Task.Run`.

When full, return typed `export_generation_busy` and let P13 map it to HTTP 503 with bounded `Retry-After`.

One requester cancelling stops that requester waiting or copying but does not cancel a shared generation still needed by other waiters. Service shutdown cancels generation. A failed task is removed from single-flight state so a later request can retry.

Variant publication uses create-new staging, exact byte checks, close and flush, then an atomic rename. No partial variant is visible.

## Download behavior

Download validates reference, owner, format, and canonical availability before generation.

It obtains the immutable variant before committing response headers, then:

- Keeps the lease through response completion.
- Sets content type, safe generated filename, and exact `Content-Length`.
- Copies asynchronously with the configured bounded buffer.
- Awaits response writes and observes `RequestAborted`.
- Supports range requests only when lease and immutable-file semantics are correct.
- Never calls `ReadAllBytes`.

A disconnected client may receive a partial network transfer, but the cached artifact remains complete and immutable.

## Execution-path publication

### Synchronous and CSV

1. Receive P06-bounded successful finalized result.
2. Adapt without cloning.
3. Prepare and publish canonical artifact.
4. Build preview from the descriptor.
5. Return success with opaque artifact reference.
6. Release the execution result after response construction.

Canonical publication failure means no successful preview or download reference.

### Queued path before P14

P07 migrates queued results from `IMemoryCache`:

1. Publish the canonical artifact before the current completion call.
2. Store the opaque reference rather than a cache key.
3. Use the descriptor for preview and download.
4. Remove the artifact on observable failure before completion.

The current mutable job-state race remains explicitly owned by P14; P07 does not claim atomic queued completion.

### Queued path after P14

P14 publishes through P07, atomically transitions to completed with the reference, and removes the artifact if cancellation or a stale lease wins.

## Capacity, cleanup, and recovery

The atomic capacity coordinator admits and reserves canonical and variant work before any staging write, as defined above. Publication reconciles provisional maximum bytes to exact file lengths; it does not perform the first capacity check after data already exists.

Eviction order:

1. Expired staging.
2. Expired export variants.
3. Expired canonical artifacts.
4. Oldest unleased export variants whose canonical source remains valid.
5. Never evict an unexpired canonical artifact merely to admit a new result.
6. Fail with `artifact_store_full` if capacity remains unavailable.

Access never extends absolute expiry.

Periodic cleanup never treats a staging directory owned by a live reservation ticket as stale, regardless of age. Startup has no live tickets and may remove only validated staging directories older than `StagingRetentionMinutes`. Variant generation holds a canonical lease and rechecks canonical expiry immediately before variant commit; if the source expired, discard variant staging and do not publish it.

Results directories without a valid bounded `commit.json` are never artifacts. Periodic cleanup skips a final path named by a live `MovedUncommitted` ticket, but otherwise quarantines and deletes every missing/invalid-marker results directory. Startup holds the exclusive root lease and has no live tickets, so it performs the same incomplete-results sweep before recovery. This covers a process crash after the staging directory moved but before marker creation. A directory with a valid marker is durably committed and is loaded into the rebuildable registry, including after a crash before its original in-memory registration; if no caller received its opaque reference, it remains unreachable and expires normally.

Deletion and quota reconciliation use one order:

1. Mark the registered artifact, variant, or orphan reservation pending deletion and reject new leases.
2. Wait for existing leases where applicable.
3. Delete the exact validated in-root filesystem target.
4. Only after deletion succeeds, atomically decrement the coordinator's exact retained owner/global bytes and artifact count, or release the conservative orphan reservation.
5. If deletion fails, keep the object quarantined and fully charged for a later cleanup attempt.

At startup, rebuild retained counts from valid committed artifacts before opening admission. If an incomplete directory cannot be deleted, charge its safely measured bytes globally, using `MaxCanonicalArtifactBytes` as the conservative charge when measurement is incomplete; charge the owner too only when a bounded, hashed manifest yields a valid canonical SID. Keep this recovery reservation until deletion succeeds. Never make capacity available merely because an entry was removed from the in-memory registry.

At startup:

- Remove only validated in-root staging older than its retention.
- Quarantine and delete results directories without a valid bounded commit marker; retain a conservative recovery reservation when deletion fails.
- Load complete, supported, unexpired manifests.
- Never infer ownership from directory names.
- Do not serve incomplete, corrupt, or unknown-schema artifacts.
- Apply quotas to recovered artifacts.

P16 may refine filesystem permissions and root migration without changing these contracts.

## Failure contract

Stable causes:

```text
artifact_size_exceeded
artifact_manifest_too_large
artifact_store_full
artifact_generation_busy
artifact_root_in_use
artifact_write_failed
artifact_publish_failed
artifact_not_found
artifact_expired
artifact_forbidden
artifact_corrupt
export_size_exceeded
export_generation_busy
export_generation_failed
unsupported_export_format
```

P13 owns final problem details and retryability.

- Canonical size/write/publication failure fails the query with no partial success.
- Store full is a retryable capacity failure.
- Canonical generation busy is a retryable bounded-admission failure.
- Root-in-use is a startup/readiness failure, not a request-time fallback to unsafe parallel writers.
- Export size failure preserves the canonical source.
- Not found or expired is a stable 404.
- Forbidden is 403 without disclosing owner metadata.
- Busy is 503 with `Retry-After`.
- Commit/manifest corruption fails every open. Row corruption detected during a full canonical read commits no derived variant, marks the artifact corrupt, and fails subsequent opens; bounded manifest previews do not scan the row file.

Client errors never include physical paths, row values, query text, stack traces, or raw exception messages.

## Telemetry

Use the P06 application meter.

Record:

```text
adquery.artifact.canonical_bytes
adquery.artifact.export_bytes
adquery.artifact.rows
adquery.artifact.write_duration
adquery.artifact.export_duration
adquery.artifact.active_leases
adquery.artifact.store_bytes
adquery.artifact.store_count
adquery.artifact.cleanup
adquery.artifact.failure
adquery.export.queue_depth
adquery.export.active_generations
adquery.export.cache_hit
adquery.export.singleflight_waiter
adquery.download.bytes
```

Allowed tags are artifact kind, format, formatter version, fixed outcome, and fixed failure reason. Never tag artifact ID, owner, query, path, filename, cell, or error message.

## Deterministic tests

Use temporary roots, `FakeTimeProvider`, and controlled streams. No test sleeps.

Test probes:

- A row source that throws if enumerated more than one row ahead.
- A stream that blocks each write behind a manual gate and records outstanding writes.
- A counting stream that retains no payload.
- A stream that fails at an exact byte.
- A fake disk-capacity provider.
- A controlled exporter with start/continue/finish gates.
- A seekable, temp-file-backed Excel probe that exposes destination-write gates and counts row-source lookahead without retaining workbook bytes in memory.

Required guards:

1. Invalid roots and every zero/unbounded option fail startup.
2. A second process/store instance cannot acquire the same root lease and cannot initialize an independent capacity coordinator.
3. All resolved paths remain under root and create-new cannot overwrite.
4. Artifact opacity cannot bypass owner authorization.
5. Empty and nonempty results publish correct immutable manifests.
6. Exact canonical, manifest, preview, commit-marker, and cell limits succeed; one encoded byte above publishes nothing. The canonical boundary includes the exact prepared marker bytes before the move.
7. UTF-8 byte counts handle multibyte input and format escaping.
8. Cancellation and write failure remove staging or an uncommitted final directory; a failed deletion remains quarantined and charged.
9. Final artifact is invisible before publication.
10. Commit/manifest digest mismatch and unknown schema prevent opening; a row digest mismatch during full sequential read prevents variant commit and poisons later opens.
11. Preview is bounded and schema order deterministic.
12. Writer never enumerates ahead while a destination write is blocked.
13. Outstanding writes and buffer sizes remain bounded.
14. CSV quoting, formula neutralization, text formatting, HTML encoding, and Excel typed cells match golden small results.
15. Excel opens through an independent Open XML reader; raw worksheet XML contains no formula elements for untrusted strings.
16. While the Excel destination write is blocked, the exporter cannot enumerate beyond the documented fixed row-lookahead window and cannot retain a workbook-scale object model.
17. An architecture/dependency guard rejects `ClosedXML`, `XLWorkbook`, `MemoryStream`, and `ToArray` in the Excel exporter and proves the ClosedXML package is absent after migration.
18. Exact export limit succeeds; one byte above leaves no variant.
19. Concurrent same-key requests execute one exporter.
20. Different keys never exceed active or pending generation bounds.
21. Full pending queue returns `export_generation_busy`.
22. One cancelled waiter does not cancel another waiter's generation.
23. Download copy uses bounded async reads and holds its lease.
24. Active lease prevents deletion; final lease completes pending deletion.
25. Absolute expiry is not refreshed by access.
26. Owner/global/free-disk capacity and eviction order are enforced.
27. Concurrent canonical preparations cannot exceed active/pending counts or retained-plus-reserved byte ceilings.
28. A failed, cancelled, or rejected preparation releases its exact provisional ticket once.
29. Startup recovers valid committed manifests and deletes missing/invalid-marker result directories, retaining a conservative recovery reservation on deletion failure.
30. Sync, CSV, and queued compatibility paths use one canonical store and no row memory cache.
31. Preview does not scan the full canonical artifact or exceed row/byte caps.
32. P06 row and aggregation counters are not charged again.
33. Executor aggregation is preserved without recomputation.
34. A preview reads only the bounded marker and manifest; it never scans canonical rows.
35. A full canonical read verifies row length and digest before publishing a derived variant.
36. Cleanup decrements retained bytes/count or releases an orphan reservation only after confirmed deletion; deletion failure stays fully charged.
37. A crash after move/before marker leaves an unreadable directory that startup reclaims; a crash after marker/before registration leaves a valid committed artifact that startup recovers, charges exactly, keeps owner-protected, and expires normally.

## Red/green proof

For each test-bearing slice:

1. Add the focused guard and confirm current behavior fails.
2. Implement the smallest change and confirm success.
3. Temporarily restore the old behavior or disable the new check.
4. Confirm the guard fails again.
5. Restore implementation and run P01’s canonical verification.
6. Commit only that slice.

Mandatory mutations:

- Restore full row cloning; no-copy guard fails.
- Restore whole-document `StringBuilder` or `byte[]` generation; backpressure guard fails.
- Restore `ReadAllBytes`; bounded download guard fails.
- Check size after writing; one-byte-over guard fails.
- Move canonical reservation after staging; concurrent-capacity and free-headroom guards fail.
- Leak a reservation on cancellation; exact reservation-release guard fails.
- Exclude exact commit-marker bytes from the preparation boundary; the canonical one-byte-over guard fails before publication.
- Register before move; publication-visibility guard fails.
- Drop moved-uncommitted recovery, treat a valid marker as dependent on volatile registration, or decrement quota before deletion; the distinct pre-marker/post-marker crash guards and retained-capacity guard fail.
- Publish a variant before validating the full row digest; the corruption guard fails.
- Skip owner comparison; authorization guard fails.
- Remove CSV formula prefixing or write an Excel formula element; spreadsheet-safety guards fail.
- Remove single-flight or queue bound; concurrency guard fails.
- Delete with an active lease; lease-race guard fails.
- Extend expiry on access; absolute-retention guard fails.
- Restore ClosedXML/`ToArray`; the source/dependency architecture guard and blocked-destination row-lookahead guard fail deterministically.

Leave no mutation in the worktree.

## Benchmarks

Extend P06’s benchmark project with canonical-write, export, and download benchmarks.

Parameters:

```text
Rows: 100, 1,000, 5,000
Columns: 5, 25, 64
Average cell bytes: 16, 256, 4,096
Formats: csv, text, html, excel
Destinations: counting stream and temporary file
```

Measure allocations, collections, throughput, maximum rows ahead, outstanding writes, retained bytes, and repeated-download cache-hit cost.

Gate structural facts rather than absolute timing:

- Row lookahead and outstanding writes remain bounded.
- No complete export-sized managed byte array exists.
- Retained managed memory does not grow proportionally with destination size.
- Repeated format requests reuse one immutable variant.
- Canonical row enumeration equals admitted P06 rows exactly once.

Run:

```powershell
dotnet run --project benchmarks/AdQueryOrchestrator.Benchmarks/AdQueryOrchestrator.Benchmarks.csproj -c Release -- --filter "*CanonicalArtifact*|*StreamingExport*|*ArtifactDownload*"
```

## Dependency boundaries

### P01

Supplies tests and canonical verification.

### P05

Bounds CSV input rows and batches. P07 owns resulting artifact bytes and downloads.

### P06

Owns logical rows, groups, work, time, and no-partial behavior. P07 owns encoded cells, metadata, artifacts, exports, storage, and post-execution backpressure. Do not duplicate accounting.

### P11

P07 lands first and supplies the row-source/schema contract. Initially it adapts bounded rows. P11 later produces that source directly and removes remaining producer materialization where feasible.

### P13

P07 supplies stable artifact causes. P13 owns causal classification, retryability, and HTTP problem details.

### P14

P07 lands first and supplies prepare, publish, lease, and remove. P14 later orders artifact publication with atomic job completion and removes artifacts after lost races.

### P16

P07 uses an interim validated root. P16 later relocates it beneath `DataRoot` and owns ACL, log, and broader storage guidance without changing callers.

## Implementation slices

Each slice is one commit and must be verified before the next begins.

### Slice 1 — Contracts and validated options

Commit: `feat: define bounded result artifact contracts`

- Add options, references, descriptors, schema/cells, row source, prepared handles, leases, failures, and no-clone adapter.
- Add option, normalization, and identifier/path tests.

### Slice 2 — Atomic canonical filesystem store

Commit: `feat: publish canonical result artifacts atomically`

- Add the exclusive process-lifetime root lease, bounded canonical admission, atomic provisional reservations, staging, byte counting, hashing, manifest/preview caps, final commit marker, prepare/publish/abort/open/remove, SID ownership, path containment, and leases.
- Add boundary, cancellation, corruption, publication, and authorization tests.

### Slice 3 — Migrate sync and CSV results

Commit: `refactor: store query results as canonical artifacts`

- Publish canonical artifacts before success.
- Build preview from descriptor.
- Remove `CachedQueryResult`, memory row clones, eager complete CSV bytes, and direct output writes from these paths.
- Add integration and P06 exactly-once tests.

### Slice 4 — Stream CSV, text, and HTML

Commit: `perf: stream text result exports`

- Extract exporters, use bounded async writes, enforce exact bytes, and add parity/backpressure tests.
- Remove whole-document builders for these formats.

### Slice 5 — Stream Excel

Owner Excel decision required.

Commit: `perf: stream excel result exports`

- Add the Microsoft `DocumentFormat.OpenXml` package at the P03-audited pinned version and implement a forward-only `OpenXmlWriter`/`SpreadsheetDocument` path.
- Add independent-reader parity, raw-XML formula guards, deterministic blocked-destination lookahead, and source/dependency architecture guards.
- Remove ClosedXML after verification and vulnerability audit.

### Slice 6 — Lazy export cache and streaming downloads

Commit: `feat: cache bounded export artifacts`

- Add tracked bounded generation workers, single-flight, atomic variant publication, and lease-safe async HTTP copy.
- Remove controller generation and `ReadAllBytes`.

### Slice 7 — Migrate queued result storage

Commit: `refactor: store queued results as artifacts`

- Replace queued `IMemoryCache` result objects with opaque references.
- Use descriptors for preview/download and preserve executor aggregation.
- Publish before current completion and record P14’s residual atomicity gap.
- Do not implement P14 transitions.

### Slice 8 — Capacity, cleanup, recovery, and metrics

Owner limit decision required.

Commit: `feat: bound artifact retention and capacity`

- Complete retained-capacity reconciliation, free-space rechecks, lease/ticket-aware cleanup, stale staging, recovery, export-first eviction, and telemetry.

### Slice 9 — Benchmarks and guidance

Commit: `perf: benchmark streaming artifact pipeline`

- Add structural benchmarks, record one Release baseline, and document manifest versions, tuning, metrics, and P16 migration.

Verification:

```powershell
pwsh -NoLogo -NoProfile -File scripts/verify.ps1
dotnet run --project benchmarks/AdQueryOrchestrator.Benchmarks/AdQueryOrchestrator.Benchmarks.csproj -c Release -- --filter "*CanonicalArtifact*|*StreamingExport*|*ArtifactDownload*"
```

## Acceptance criteria

- Every successful sync, CSV, and queued result references one canonical immutable artifact.
- No complete result rows remain cached in `IMemoryCache`.
- Controllers do not clone full rows or build complete exports.
- No download uses `ReadAllBytes`.
- Exact actual bytes are checked before every over-limit write.
- Exactly-at-limit succeeds and one-byte-over publishes nothing.
- Cell, manifest, preview, artifact, owner, global, and free-disk ceilings are finite.
- Manifest, preview rows, and preview bytes have explicit finite scopes; canonical totals include rows, manifest, and commit marker.
- Canonical and variant writers reserve worst-case bytes before staging and release or reconcile every ticket exactly once.
- Canonical and variants publish staging-first and atomically.
- Missing/invalid-marker final directories are quarantined and reclaimed after a failed publication or process crash.
- Retained bytes, counts, and orphan reservations are released only after confirmed filesystem deletion.
- A second IIS worker cannot open the same root or create an independent capacity view.
- Export variants are lazy, immutable, versioned, single-flight, and bounded in concurrency and queueing.
- Slow consumers create backpressure without pending-memory growth.
- All exporters are forward-only; Excel no longer uses ClosedXML or `MemoryStream.ToArray`.
- CSV and Excel cannot interpret canonical strings as formulas.
- Owner authorization is independent of ID opacity.
- Preview validates marker and manifest without scanning rows; every full canonical read verifies row length and digest before committing a derived variant.
- Active leases prevent cleanup races and access does not extend expiry.
- Canonical failure produces no successful partial result; export failure preserves the source.
- P06 rows/groups are not double-counted or recomputed.
- P14 can consume the interface without paths; P16 can relocate the root without caller changes.
- Deterministic guards and benchmarks pass.
- P01 canonical verification passes.
- Each slice is committed separately.

## Rollback

Use new revert commits; do not rewrite history.

- Telemetry and benchmarks may revert independently.
- Variant caching may be disabled while retaining bounded staging generation.
- If one exporter is unsafe, disable that format rather than restore complete buffering.
- Do not restore full row memory caching, `ReadAllBytes`, or complete export arrays.
- Do not restore controller-private ownership checks.
- If publication atomicity is unproven, fail publication rather than serve staging.
- P16 rollback retains a validated P07 root and path checks.
- P14 rollback retains opaque references and publish-before-completion ordering.
- Manifest rollback requires a compatible reader or explicit invalidation; never reinterpret silently.

## Risks

- **Initial executor rows remain materialized.** P06 bounds them; P07 removes downstream copies; P11 later streams the producer.
- **Disk replaces memory as the constrained resource.** Enforce per-artifact, owner, global, retention, and free-space ceilings.
- **Interim root overlaps P16.** Keep it behind options and one interface.
- **The exclusive root lease rejects default IIS overlap.** This is intentionally fail-closed for storage integrity; P15/P16 must verify a single-writer pool lifecycle before production promotion.
- **Manifest versions require compatibility discipline.** Version and reject unknown schemas.
- **Atomic move differs by filesystem.** Keep staging on one volume and use a commit marker if needed.
- **Canonical normalization may change formatting.** Use one typed contract and golden tests; P11 later emits it directly.
- **Forward-only Excel is more work.** It removes workbook-scale retention and is independently verifiable.
- **Lazy variants add first-download latency.** Single-flight and caching bound the tradeoff.
- **A disconnected waiter may leave generation running.** Work is globally bounded and reusable.
- **Cleanup races with readers and P14.** Use immutable references, leases, and pending deletion.
- **Metadata can contain sensitive query text.** Bound and owner-protect it; P16 owns filesystem permissions.
- **P07 cannot fix current mutable job-state publication.** Keep the residual explicit until P14.

## Open owner decisions

### Decision 1 — Canonical versus eager CSV

Choose one disk-backed canonical artifact with lazy formats or continued eager CSV output. Recommendation: canonical versioned NDJSON plus lazy formats; this removes duplicate work and provides one format-neutral source, at the cost of first-download generation latency.

Blocked until decided: Slices 2–4.

### Decision 2 — Initial limits and retention

Approve 64 MiB total canonical, 128 MiB per export, 64 KiB per cell, a 1 MiB complete manifest, 20/512 KiB preview bounds, two-hour absolute retention, 50 artifacts and 512 MiB per owner, 2 GiB retained-or-reserved total, and 1 GiB free headroom. Recommendation: start finite and tune from metrics.

Blocked until decided: Slices 1 and 8 values.

### Decision 3 — Generation backpressure

Choose bounded waiting or fail-fast behavior when canonical or export work is full. Recommendation: two active/eight pending canonical preparations and two active/16 pending unique variants, then HTTP 503 with `Retry-After`; unbounded waiting converts disk pressure into request and retained-row pressure.

Blocked until decided: Slice 6.

### Decision 4 — Excel engine

Choose forward-only Open XML or retain ClosedXML behind lower limits. Recommendation: use Microsoft's `DocumentFormat.OpenXml` package at a P03-audited pinned version and remove ClosedXML after parity tests; lower limits do not remove workbook-scale managed allocation.

Blocked until decided: Slice 5.

## Advisory Review

### Round 1 — 2026-07-21T21:27:36Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Revisions required

- Added bounded canonical preparation concurrency/queueing and an atomic worst-case reservation before any canonical or variant staging write, with exact reconciliation and release on every exit.
- Added CSV apostrophe neutralization and forced-text Excel cells for formula-looking strings, plus byte/XML golden guards.
- Replaced the vacuous Excel mutation proof with deterministic source/dependency bans and blocked-destination row-lookahead checks.
- Defined exact canonical, manifest, preview, export, and digest byte scopes; added a required final commit marker and final staging filenames.
- Replaced domain-ambiguous account-name authorization with canonical Windows SID ownership and described hashes as corruption detection rather than signatures.

### Round 2 — 2026-07-21T21:39:31Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Revisions required

- Included the exact bounded `commit.json` bytes in preparation-time canonical admission and revalidated those same bytes before final publication, closing the one-marker-over limit gap.
- Added explicit `MovedUncommitted` cleanup and startup recovery for a crash after directory move but before marker creation.
- Kept failed deletions quarantined and fully charged, and made cleanup release retained counts/bytes or orphan reservations only after confirmed filesystem deletion.
- Defined bounded preview verification separately from full row-digest verification: preview hashes only the marker/manifest, while export generation verifies the complete row stream before committing a variant.

### Round 3 — 2026-07-21T21:49:01Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort

**Verdict:** Revisions required; final permitted round

- Confirmed the prior capacity, exact marker-byte, formula-safety, SID ownership, corruption-verification, and forward-only export repairs.
- Corrected the remaining crash-state contradiction: a missing marker after move is incomplete and reclaimed, while a valid marker is the durable commit point and is recovered even when the process crashed before volatile registration.
- Updated the crash guards to expect those distinct outcomes and kept unreachable recovered artifacts owner-protected, charged, and subject to normal expiry.
- Applied the non-blocking clarity suggestions by enumerating every atomic admission ceiling, documenting the intentional P06-group/P07-manifest interaction, and naming Microsoft's `DocumentFormat.OpenXml` package subject to P03 pinning and audit.
- No fourth substantive review is authorized. The reviewer assessed the rest of the plan as implementable; this final-round repair has not been independently re-reviewed.

Record no more than three headless Claude review rounds. Each round must identify material findings, the resulting revision or retained disagreement, and the reviewer's final assessment.
