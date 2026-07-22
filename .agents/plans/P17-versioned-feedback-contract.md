# P17 — Versioned Feedback Storage and Analyzer Contract

Status: **Reviewed — implementation is not authorized**

Owner approval: Pending

Implementation dependencies: P01, P02, P07, P12, P13, P14, and P16 must land first. P14 must supply its private 24-hour `IQueryJobFeedbackTargetReader` receipt; P16 must supply `IDataPaths.FeedbackRoot`, the process-lifetime DataRoot lease, `IFeedbackQueryFingerprintSecretSource`, validated configuration/secret binding, and the logging/ACL contract. Do not recreate either predecessor contract inside P17.

Review status: Round 3 accepted the reviewed snapshot. A later idempotency-scope contradiction in one test sentence was corrected without a fourth round and was not re-reviewed, per the three-round cap.

## Problem

The feedback endpoint currently accepts the facts it is meant to measure from the browser. A caller can submit any job ID, query, model, retry relationship, result count, and response time; the endpoint performs no job lookup or owner check and persists those echoes as if they were authoritative. The resulting data cannot support trustworthy model, retry, latency, or outcome analysis.

Storage and analysis are also unsafe and mutually inconsistent. The process-local writer appends without durable flush or cross-process ownership, has only calendar-month naming, and has no record/segment/capacity/retention/idempotency contract. Its private serializer writes `FeedbackSentiment` as a number, while the Python analyzer expects lowercase strings. The analyzer uses a different default directory, loads all records into memory, interpolates raw query and comment text into an external-model prompt and Markdown report, and has no consent, schema-version, integrity, duplicate, or quarantine handling.

Repository evidence:

- `Models/QueryFeedback.cs` mixes a persisted event with its transport request. `SubmitFeedbackRequest` accepts `Query`, `ModelUsed`, `OriginalJobId`, `UserRequestedRetry`, `ResultCount`, and `ResponseTimeMs` from the client.
- `QueryController.SubmitFeedback` copies every client value and the current account name directly into `QueryFeedback`; it neither resolves the job nor verifies ownership or terminal state.
- The browser copies query, display-model, row-count, latency, retry intent, and lineage state into the feedback request. The retry flow writes `user_requested_retry=true` before it knows whether P14 accepted a retry, and a later comment can create a second negative record.
- `JsonLinesFeedbackStore` uses only an instance `SemaphoreSlim`, `File.AppendAllTextAsync`, a hard-coded `QueryLogHelper.OutputRoot` child, and monthly filenames. It has no `Flush(true)`, process/root lease, cancellation contract, size bound, segment integrity marker, recovery, retention, capacity, or replay index.
- The feedback serializer has no string-enum converter. Current records contain numeric sentiment, but `tools/analyze_feedback.py` compares against lowercase `positive` and `negative`; `tools/README.md` documents yet another shape (`user`, lowercase sentiment).
- `analyze_feedback.py` constructs an external API client even for local loading/statistics, materializes all records and comments, trusts arbitrary JSON fields, and sends raw queries/comments to the configured model with instructions to quote them.
- The analyzer defaults to `../csharp/wwwroot/metrics`, while the application writes `E:\WWWOutput\_system\feedback`.
- Current job metadata is mutable and in-memory, completed jobs are removed after 24 hours, and retry ancestry is encoded by modifying `Context`. P14 owns replacing those behaviors; P17 must not build another job registry.
- P02 owns the actual effective provider/model request contract, P12 owns the executable-plan fingerprint and compiler/policy versions, P13 owns sanitized terminal outcomes, and P07 owns result/artifact counts. P17 must snapshot those server facts, not recalculate or accept them.

## Goals

1. Accept only user feedback and explicit consent from the client; resolve every execution fact from P14's owner-authorized immutable private receipt.
2. Reject missing, foreign, nonterminal, expired, or ambiguous feedback targets without creating a record.
3. Define a checked-in immutable JSON event schema with a stable event ID, server timestamp, explicit provenance, and strict cross-field validation.
4. Keep raw user identity, query/context, plan, results, provider response, exception text, and physical paths out of feedback events.
5. Permit one bounded, structurally sanitized optional comment only after an explicit versioned local-storage consent.
6. Require a separate opt-in before any comment is included in external AI analysis; absence, ambiguity, or a legacy record means no external transfer.
7. Make submission logically exactly-once for a caller-supplied idempotency key and one immutable primary feedback event per job attempt.
8. Append bounded UTF-8 records durably through a single-writer, crash-recoverable, integrity-checked segmented store.
9. Bound record, segment, retained event, retained byte, free-space, rotation, quarantine, report, and retention behavior.
10. Make the analyzer a streaming tolerant reader with explicit adapters, deterministic de-duplication, safe quarantine diagnostics, and copy-on-write migration.
11. Make local statistics credential-free and make aggregate exports safe by construction.
12. Keep logging and metrics low-cardinality and free of comments, queries, identities, model IDs, event/job IDs, and paths.
13. Prove request authority, privacy, idempotency, durability, recovery, schema compatibility, bounded memory, and export safety with deterministic tests and mutation guards.

## Non-goals

- Do not implement P14 job persistence, transition arbitration, retry admission, lineage, or drain semantics.
- Do not define `DataRoot`, filesystem principals/ACLs, secret-source precedence, Serilog sinks, or general log retention; P16 owns them.
- Do not persist query result rows, raw plan/provider payloads, raw P13 exception text, or P07 artifact paths.
- Do not infer consent from a feedback click, a comment being present, a prior session, a default checkbox, or an old record.
- Do not promise semantic PII/secret removal from arbitrary free text. The structural sanitizer is not a data-loss-prevention system.
- Do not add an administrative raw-data export, a feedback browsing endpoint, a user-profile system, or an editable feedback record.
- Do not make external LLM analysis mandatory for validation, migration, statistics, or reporting.
- Do not silently reinterpret unknown schema versions, client metadata, old records, or corrupt segments.
- Do not use event IDs, hashes, or opaque job IDs as authorization tokens.
- Do not introduce a database, distributed queue, cloud storage, or multi-writer filesystem protocol.

## Dependency and ownership boundaries

### P01 — Verification

P01 supplies the C# test project and canonical verification entry point. P17 adds deterministic C# fixtures and a Python test stage to that one entry point; it does not create a parallel success command. Python tests must use the standard library plus the analyzer's pinned runtime dependencies, no network or credentials.

### P02 — Effective provider/model

P02 remains the owner of request construction and the effective provider/model selected for an attempt. P17 copies the bounded effective integration kind, effective model identifier, and fixed selection route from P14's immutable private receipt. It never derives model identity from browser display text, current configuration, a model-name heuristic, or a later retry. The analyzer's optional provider adapter likewise omits `temperature` and other sampling fields unless its own future versioned capability contract explicitly supports them.

### P07 — Result artifacts

P07 remains the owner of immutable result artifacts, authoritative row count, publication state, and artifact expiry. P17 may snapshot the authoritative row count and an opaque artifact-presence boolean from a terminal attempt; it stores no artifact path, preview, cell, result row, aggregation value, or export. Feedback authorization never opens an artifact merely to prove job ownership.

### P12 — Query-plan metadata

P12 remains the only producer of the executable-plan SHA-256 fingerprint, compiler schema version, and policy snapshot version. P17 stores those exact values when present. It never serializes a raw/executable plan, recomputes a fingerprint from a client plan, or treats a fingerprint as authentication.

### P13 — Outcome and HTTP failures

P13 remains the owner of terminal status meaning, failure code/category/retry disposition, safe problem details, and causal logging. P17 snapshots its immutable sanitized job outcome and registers only P17-owned failure codes through P13's central registry. It stores no rendered detail or exception/provider body.

### P14 — Authoritative job and retry lineage

P14 is a hard integration prerequisite and owns the exact internal authorization seam:

```text
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

P14 performs canonical-SID authorization and `feedback_expires_at_utc > now` in the same bounded SQLite read. Missing, foreign, or feedback-expired targets produce one indistinguishable `NotFound`; an owned active job produces `NotTerminal`; only an authorized receipt produces `Found`. The snapshot deliberately does not return `OwnerSubject`. `QueryUtf8` is a bounded owned zeroable buffer used only for the P17 HMAC and is disposed/cleared immediately; no mutable job, SQLite object, raw/compiled plan, context, owner, artifact path, or provider body crosses the seam.

The terminal state transaction creates the immutable private receipt, including P02's effective provider/model/route, P12's already-safe fingerprint/compiler/policy versions, P13's sanitized outcome, P07's authoritative publication/count, and server-measured duration. The receipt expires exactly 24 hours after terminal time, independently of P14's two-hour public job/artifact/idempotency window, and P14 charges it to dedicated global/per-owner capacity. P17 uses the returned `FeedbackExpiresAtUtc`; it does not extend or independently reconstruct that authority window.

In the same read transaction, P14 derives accepted-retry facts from bounded direct children only: records whose parent/predecessor equals the target. `AcceptedRetryCount` counts distinct direct children; latest means highest `AttemptNumber`, then canonical `JobId` as the deterministic unreachable-tie breaker. These facts do not mutate `TerminalJobVersion`. P17 stores this point-in-time server projection and never derives retry success from `Context`, `OriginalJobId`, a browser flag, or lineage-wide descendants.

P14 also owns retry idempotency. The browser retry flow first requests a retry through P14. Only after P14 returns an accepted/existing child does it submit feedback for the original attempt with its own P17 idempotency key, so the subsequent P14 read observes that direct child. Feedback failure does not roll back a retry, and a rejected/failed retry admission produces no accepted-child fact.

Once the private receipt expires, P17 returns the same public not-found result for new submissions and transport replays and never treats an idempotency key as authorization. P17's retained event remains self-contained for storage and analysis after receipt expiry; that does not keep the submission API open.

### P16 — DataRoot, secrets, ACLs, and logging

P16 is a hard storage prerequisite. Its process-lifetime DataRoot lease is the outer single-writer gate; `IDataPaths.FeedbackRoot` is the only physical root P17 receives. P16 also owns the application identity/ACL projection, validated configuration/secret binding, and the one application logging pipeline. P17 owns only the layout below `FeedbackRoot`, feedback quotas, its child writer lease as defense in depth, record/segment formats, feedback retention, and safe event semantics.

The one canonical active fingerprint pair is `Feedback:QueryFingerprint:ActiveKeyId` and secret `Feedback:QueryFingerprint:ActiveKey`. P16 registers/binds them through `ApplicationConfigurationCatalog` and exposes its internal `IFeedbackQueryFingerprintSecretSource.GetActive()`. The returned `FeedbackQueryFingerprintSecretSnapshot` is non-record, non-serializable, non-display, restart-stable, and contains the validated ID plus P16 `SecretValue`; P17 does not bind configuration or unwrap the secret anywhere except its narrow HMAC component. `ActiveKeyId` is required while feedback is enabled and must match ordinal-lowercase `^[a-z0-9][a-z0-9_-]{0,31}$`. `ActiveKey` is canonical base64 that decodes to exactly 32 random bytes. Missing pairs, malformed IDs/base64, or the wrong decoded length fail runtime startup without rendering either value. Package/default validation checks catalog shape and the absence of assigned secret/environment overrides; protected-promotion validation checks ID and secret-reference shape; runtime startup alone resolves/decodes the secret and verifies its length. None of those offline checks scans live feedback storage.

P17 owns `FeedbackQueryFingerprintKeyId`, the `hmac_sha256_utf8_v1` domain separator/canonical byte framing, HMAC-SHA-256 computation, and event/cohort meaning. A controlled restart activates a new pair. Old secret material is not retained: live submissions use only the active source, while startup, analyzer, export, migration of already-versioned events, and retention treat persisted `(query_fingerprint_key_id, query_fingerprint_sha256)` as an opaque cohort key and never verify or recompute it. Legacy-untrusted conversion drops the raw query and creates no fingerprint. Rotation intentionally splits equality cohorts; events are never re-hashed. Cross-generation rehashing would require a future approved migration and key-retention decision.

No P17 code references `QueryLogHelper.OutputRoot`, `E:\WWWOutput`, a release directory, current working directory, `wwwroot`, or `logs`. Feedback files and reports are data, not Serilog sinks. P17 emits safe structured log events into P16's pipeline and never creates another logger or file sink.

## Invariants

- The request carries only the target in the route, one idempotency key, sentiment, optional comment, and explicit consent choices.
- Every target fact is copied from one P14 private terminal receipt returned by its owner-authorizing read.
- A nonterminal or mutable snapshot can never be persisted as feedback.
- One job attempt has at most one logical `feedback_submitted` event in version 1.
- Replaying the same job-scoped idempotency key and normalized request within P14's authorization/submission window returns the original event ID and creates no logical duplicate.
- Reusing an idempotency key with different normalized feedback is a conflict.
- A different idempotency key for an attempt that already has feedback is a conflict, not an edit.
- Event IDs, receive times, schema versions, lineage, model, plan, result, duration, and outcome metadata are server-derived.
- Raw owner identity, query/context, plan, results, provider body, failure detail, and paths never enter the event.
- Local-storage consent is required and versioned. External-analysis consent is independent, false by default, and never inferred.
- Published events and sealed segments are immutable. Corrections require a future versioned event type, never a rewrite.
- An acknowledged new event has been written with its terminating LF and durably flushed.
- Exactly one application process owns the feedback writer/maintenance lease. A second writer fails readiness.
- Only complete integrity-checked sealed segments are analyzer input. The active segment is never analyzed.
- Corruption or an unknown retained event version prevents the application writer from accepting new events until reconciled; the offline analyzer quarantines and continues with supported valid segments.
- Retention deletes only sealed/recovery/quarantine/report targets selected under the feedback root. Failed deletion remains charged.
- Default exports are aggregate-only and suppress small cohorts. No export contains event/job/lineage IDs, hashes, comments, or free text.
- All loops, buffers, indexes, samples, groups, files, and outputs have finite configured or schema-fixed bounds.

## Submission and ownership contract

Replace the controller-injected store call with a narrow workflow such as `IFeedbackSubmissionService`. The endpoint shape is:

```http
POST /api/query/jobs/{jobId}/feedback
Idempotency-Key: 79b76fb0-3149-4b0e-a24d-fbf59761eeea
Content-Type: application/json

{
  "sentiment": "negative",
  "comment": "The result omitted contractors.",
  "consent": {
    "notice_version": "feedback-privacy-v1",
    "local_storage": true,
    "external_ai_analysis": false
  }
}
```

Transport rules:

- `jobId` and `Idempotency-Key` use canonical lower-case hyphenated UUID text and have exact length bounds. P14 may later expose a different opaque ID grammar; use its canonical parser, never a filesystem/path parser.
- `sentiment` is exactly `positive`, `negative`, or `neutral` in lower snake case.
- The DTO disallows unmapped JSON members. Stale/spoofed `query`, `model_used`, `original_job_id`, `user_requested_retry`, `result_count`, `response_time_ms`, timestamp, owner, schema, or event ID properties fail binding rather than being ignored.
- Consent fields are required booleans/strings, not nullable defaults. For a new event the client must echo the currently rendered notice version, while the server selects and records that same current version. A mismatch returns a fixed refresh-required conflict. An exact replay presents the original body and notice version and returns the existing event without re-consenting or adopting newer notice semantics.
- The controller obtains a canonical authenticated subject from P14/P16's identity seam. `unknown`, display name, domain-stripped account name, and a request field are invalid authorization identities.
- Resolve the target through `IQueryJobFeedbackTargetReader` before comment normalization, fingerprinting, or storage. P14 performs the indexed owner/expiry check in that read and does not return an owner value for P17 to compare. Missing, foreign, and expired targets return the same public not-found problem and write zero bytes.
- Accept every P14 terminal class so the contract can represent successful and failed experiences. The initial browser continues to expose feedback after completed results; P19 may later surface the same endpoint for sanitized failures without changing storage.
- Require P14's immutable terminal receipt and use its `TerminalJobVersion` and `FeedbackExpiresAtUtc` verbatim. Direct-child retry facts are a coherent point-in-time projection from the same P14 read; P17 never mixes them with a second public/job-store read.
- Honor request cancellation before durable-store admission. Once a bounded append begins, finish the write/flush/index transaction without the caller token; P13 may abandon a disconnected response, but the same request/key returns the committed event while P14 can still authenticate the retained target within the submission window.

The first successful response is `201 Created`; an exact replay is `200 OK`. Both return only:

```json
{
  "schema_version": 1,
  "event_id": "0190f5b5-6f2a-7b80-8ca4-5bb43a615105",
  "received_at_utc": "2026-07-21T20:00:00.0000000Z",
  "replayed": false
}
```

No response returns stored comment or execution metadata. The browser generates one idempotency key when the feedback interaction opens and retains it through transport retries. Negative feedback is submitted once, after the user chooses optional comment/external-analysis consent and after any requested P14 retry reaches accepted/existing or failed state. It is never submitted once before retry and again for a comment.

## Consent and privacy contract

Ship the notice as versioned static copy with an exact checked-in identifier. The UI must present, unchecked:

1. A required local-storage choice that states the sentiment, optional comment, server-derived operational metadata, retention period, and administrator access boundary.
2. A separate optional choice permitting the comment to be included in externally hosted AI analysis. It names the configured provider class without exposing routing credentials and says generated reports do not quote the comment.

Submitting while local consent is false creates no event. The external choice is false by default and has no effect on local statistics. Changing notice text, retention meaning, stored fields, or external-use meaning requires a new notice version; old consent never carries forward to a new submission.

Privacy-minimal storage is unconditional:

- Do not store Windows SID/account/display name or a stable user pseudonym.
- Do not store raw query, context, plan, result/preview/aggregation, prompt, model response, provider error, or rendered failure detail.
- Store a domain-separated, length-framed HMAC-SHA-256 of the exact P14-owned UTF-8 query plus the active nonsecret `query_fingerprint_key_id`. The secret comes only through P16's narrow source and never enters the event. Also store only a fixed query-length bucket.
- Store P12's already-safe plan fingerprint and versions; do not add plan shape/text unless a future schema and consent decision explicitly permits it.
- Store bounded server model metadata because model comparison is a declared analytic purpose. Never expose it as a metric tag or accept a display label.
- Store a comment only after local consent. External analysis filters again on the event's explicit external consent.

`hmac_sha256_utf8_v1` is exactly `HMAC-SHA-256(active_key, UTF8("adquery.feedback.query.v1") || 0x00 || UInt32BE(query_utf8_byte_count) || query_utf8_bytes)`, rendered as lowercase 64-hex. The key ID is stored beside, not inside, that message. Do not decode/re-encode, trim, normalize, or case-fold P14's query bytes. Derive `query_length_bucket` from that same byte count using the closed values `empty`, `1_64`, `65_256`, `257_1024`, `1025_4096`, and `4097_plus`; P14 remains the authority for its finite maximum query size.

The notice must tell users not to enter credentials, personal data, distinguished names, or confidential directory values. Structural validation cannot prove a comment contains none of those things. No code or documentation may claim the sanitizer anonymizes arbitrary text or makes external transmission risk-free.

## Comment normalization and bounds

Normalize before hashing/idempotency comparison and before storage:

1. Treat missing or all-whitespace input as no comment.
2. Convert CRLF and CR to LF, trim leading/trailing Unicode whitespace, and normalize to Unicode NFC.
3. Enumerate Unicode scalar values; reject invalid surrogate sequences.
4. Reject NUL, C0/C1 controls other than LF and horizontal tab, bidi override/isolate controls, and U+FEFF.
5. Permit at most 1,000 Unicode scalars, 4,096 UTF-8 bytes, 20 lines, and 256 UTF-8 bytes per line. Exactly at each bound succeeds; the next unit fails without truncation.
6. Serialize only through the JSON writer. Never concatenate comment text into logs, HTML, Markdown, CSV, filenames, paths, or command lines.

The client mirrors `maxlength` and line guidance for usability, but server checks are authoritative. Rejection never echoes the offending text. A future viewer must render comments as text, not HTML/Markdown.

## Immutable event schema

Add `contracts/feedback/feedback-event-v1.schema.json`, a golden canonical example, explicit C# JSON-property attributes, and a Python adapter fixture. `schema_version: 1` and `event_type: feedback_submitted` freeze field names, types, enum values, nullability, and cross-field rules; any semantic or wire change requires a new schema version or event type. Additive fields are not silently added to v1.

The v1 logical shape is:

```text
schema_version                 integer, exactly 1
event_type                     feedback_submitted
event_id                       server UUIDv7, canonical text
idempotency_key                canonical client UUID
request_digest_sha256          digest of normalized client-controlled fields
received_at_utc                server UTC RFC3339 with seven fractional digits

consent
  notice_version               bounded current server notice ID
  local_storage                exactly true
  external_ai_analysis         boolean, default forbidden at binding

feedback
  sentiment                    positive | negative | neutral
  comment                      nullable normalized bounded string

target
  job_id                       P14 immutable attempt ID
  job_version                  positive P14 TerminalJobVersion
  lineage_id                   P14 stable lineage ID
  attempt_number               positive bounded integer
  predecessor_job_id           nullable P14 ID
  retry_kind                   none | alternate_model | same_model | policy
  accepted_retry_count         nonnegative bounded direct-child count
  latest_accepted_child_job_id nullable direct P14 child ID
  latest_accepted_retry_kind   nullable alternate_model | same_model | policy
  query_received_at_utc        P14 server timestamp

query_metadata
  query_fingerprint_algorithm  hmac_sha256_utf8_v1
  query_fingerprint_key_id     bounded P16 nonsecret key ID
  query_fingerprint_sha256     lower-case 64-hex digest
  query_length_bucket          empty | 1_64 | 65_256 | 257_1024 | 1025_4096 | 4097_plus UTF-8 bytes

model_metadata
  effective_provider           nullable bounded fixed P02 integration enum
  effective_model_id           nullable bounded server identifier, maximum 256 UTF-8 bytes
  selection_route              nullable default | alternate | explicit_policy

plan_metadata
  plan_fingerprint             nullable P12 lower-case SHA-256
  compiler_schema_version      nullable bounded version
  policy_snapshot_version      nullable bounded version

outcome
  terminal_status              completed | failed | cancelled | interrupted
  failure_code                 nullable registered P13 code
  failure_category             nullable P13 category
  retry_disposition            nullable P13 enum
  result_row_count             nullable nonnegative P07 count
  artifact_published           boolean
  duration_ms                  nullable nonnegative bounded integer
  terminal_at_utc              P14 server timestamp
```

Before serialization or hashing, convert every timestamp, including all P14-sourced values, to UTC and render exactly `yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'`. Offset-preserving, variable-precision, local, and unspecified-kind representations are invalid at the event boundary. The JSON Schema regex and cross-language golden fixtures pin this form.

Cross-field rules are explicit and tested:

- `completed` requires no failure fields, `artifact_published=true`, and a result count. Other terminal states require the P13 outcome allowed by P14 and never claim a published success artifact.
- Plan metadata is all present or all absent. Absence is valid when failure occurred before compilation.
- Effective model/provider/selection route are all present or all absent. They may be absent only if P14 records a pre-provider terminal failure; represent that through explicit nullable schema fields rather than strings such as `unknown`.
- `attempt_number=1` requires no predecessor and `retry_kind=none`; later attempts require a predecessor and non-`none` retry kind.
- `accepted_retry_count=0` requires both latest-accepted fields to be null. A positive count requires both fields, a non-`none` retry kind, and P14 provenance as a direct child of this target; the schema validates the nullable shape while the P14 reader is authoritative for relationship and ordering.
- The event contains no JSON extension bag. Duplicate JSON property names are invalid.
- The exact UTF-8 event plus LF must not exceed `MaxRecordBytes`.

`request_digest_sha256` covers a version prefix, canonical idempotency key, sentiment, normalized comment bytes or explicit null marker, notice version, and both consent booleans. It does not cover server metadata. The digest detects conflicting replay; it is not authentication and never replaces the owner check.

## Event identity and idempotency

Generate `event_id` as UUIDv7 from injected `TimeProvider` plus an injected randomness source for a new event after authority/current-consent validation and before admission. Because P14 guarantees globally unique immutable job IDs, the recoverable indexes use only persisted bases: `job_id -> event` and `(job_id,idempotency_key) -> event`. An idempotency key is scoped to the target route/job; the same random key on a different globally unique job is allowed and has no cross-owner effect. Owner isolation is always the live P14 authorization check and is never encoded into an index or inferred from key possession.

Strict syntactic request parsing occurs first under the route/body limits. Every submission and replay must then obtain a still-authorized private receipt through P14's owner/window-checking reader before comment normalization, fingerprinting, or storage work. After bounded normalization/digest construction, acquire the writer lock and inspect the persisted indexes before applying the current notice rule. This ordering permits an exact lost-response replay under its original notice while P14 still authorizes that target, without treating an old notice as permission for a new event.

Within the one writer transaction:

1. If the job has an event with the same idempotency key and request digest, return the original event ID/receive time as a replay, even when that event's notice is no longer current.
2. If the job has an event with the same key but another digest, return `feedback_idempotency_conflict`.
3. If the job has an event under another key, return `feedback_already_submitted`.
4. If no event exists, require the current notice version/local consent, then append and durably flush the new event and publish both index entries before releasing the lock.

The persisted event is the recoverable idempotency ledger. Startup rebuilds both owner-free bounded indexes from verified retained segments plus the recovered active prefix before admission opens. A crash after durable append but before HTTP response therefore replays the original ID while P14 still authorizes the target. If retained supported data is corrupt or a future version is unknown, writer readiness fails rather than accepting a possible duplicate.

The feedback submission window is shorter than configured analytic event retention. Replays are guaranteed only while P14 retains and authorizes the target; after that, the API returns the same public not-found result even if the event still exists for analysis. Every event also has a conservative non-evictable idempotency horizon through `received_at_utc + SubmissionWindowHours`. Because receipt authorization can end no later than that horizon, time/capacity retention cannot remove the ledger while P14 could still authorize another submission. After the horizon, capacity retention may shorten analytic retention because P14 can no longer authorize recreation. Tests enforce these window and configuration relationships.

## Storage configuration and layout

P17 owns validated `FeedbackStoreOptions`; P16 supplies the catalog binding and `IDataPaths.FeedbackRoot`. The fingerprint pair is obtained only through P16's secret-source seam and is not copied into this ordinary options object. Recommended initial values are:

```json
"Feedback": {
  "Enabled": true,
  "ExternalAnalysisEnabled": false,
  "SubmissionWindowHours": 24,
  "RetentionDays": 90,
  "TornWriteRetentionDays": 7,
  "ReportRetentionDays": 30,
  "MaintenanceIntervalMinutes": 15,
  "MaxRecordBytes": 16384,
  "MaxSegmentBytes": 16777216,
  "MaxSegmentAgeHours": 24,
  "MaxRetainedEvents": 100000,
  "MaxRetainedBytes": 268435456,
  "MaxReportBytes": 10485760,
  "MinFreeDiskBytes": 1073741824,
  "MaxAnalyzerCohorts": 128,
  "MinAggregateCohortSize": 5,
  "MaxExternalComments": 100,
  "MaxExternalInputBytes": 262144
}
```

Schema version, event type, ID formats, consent notice version, and comment grammar are code/contracts, not mutable configuration. Validate positive values, checked conversions, record/segment/report relationships, `SubmissionWindowHours == 24` to match P14's enabled private receipt, `RetentionDays * 24 > SubmissionWindowHours`, event/byte bounds, minimum cohort at least five, and free-space headroom at startup. `MaxRetainedBytes` covers active, sealed, commit, recovery/quarantine, and migration data; reports have their own included sub-cap and remain part of the global total. Capacity exhaustion may reject new feedback but may never evict an event inside the conservative idempotency horizon.

Use only P16's resolved typed child path:

```text
<FeedbackRoot>/
  writer.lock
  active/<segment-id>/events.jsonl
  segments/<segment-id>/events.jsonl
  segments/<segment-id>/commit.json
  quarantine/torn/<recovery-id>/tail.bin
  quarantine/torn/<recovery-id>/commit.json
  quarantine/reports/<run-id>.json
  migrations/<run-id>/...
  reports/<run-id>/...
```

Segment/run IDs are generated and validated opaque identifiers, never caller text. P16 path containment and reparse-point policy applies to every create, move, open, and delete. The application identity receives only the P16-approved rights. The web server never serves this tree as static content.

## Durable append, concurrency, and rotation

Register one singleton store only after P16 has acquired its outer process-lifetime DataRoot lease and supplied `FeedbackRoot`. The store then opens `writer.lock` with `FileShare.None` and holds the handle for process lifetime as P17 child-level defense in depth. Outer-lease failure uses P16's fixed readiness reason; child-lease failure maps to `feedback_store_in_use`. Do not fall back to another root or a process-local second writer. IIS overlap remains unavailable until P15's drain/stop lifecycle hands outer ownership to the new process.

Use one process-local async lock around index check, capacity admission, append, rotation, and index publication. Resolve job metadata, normalize/comment-bound, compute fingerprints/digests, and serialize the complete event to one bounded UTF-8 buffer before acquiring it. No network/provider/LDAP call or unbounded serialization occurs inside the lock.

For a new event:

1. Honor caller cancellation before admission.
2. Recheck idempotency and one-event-per-job under the lock, and reserve the exact bounded in-memory index capacity/entries before any durable write.
3. Run retention if the full record reservation would exceed event/byte/free-space limits; reject if confirmed deletion cannot admit it.
4. Rotate first if adding the exact buffer would exceed `MaxSegmentBytes` or the active age has reached `MaxSegmentAgeHours`. Exactly at the byte limit succeeds.
5. Open the active file for append with readers excluded from `active`, write the exact JSON-plus-LF buffer, await completion, and call `Flush(flushToDisk: true)` before acknowledgment.
6. Publish the already-reserved immutable index entries. Cancellation/disconnect after step 4 does not interrupt steps 5–6. If an unexpected publication failure occurs after flush, keep the store unavailable and rebuild from the durable active prefix before releasing admission; never continue with a ledger/index mismatch.
7. Release the lock and return. Logging occurs after the transaction and contains no comment/model/query/identity.

A `TimeProvider`-driven hosted maintenance task rotates an aged nonempty active segment even when no new event arrives, applies retention, and seals on graceful shutdown. It has one bounded iteration and shares the same lock; no overlapping timer callback is allowed.

Sealing is staged and same-volume:

1. Flush and close `active/<id>/events.jsonl`.
2. Finalize the incrementally maintained SHA-256, exact byte count, record count, first/last server receive timestamps, segment format version, and ID.
3. Write bounded `commit.json` through a create-new temporary file, flush it durably, and atomically rename it to its final name.
4. Atomically move the active directory to `segments/<id>`; analyzers recognize only final directories containing a valid commit marker.
5. Create a new active directory with a new ID only when the next record is admitted.

The commit hash detects accidental corruption, not malicious rewrite by an actor with root write access. A sealed segment is never appended, repaired, or renamed for normal operation.

## Startup recovery

Recovery runs while holding the writer lease and before readiness/admission:

- Validate every sealed commit size/schema/ID, exact data length/hash, event schema, event ID uniqueness, one-event-per-job rule, and idempotency mapping. Unsupported future versions or any supported-record contradiction fail writer readiness with a fixed safe reason.
- Rebuild exact retained byte/event counts and both idempotency indexes. Do not trust file timestamps, directory names, or a cached sidecar index.
- If an active file ends in LF and every record is valid, seal it normally. This covers a crash after append/flush or during rotation.
- If it has a nonempty unterminated tail, first copy only that tail into a create-new restricted quarantine item, write/flush its digest/length/reason commit, then truncate the active file to the last LF and flush. Seal the valid prefix. Recovery is idempotent by source segment ID, tail offset, and digest.
- If a complete active line is malformed, duplicate, unsupported, or conflicts with prior state, move the whole active directory to restricted quarantine and fail writer readiness. Do not discard valid-looking prefix records while accepting new writes under an uncertain ledger.
- Final segment directories missing/invalid commit markers are quarantined and charged; they are never analyzed or treated as events.
- A failed quarantine, truncate, move, hash, or delete remains charged and fails closed when ledger integrity is uncertain.

Fault-injection tests cover every boundary before/after event flush, commit flush/rename, directory move, index publication, quarantine commit, and active truncation. No recovery test relies on process kill timing or wall-clock sleep.

## Retention and capacity

Retention uses server `received_at_utc` from verified events and commit bounds, not filesystem creation time. It never extends on read, analysis, replay, or report generation.

Under the writer lock:

1. Seal an active segment that must be considered for capacity.
2. Remove expired reports and completed migration staging under their specific retention.
3. Remove committed torn-tail quarantine after its shorter retention.
4. Remove sealed event segments whose newest event is older than `RetentionDays`.
5. If event count/bytes still exceed the approved caps, consider oldest sealed segments only when every contained event is past `received_at_utc + SubmissionWindowHours`; remove eligible segments until the next maximum record reservation fits and record that capacity shortened analytic retention. A segment containing any event within that safety horizon is not eligible.
6. Never delete the active segment, an uncommitted recovery item, a migration source, or any path not proven to be an exact P16-contained P17 object.

Delete by first atomically moving the exact item to a P17 trash name, then deleting it. Decrement retained counts and remove index entries only after confirmed deletion; on failure, restore/retain the charged tombstone and retry later. An analyzer racing enumeration may miss a just-retired segment but cannot read a partially rewritten one. The analyzer reports the immutable segment snapshot it actually consumed.

When count/byte/free-space admission remains unavailable, reject before writing with `feedback_store_full`. The owner-approved byte/event cap may shorten analytic retention only through the eligible oldest-first rule above; it never overrides the idempotency horizon. Metrics distinguish time expiry, capacity expiry, quarantine expiry, and deletion failure.

## Analyzer architecture

Refactor `tools/analyze_feedback.py` into small importable modules while retaining one documented CLI:

```text
tools/adquery_feedback/contracts.py      immutable normalized records/adapters
tools/adquery_feedback/reader.py         bounded sealed-segment reader
tools/adquery_feedback/aggregate.py      bounded streaming statistics
tools/adquery_feedback/external.py       optional provider adapter/payload builder
tools/adquery_feedback/report.py         aggregate-only report writers
tools/adquery_feedback/migrate.py        copy-on-write migrations
tools/analyze_feedback.py                argument parsing/exit mapping only
```

Local commands do not load provider configuration or construct an API client:

```text
analyze_feedback.py validate --feedback-root <resolved-root>
analyze_feedback.py stats --feedback-root <resolved-root> [--since <RFC3339>]
analyze_feedback.py report --feedback-root <resolved-root> [--since ...]
analyze_feedback.py analyze --feedback-root <resolved-root> --external-ai
analyze_feedback.py migrate --legacy-root <old-root> --feedback-root <resolved-root>
```

The CLI requires an explicit feedback root or a nonsecret P16-projected environment reference; it has no stale `wwwroot/metrics` default. Provider keys come only from the P16-approved environment/secret source, never `tools/config.json`, command-line arguments, reports, or checked-in examples. `validate`, `stats`, `report`, and `migrate` succeed without `anthropic` credentials or network. `analyze` is the only command that imports/constructs the optional client.

The reader:

- Enumerates a snapshot of committed sealed segment directories in ordinal segment-ID order and ignores `active`.
- Bounds commit bytes, event bytes, record count, total input, group count, duplicate index, and diagnostic count before allocation.
- Verifies commit schema, path/ID agreement, exact length, and SHA-256 while streaming. It merges a segment's aggregates only after integrity succeeds.
- Decodes strict UTF-8, requires LF-terminated records, rejects duplicate JSON properties, and applies the adapter named by exact schema/event type.
- De-duplicates repeated identical `event_id` records logically. The same ID with different bytes is corruption and quarantines the affected segment.
- Orders final time-dependent results by `(received_at_utc,event_id)`, never filesystem enumeration order.
- Parses RFC3339 as offset-aware UTC. `--since` is UTC and exact at the boundary.
- Uses bounded counters, at most `MaxAnalyzerCohorts` groups with deterministic overflow into `other`, at most `MaxExternalComments` sampled comments, and no list of every event.

### Tolerant compatibility and quarantine

The application writer is strict because it owns idempotency. The offline reader is tolerant: one unsupported/corrupt segment or invalid record does not erase analysis of other verified segments.

Each excluded item creates only a bounded diagnostic:

```text
quarantine_id      SHA-256 of segment ID, byte offset, raw-byte digest, and reason
segment_id         opaque bounded ID
byte_offset        nonnegative integer
byte_length        bounded integer
raw_sha256         digest only
reason             fixed enum
observed_version   nullable bounded scalar
```

Never place raw event bytes, comment, query, identity, model ID, job/event ID, JSON text, exception message, or physical path in console output or a quarantine report. Original immutable/quarantined source bytes remain under P16 ACLs for the retention period. Write a run-specific bounded quarantine report atomically under `quarantine/reports`; `--no-write-report` still returns counts and a nonzero partial-data exit code.

Fixed reasons include invalid commit, hash/length mismatch, invalid UTF-8, oversized line, unterminated sealed line, malformed/duplicate-key JSON, unsupported schema/event type, schema violation, duplicate event conflict, and legacy field conflict. Unknown future versions are quarantined as unsupported, never parsed as the newest known version.

Analyzer exit classes are stable: success/no quarantine, success-with-exclusions, invalid invocation/configuration, and no usable records. External-provider failure cannot change or overwrite the local validation/statistics report.

## Schema compatibility and migration

Keep an explicit adapter registry. The analyzer supports every event schema that can remain within approved retention. A writer version refuses startup if retained future events exist; an analyzer skips them with quarantine. Removing an adapter requires proof that no corresponding segment can remain and a versioned documentation change.

The unversioned legacy adapter recognizes only the two evidenced shapes:

- Actual C# snake-case properties including `user_name` and numeric `sentiment` values `0`, `1`, `2`.
- The README example using `user` and lowercase sentiment strings.

It rejects duplicate/conflicting aliases and out-of-range values. Legacy job/query/model/result/retry fields are client-authoritative, and legacy query/comment/user data has no recorded consent. The adapter labels every record `legacy_untrusted`, excludes it from authoritative model/retry/query/outcome cohorts and all external analysis, and may count only a separately labelled sentiment total. It never silently upgrades those fields to v1 authority.

Migration is offline, explicit, copy-on-write, and rerunnable. Because this application has not been used in production, first perform a bounded operator inventory of an explicitly named legacy source; when no source data exists, record `no_legacy_feedback` and skip migration. Never auto-discover, auto-copy, or infer content from the historical hard-coded drive.

1. Stop the application or otherwise prove exclusive access to the explicit legacy input; never scan an inferred drive/root.
2. Read the source without modification under finite file/line/record/byte bounds.
3. Write a new migration staging directory beneath the explicitly supplied, P16-validated `FeedbackRoot/migrations` using the current segment publisher.
4. Compute a deterministic `migration_id` from the ordered source inventory digests, migration tool/schema version, and normalized migration options. Sort sources by normalized relative path only to assign a stable ordinal; persist the ordinal and source digest, never the path.
5. Convert usable legacy rows to a distinct `legacy_feedback_imported` event type with `legacy_untrusted` provenance, no identity/query/comment/model/retry/result/outcome fields, `external_ai_analysis=false`, and a source-record digest. Its event ID is UUIDv5 in a fixed P17 migration namespace over `(migration_id,source_ordinal,record_ordinal,raw_record_sha256)`, its timestamp is the canonicalized valid legacy timestamp, and destination segment IDs derive from `(migration_id,segment_ordinal)`. These deterministic migration IDs are explicitly distinct from live UUIDv7 IDs. Do not fabricate P14 ownership or consent.
6. Write a durable canonical manifest with source ordinals/digests, accepted/rejected counts, dropped-field categories, destination segment digests, tool/schema/options versions, and no raw values/paths or wall-clock generation field.
7. Re-read and verify destination counts/hashes through the normal analyzer. The same unchanged source inventory, tool/schema version, and options must produce byte-identical events, segments, and manifest on every rerun.
8. Require a separate explicit operator cutover/legacy-destruction action after review. The migration command never deletes or overwrites the source.

Future breaking migrations follow the same pattern. Do not rewrite sealed records in place, preserve event IDs across authoritative schema migrations, and record old/new schema plus hashes/counts in a bounded manifest. A semantic transformation that cannot preserve meaning must quarantine or emit an explicitly lossy provenance event, never guess.

## Safe statistics, external analysis, and exports

`stats` and `report` consume normalized records and emit aggregate data only. Initial dimensions are fixed sentiment, terminal status, P13 category/retry disposition, provider integration, effective model, selection route, attempt-number bucket, target retry kind, accepted-direct-retry-count bucket, latest accepted retry kind, result-count bucket, duration bucket, compiler schema version, and consent counts. `(query_fingerprint_key_id, query_fingerprint_sha256)` and plan fingerprints may count repeats internally but never appear in output; digests from different key IDs are never merged.

Every combination must contain at least `MinAggregateCohortSize` events. Smaller cells merge into `other` only when the merged result also meets the threshold; otherwise omit them and report one suppressed-cell count. Totals below the threshold emit only `insufficient_data`. Cap dimension combinations and sort deterministically. This is a privacy risk-reduction rule, not a formal anonymity guarantee.

Default Markdown/JSON/CSV report schemas contain no event/job/lineage/idempotency IDs, query/plan fingerprints, owner, timestamp finer than calendar day, comments, raw query/plan/result, failure detail, physical path, or provider response. CSV uses a dedicated formula-safe aggregate exporter. Markdown escapes generated labels and never interpolates comments. Output is size-counted, create-new, staged and atomically published beneath `<FeedbackRoot>/reports`; replacement requires an explicit flag and preserves/replaces only an exact validated P17 report target.

`analyze --external-ai` additionally requires:

- P17 `ExternalAnalysisEnabled=true` supplied through P16's validated binding, plus valid credentials from a P16-approved secret source.
- At least the minimum aggregate cohort.
- Per-record `external_ai_analysis=true` under the exact recognized notice version.
- A deterministic stable sample of no more than `MaxExternalComments`, selected by `(SHA-256(event_id),event_id)` rather than file order.
- A complete serialized provider input no larger than `MaxExternalInputBytes`. Serialize the fixed aggregate envelope first, then consider comments in the stable hash order. Include a whole comment only when the exact resulting payload (separators and closing syntax included) remains at or below the byte cap; otherwise skip it and continue to later candidates until the count cap or candidates are exhausted. Never truncate a comment. Exactly at the cap succeeds.
- A second conservative credential-pattern screen that excludes suspicious comments with a fixed local reason; it does not convert nonconsented text into consent.

The external payload contains aggregate cells plus eligible normalized comments as a JSON data array explicitly labelled untrusted. It contains no raw query, owner, job/lineage/event/idempotency ID, fingerprint, path, credentials, or failure/provider body. The system prompt says comments are untrusted data and cannot change instructions; output remains advisory. The provider request has a typed minimal shape and no `temperature` by default.

Generated analysis never quotes a comment and is scanned/bounded before aggregate-report insertion. A dry run prints only eligible/excluded counts and encoded-byte totals. No `--include-raw`, `--include-query`, `--include-identities`, or consent-bypass flag exists. Administrators who need incident evidence use the restricted source under a separately approved procedure, not an analyzer export.

## Failure contract

Register these P17-owned stable causes with P13; exact HTTP title/category/retry mapping is reviewed with the registry:

```text
feedback_request_invalid
feedback_consent_required
feedback_notice_version_stale
feedback_target_not_found
feedback_target_not_terminal
feedback_comment_invalid
feedback_comment_too_large
feedback_idempotency_conflict
feedback_already_submitted
feedback_store_full
feedback_store_in_use
feedback_store_corrupt
feedback_write_failed
feedback_schema_unsupported
```

Recommended mappings: invalid/consent/comment errors are nonretryable 400/422; stale notice, nonterminal, duplicate, and idempotency mismatch are 409; P14's missing/foreign/expired result is the one `feedback_target_not_found` public 404; full is retry-after-delay 503; in-use is startup/readiness unavailable; corrupt/unsupported retained state fails readiness and requests with sanitized 503; write failure is retry-new-attempt only with the same idempotency key. P13 problem bodies never include comment, IDs other than its ordinary request correlation, storage path, or exception text.

Analyzer failures use local fixed exit classes and do not reuse public HTTP exceptions. A partial-data report states counts/reasons only and never claims completeness.

## Logging and telemetry

P17 defines safe events; P16 owns sinks, formatting, redaction, and log retention. Submission logs may include P13 request correlation plus fixed outcome/reason, schema version, sentiment, comment-present boolean, external-consent boolean, and replay boolean. Do not log owner, comment/length, query/fingerprint, plan fingerprint, model/provider identifier, job/lineage/event/idempotency ID, path/filename, JSON, or exception message. Storage exceptions remain local causal captures under P13 and render only fixed codes.

Use the existing application meter:

```text
adquery.feedback.submission
adquery.feedback.append_duration
adquery.feedback.record_bytes
adquery.feedback.store_events
adquery.feedback.store_bytes
adquery.feedback.rotation
adquery.feedback.retention
adquery.feedback.recovery
adquery.feedback.analyzer_records
adquery.feedback.analyzer_quarantine
adquery.feedback.external_analysis
adquery.feedback.report_bytes
```

Allowed tags are fixed outcome/reason, known schema/event type, sentiment, replay boolean, consent boolean, terminal-status/category/retry enum, rotation/recovery action, and size/duration bucket. Schema tags come from the finite adapter registry. Never tag any ID, timestamp, model/provider name, fingerprint, query/comment, path, exception, file, or arbitrary JSON value.

## Deterministic verification

All C# tests use temporary directories, injected `TimeProvider`, deterministic ID/randomness sources, a fake P14 feedback-target reader, fake P16 `IDataPaths`/fingerprint-secret sources, a recording durable-file adapter, and fault injection. Python tests use checked-in tiny fixtures and monkeypatched provider adapters. No live IIS, AD, provider, credentials, machine path, random delay, sleep, or wall clock.

### Authority, API, and privacy

1. Every forbidden client metadata property is rejected and can never affect a stored event.
2. Missing, foreign, and expired targets return the same public not-found result; owned nonterminal targets return the fixed not-terminal result; all write zero bytes and P17 never receives an owner value.
3. The P14 receipt alone controls job/lineage/direct-retry/query/model/plan/outcome/result/duration fields. A committed direct retry is present when its transaction precedes the receipt read and absent when it follows; both projections are internally coherent.
4. Raw identity/query/context/plan/result/provider body/failure detail and sentinel secrets are absent from event JSON, response, logs, metrics, reports, and external payload.
5. Missing/false local consent and stale notice version write nothing; external consent defaults false and never affects local acceptance.
6. Comment normalization has exact scalar/UTF-8/line/per-line boundaries, rejects invalid/control/bidi input, and never truncates.
7. All terminal-state cross-field combinations have table-driven valid/invalid fixtures.
8. The golden event validates against the checked-in JSON Schema and serializes identically in C# and Python adapters; accepted-retry zero/nonzero nullable shapes and every fixed query-byte bucket have valid/invalid fixtures.

### Idempotency and ownership races

9. First submission is created; exact sequential and concurrent replays return the same ID/time and one logical/physical line.
10. For the same job, reusing a key with changed sentiment/comment/consent conflicts and using another key after feedback exists conflicts; the same key on a different owner-authorized job is an independent allowed submission.
11. A crash/fault after durable append and before response/index publication recovers and replays the original ID.
12. P14's immutable terminal version and same-read direct-child projection are copied once; no public snapshot, browser retry flag, or second mutable store read can produce mixed metadata.
13. Caller disconnect before admission writes nothing; disconnect after commit starts finishes one durable event and the retry replays it.

### Storage, rotation, recovery, and retention

14. Exactly-at record/segment/store/event/free-space boundaries succeeds; one unit over rejects before partial write.
15. The recording adapter proves JSON-plus-LF write precedes `Flush(true)` and acknowledgment/index publication follows it.
16. Concurrent writers serialize; missing/failed P16 outer ownership or a competing P17 child writer lease fails readiness without choosing another root.
17. Age and size rotation publish only valid commit+data pairs, preserve hashes/counts, and never expose active data.
18. Every injected crash boundary reaches exactly the specified recovered sealed, active, quarantined, or unavailable state on repeated startup.
19. Unterminated tails are durably quarantined before truncation; valid prefixes retain event IDs and idempotency mappings.
20. Mid-file malformed/conflicting/unknown retained data fails writer readiness; analyzer excludes it and continues with other valid segments.
21. Time and capacity retention select deterministic oldest sealed targets, never active/out-of-root paths, and decrement counts only after confirmed deletion. A segment with any event inside `received_at_utc + SubmissionWindowHours` is ineligible even at the byte/event cap.
22. Failed deletion and safety-horizon data remain charged; a full store rejects without unbounded growth or loss of replay ledger state.
23. Recovery and analyzer reject traversal, reparse, manifest/data ID mismatch, oversized commit/line, duplicate JSON keys, hash mismatch, and invalid UTF-8.

### Analyzer, compatibility, migration, and exports

24. Local commands never construct/import the external client, read credentials, or contact a network fake.
25. Actual numeric-sentiment legacy fixtures and README-string fixtures parse only as `legacy_untrusted`; conflicting aliases quarantine.
26. Legacy identity/query/comment/model/retry/result fields never enter authoritative cohorts, external input, or migrated output.
27. Unknown future versions and corrupt segments produce bounded digest-only quarantine diagnostics while valid supported segments still aggregate.
28. Identical duplicates count once; conflicting duplicate event IDs quarantine. File enumeration order cannot change output.
29. Streaming a generated maximum-count fixture keeps retained normalized records/samples/groups within configured bounds and never builds an all-events list.
30. Cohorts below five are suppressed/merged deterministically; aggregate JSON/CSV/Markdown contain none of the forbidden raw fields or formula/Markdown injection.
31. Only explicitly externally consented comments pass the stable sample; count/byte caps and the credential screen exclude deterministically.
32. The captured provider payload contains no forbidden fields and no `temperature`; provider output/failure cannot overwrite the local report.
33. Migration leaves source bytes unchanged, is rerunnable to identical semantic output/manifests, verifies destination hashes/counts, and requires separate cutover/delete.
34. Report size, create-new publication, replacement containment, retention, and partial-data exit classes are exact and deterministic.
35. P16 startup rejects missing/mismatched/invalid active fingerprint pairs; same bytes/key produce the golden HMAC, another active key ID/key splits the cohort, and old events remain analyzable after old key material is unavailable.

### Performance evidence

Record a Release baseline for 100,000 minimal events spread across maximum-size committed segments. Measure startup index rebuild, append p50/p95 after warm-up, streaming validation/statistics throughput, peak managed/Python memory, and report size. This is evidence, not a machine-independent timing gate. Structural gates enforce one record buffer, capped indexes/groups/samples, no all-event list, and no provider call under the writer lock.

## Red/green guard proof

For every test-bearing slice:

1. Add the focused failing guard against current behavior or a deliberately incomplete new contract.
2. Run it and record the expected failure.
3. Implement the smallest slice.
4. Temporarily restore/bypass only the protected behavior without rewriting history.
5. Confirm the guard fails for the intended reason.
6. Restore the implementation and run the focused suite plus P01's canonical verifier.
7. Commit that one restored slice before starting the next.

Mandatory mutations:

- Copy one client `model_used`, `result_count`, or `user_requested_retry` into the event; authority guard fails.
- Bypass P14's owner-authorizing feedback reader, use a public lookup, or accept `unknown`; zero-write authorization guard fails.
- Default either consent boolean to true; consent guard fails.
- Add raw query/user/comment to a safe report or nonconsented comment to external input; forbidden-sentinel guard fails.
- Change one v1 property/enum/nullability without a schema bump; cross-language golden/schema guard fails.
- Acknowledge before `Flush(true)` or cancel mid-append; durability-order guard fails.
- Bypass the required P16 outer lease/root seam or remove the P17 child writer lock; concurrency guard fails.
- Retain/retrieve an old fingerprint secret, merge digests across key IDs, or alter the HMAC framing; secret-lifecycle/golden-cohort guard fails.
- Accept a conflicting idempotency replay or second event for one attempt; exactly-once guard fails.
- Analyze `active`, accept a bad commit hash, or reinterpret an unknown version; integrity/quarantine guard fails.
- Delete before confirmed quarantine/retention selection or decrement on failed deletion; recovery/capacity guard fails.
- Evict an event inside its idempotency horizon to admit a new one; replay-ledger guard fails.
- Materialize every normalized event or exceed a group/sample cap; bounded-streaming guard fails.
- Emit a cohort of four or interpolate a comment into Markdown; safe-export guard fails.
- Send `temperature` or create a provider client during `stats`; captured-client guard fails.
- Migrate legacy client metadata as authoritative or infer old consent; legacy-provenance guard fails.

Leave no mutation or generated report/segment in the worktree.

## Implementation slices

Each slice is one commit, passes focused tests plus the current P01 canonical verification before the next, and does not absorb another queued finding.

### Slice 1 — Freeze feedback contracts

Commit intent: `feat: define versioned feedback contracts`

- Add v1 JSON Schema, golden fixtures, strict transport DTO, immutable event/receipt projection types including direct accepted-retry fields, comment normalizer, consent/version rules, deterministic serializer, event/digest factories, and `FeedbackStoreOptions` validation.
- Add schema parity, comment boundary, cross-field, privacy-sentinel, and strict-unmapped-member tests.
- Do not wire the endpoint or filesystem yet.

### Slice 2 — Resolve authoritative feedback targets

Commit intent: `feat: authorize feedback targets`

- Consume P14's `IQueryJobFeedbackTargetReader`, dispose/clear `QueryUtf8`, and map the exact immutable receipt plus same-read direct-child projection without persisting owner/query.
- Consume P16's `IFeedbackQueryFingerprintSecretSource`; derive the exact domain-framed HMAC and fixed byte-length bucket without rebinding or retaining secrets.
- Add missing/foreign/nonterminal/window, direct-child ordering, retry-first, active-pair validation/rotation, HMAC-golden, and metadata-spoof tests.
- Stop if landed P14/P16 semantics differ from the recorded seam; reconcile plans instead of adding an adapter with broader authority.

### Slice 3 — Publish durable feedback segments

Commit intent: `feat: durably append feedback events`

- Add the `IDataPaths.FeedbackRoot` layout after P16's outer lease, a defense-in-depth P17 process-lifetime child writer lease, singleton store, bounded record buffer, durable append ordering, segment commit/hash, size/age rotation, and graceful sealing.
- Add exact-boundary, lease, append-order, hash, active-invisibility, rotation, and cancellation tests.

### Slice 4 — Recover and enforce idempotency

Commit intent: `feat: recover idempotent feedback writes`

- Add startup validation/index rebuild, one-event-per-attempt and idempotency conflict/replay behavior, active-prefix/torn-tail recovery, restricted quarantine, and fail-closed future/corrupt writer state.
- Add concurrency, duplicate, crash-point, repeated-recovery, and unknown-version tests.

### Slice 5 — Bound retention and capacity

Commit intent: `feat: bound feedback retention`

- Add event/byte/free-space admission, maintenance scheduling, deterministic time/capacity retention, delete accounting/tombstones, and recovery/report/migration charges.
- Add fake-time, exact-capacity, failed-delete, containment, and no-unexpired-eviction tests.

### Slice 6 — Replace the feedback API and browser flow

Commit intent: `refactor: submit trustworthy feedback`

- Replace `QueryFeedback`/`SubmitFeedbackRequest` and controller store injection with the strict route/workflow and P13 problems.
- Add versioned unchecked consent UI, one idempotency key per interaction, one negative submission, and retry-first P14 sequencing so accepted-child facts are resolved only after retry admission returns.
- Remove browser-held query/model/result/time/original-job feedback metadata and legacy duplicate submission paths.
- Add endpoint/browser contract tests with zero-write failure assertions.

### Slice 7 — Add the tolerant streaming analyzer

Commit intent: `refactor: stream versioned feedback analysis`

- Split the Python modules/CLI, add committed-segment integrity verification, strict JSON/adapters, bounded aggregation/de-duplication, legacy-untrusted compatibility, quarantine diagnostics, and stable exits.
- Make local commands credential/network independent and remove the stale default directory/config behavior.
- Add Python fixtures for supported, legacy, malformed, duplicate, future, corrupt, order, and maximum-bound cases.

### Slice 8 — Add safe reports and optional external analysis

Commit intent: `feat: export privacy-bounded feedback insights`

- Add minimum-cohort aggregate schemas, bounded atomic JSON/CSV/Markdown reports, deterministic consent-filtered sampling, secret-pattern exclusion, minimal typed external request, dry run, and provider isolation.
- Remove raw query/comment report sections and instructions to quote data.
- Add suppression, formula/Markdown, payload, no-temperature, output-size/path, and provider-failure tests.

### Slice 9 — Add copy-on-write migration

Commit intent: `feat: migrate legacy feedback safely`

- Add explicit no-auto-discovery legacy inventory/read bounds, a no-data skip result, `legacy_feedback_imported`, source/destination manifests, copy-on-write staging below `FeedbackRoot`, and separate cutover/delete guidance.
- Add numeric/string legacy, conflict, consent/provenance, idempotent rerun, source-unchanged, and failure-injection tests.

### Slice 10 — Integrate verification, telemetry, and guidance

Commit intent: `docs: establish feedback data operations`

- Add Python tests to P01's canonical verifier, low-cardinality metrics/safe P16 logging events, Release performance harness, and operator docs for consent versions, roots, validation, partial exits, retention, migration, external analysis, and recovery.
- Update the root/C#/tools READMEs to one canonical path/schema and remove credential-bearing/stale examples.
- Record one baseline and verify documentation contains no raw-export/consent-bypass command.

Verification after every slice:

```powershell
pwsh -NoLogo -NoProfile -File scripts/verify.ps1
```

Until P01 lands, use the repository's recorded build command plus focused test commands introduced by the slice, but P17 cannot be declared complete until the canonical verifier includes and passes both C# and Python suites.

## Acceptance criteria

- The route/request cannot carry query, model, retry, result, duration, owner, timestamp, schema, or event metadata.
- Every event comes from P14's owner-authorized private terminal receipt; its same-read direct-child retry facts are authoritative, and the event survives the receipt's later expiry.
- P02/P07/P12/P13/P14 facts retain exact provenance; none is reconstructed from display text, current config, prose, or mutable state.
- No raw identity/query/context/plan/result/provider body/failure detail/path enters a feedback event.
- Consent UI is explicit/versioned/unchecked; local false stores nothing and external false sends nothing externally.
- Optional comments meet exact structural bounds and are never logged or quoted/exported.
- V1 schema, event IDs, field semantics, enum spellings, and cross-field rules are frozen and cross-language tested.
- One attempt produces at most one logical event; exact replays survive response loss/restart and conflicts never append.
- Acknowledgment follows JSON-plus-LF durable flush and index publication.
- P16's outer DataRoot ownership and P17's child writer lease are both required; contention fails readiness and no root/multi-writer fallback exists.
- Rotation publishes only integrity-checked immutable segments; analyzers never read active data.
- Recovery preserves valid acknowledged prefixes, quarantines torn bytes before truncation, and fails closed on uncertain ledger integrity.
- Record, segment, event, byte, report, sample, group, input, retention, and free-space bounds are finite and exact-boundary tested.
- Retention deletes only verified contained targets and does not release accounting on failure.
- The analyzer streams, validates exact versions/integrity, de-duplicates, segregates legacy data, and produces digest-only quarantine diagnostics while retaining supported valid aggregates.
- Migration is copy-on-write, consent/provenance safe, source-preserving, hash/count verified, and never performs implicit cutover/deletion.
- Local statistics need no provider package initialization, credentials, or network.
- Reports are aggregate-only, minimum-cohort, bounded, formula/Markdown safe, and contain no raw identifiers/text/hashes.
- External analysis receives only explicitly consented bounded comments and aggregates, never forbidden metadata or default sampling parameters.
- Logs and metric tags obey the fixed safe/low-cardinality lists and flow through P16 ownership.
- Deterministic tests, every recorded mutation proof, Python stage, performance evidence, and P01 canonical verification pass.
- Each slice is committed separately and no P14/P16 authorization, root, lease, configuration, secret-source, ACL, or logging ownership is duplicated.

## Rollback

Use new revert commits; do not rewrite history.

- Revert consumers before contracts/stores they consume.
- Disabling feedback is the safe request-path rollback if durable authority/idempotency/storage is uncertain; do not restore the client-authoritative endpoint.
- Keep privacy redaction, strict consent, comment bounds, and no-raw-export rules even if external analysis or reports roll back.
- External analysis can be disabled independently while local aggregate statistics remain.
- Reports/analyzer adapters can roll back only while every retained schema remains readable by the selected version. Otherwise leave the newer reader available or stop analysis; never reinterpret.
- Writer rollback requires proving the prior writer understands every retained segment. Unknown future segments make the older writer unavailable, not permissive.
- Retention changes affect stored data irreversibly. Roll back code/config only for future cleanup; do not claim deleted segments are recoverable.
- Migration rollback selects the untouched legacy source or verified destination through a reviewed operator step. Never merge directories or delete either automatically.
- P14/P16 rollback leaves P17 disabled/fail-closed if authoritative snapshots, fingerprint keys, DataRoot, ACL, or writer ownership are unavailable.
- Do not delete feedback/quarantine/report data merely because application support is reverted; use the last compatible reviewed retention/migration tooling and explicit authorization.

## Risks and mitigations

- **P14/P16 may change before they land.** Re-check the exact receipt, 24-hour authority window, `FeedbackRoot`, outer lease, secret-source, catalog, ACL, and logging contracts at implementation start; reconcile plans if meaning changes and never duplicate authority.
- **One event per attempt prevents edits.** The UI collects the complete submission once. A future revision/retraction needs a new immutable event type and owner decision, not in-place mutation.
- **Raw comments can still contain sensitive data.** Warn clearly, require local consent, default external consent off, structurally bound text, restrict ACLs, shorten retention if approved, and never export/quote it.
- **Query HMAC permits equality analysis and key rotation splits cohorts.** This is intentional; never use an unkeyed hash, merge across key IDs, or retain old keys merely for analytics. A future cross-generation comparison requires an approved migration/privacy/key-lifecycle decision.
- **Exclusive writer ownership conflicts with IIS overlap.** Fail readiness and rely on P15 drain/stop/single-writer deployment; unsafe concurrent append is not a compatibility fallback.
- **Durable flush can add latency.** Keep records small, serialize outside the lock, measure p50/p95, and preserve durability rather than batching acknowledgments without a reviewed loss window.
- **Daily/size sealing delays the newest data from analysis.** Analyzer consistency is favored over active-file races; graceful shutdown and bounded age make the delay explicit.
- **A corrupt retained segment blocks writes.** This preserves idempotency truth. The tolerant analyzer can still produce partial aggregates; operator recovery requires a reviewed migration/quarantine action.
- **Retention can shorten the 90-day window under the byte/event cap.** Emit a fixed capacity-retention metric/report and tune only through an owner-approved option change.
- **Small-cohort suppression is not formal anonymization.** It reduces accidental disclosure; P16 ACLs, minimal storage, consent, and no raw export remain primary controls.
- **External model output may be wrong or induced by comments.** Treat comments as untrusted bounded JSON data, never execute recommendations, do not quote them, and keep the deterministic local report authoritative.
- **Legacy data cannot gain retrospective trust or consent.** Segregate/drop sensitive fields and recommend disposal when operational inventory confirms it is unnecessary.
- **Cross-language schema drift is easy.** Check in one JSON Schema plus golden invalid/valid fixtures consumed by both suites; never maintain prose-only field definitions.
- **Large idempotency indexes consume memory.** `MaxRetainedEvents` is an exact cap; benchmark 100,000 events and lower the approved cap if measured memory is unacceptable.
- **Hashes are integrity checks, not signatures.** P16 ACLs prevent ordinary writes; do not claim tamper-proof audit storage.

## Open owner decisions

### Decision 1 — Privacy and consent baseline

Choose privacy-minimal events with no identity/raw query/plan/result, required local-storage consent, and a separate unchecked external-comment consent, or retain richer raw analytics. Recommendation: privacy-minimal; keyed query equality, plan/model/outcome metadata, and optional comments support useful analysis without silently exporting directory questions.

Blocked until decided: Slices 1, 2, 6–9 and the notice text/version.

### Decision 2 — Submission semantics

Choose one immutable primary feedback event per terminal attempt or support editable/multiple ratings. Recommendation: one event with transport replay only; it bounds abuse and keeps analysis deterministic. Revisions can later use an explicit linked event type instead of mutating history.

Blocked until decided: Slices 1, 4, and 6.

### Decision 3 — Initial storage and time limits

Approve a 24-hour submission window, 90-day event retention, 7-day torn-tail retention, 30-day reports, 16 MiB/day segments, 100,000 events, 256 MiB retained data, and 1 GiB free-space headroom. Recommendation: start finite and tune from safe metrics before production use.

Blocked until decided: Slices 1, 3–5, and 9. The approved 24-hour value must remain equal to P14's private receipt lifetime; changing it requires coordinated P14/P17 review.

### Decision 4 — Safe analysis surface

Choose aggregate-only exports with minimum cohort five and separately consented, bounded external comment analysis, or permit raw analyst exports. Recommendation: aggregate-only with no raw-export escape hatch; restricted source access remains an exceptional operator procedure, not a routine tool feature.

Blocked until decided: Slices 7, 8, and 10.

## Advisory review

### Round 1 — 2026-07-21T23:11:37Z

**Reviewer**: Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict**: Revisions required

- Replaced unrecoverable owner-scoped indexes with persisted `job_id` and `(job_id,idempotency_key)` keys; declared keys job-scoped and kept live P14 authorization as the only owner boundary.
- Scoped transport replay to P14's authorization/submission window, permitted an exact old-notice replay only after that live check, and clarified that post-window self-containment is for storage/analysis rather than API authorization.
- Made legacy migration IDs, timestamps, segment IDs, manifests, and output byte-deterministic from the source inventory/tool/options contract.
- Canonicalized every event timestamp to one fixed seven-fraction UTC representation and made external byte-cap sampling order exact.

### Round 2 — 2026-07-21T23:18:46Z

**Reviewer**: Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict**: Accepted

- Confirmed all five round-one repairs are coherent across invariants, transport ordering, event schema, idempotency/recovery, deterministic migration, timestamp serialization, sampling bounds, and their tests.
- Found no remaining material contradiction or cold-agent implementation blocker in the round-one repair surface. P14/P16 contracts became concrete afterward, requiring one final boundary review.

### Round 3 — 2026-07-21T23:37:41Z

**Reviewer**: Headless Claude Code 2.1.217 / `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict**: Accepted

- Confirmed the exact P14 owner-authorized 24-hour receipt, no-owner disposable query seam, server-derived direct-child retry projection, and independent two-hour public retention boundary are consumed without duplicate authority.
- Confirmed P16 owns `FeedbackRoot`, the outer lease, canonical active secret source, catalog/ACL/logging boundary, and no-old-key lifecycle while P17 alone owns HMAC/event/cohort semantics.
- Confirmed `received_at_utc + SubmissionWindowHours` is a conservative non-evictable replay-ledger horizon, so capacity cleanup cannot enable a duplicate while P14 can still authorize submission.
- Found no material correctness, privacy, durability, idempotency, or cold-agent implementation blocker in the reviewed snapshot.
- A later independent read corrected deterministic test 10, whose phrase “same key ... another job conflicts” contradicted the canonical job-scoped key rule. The test now requires a same-key submission on a different owner-authorized job to be independent while preserving both same-job conflict cases. This final correction was not re-reviewed; the three-round limit prohibits a fourth round.
