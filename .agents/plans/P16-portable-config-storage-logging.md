# P16 — Portable Configuration, Storage, and Logging

Status: **Reviewed — implementation is not authorized**

Owner approval: Pending

Implementation dependencies: P01 must provide canonical automated verification. P02, P03, P04, P07, P12, and P13 must first supply their reviewed provider options, runtime/logging compatibility baseline, canonical policy, artifact interface, compiler/key boundary, and safe failure/event contracts. P16 must land before P14's durable job database, P15's production IIS projection, P17's feedback store, P18's ingestion spool, and P20's readiness checks consume its roots and startup validation.

Review status: Round 3 accepted the reviewed snapshot. Two reviewer precision suggestions and a later P17 key-source boundary reconciliation were applied afterward without a fourth round; those final revisions were not re-reviewed, per the three-round cap.

## Problem

Configuration, filesystem state, and logging currently have no single validated composition boundary.

The application hard-codes `E:\WWWOutput`, resolves some relative files against the process working directory and others against the content root, binds scalar values throughout controllers and services, silently clamps or falls back on malformed settings, and keeps empty secret keys beside non-secret configuration. IIS deployment, local execution, tests, and a published application can therefore resolve different paths and tolerate different invalid states.

Logging is configured twice: `Program.cs` reads the `Serilog` section and then independently adds console and rolling-file sinks already present in that section. The same event can be duplicated, and the programmatic file sink lacks the checked-in retention cap. Separate per-query text files persist raw query/context, raw model response, mutable model/executed plans, exception messages, user names, and physical output paths. Ordinary structured logs also include queries, LDAP lookup values, user names, model identifiers, and exception objects whose messages can contain dependency data or paths.

P07 replaces result files with a bounded artifact store, P13 supplies safe failure/event fields, and P17 will own a consented feedback schema. P16 must provide one portable, fail-closed configuration and storage composition without recreating those domain contracts.

## Repository evidence

Evidence was verified at `6edfe58d27054cce7bc4411406097fa0ad8ed1d7`.

- `csharp/Services/QueryLogHelper.cs:15` hard-codes `OutputRoot = @"E:\WWWOutput"` and creates owner-derived directories.
- `QueryLogHelper.WriteQueryLog` persists account name, query, context, output path, warnings, error text, raw provider response, generated plan, and executed plan in plaintext.
- `csharp/Services/JsonLinesFeedbackStore.cs:34` derives feedback storage from that hard-coded root and creates it in the constructor.
- Controllers and `QueryJobManager` construct log/output paths and perform direct `File.WriteAll*`, `File.ReadAllBytes`, and append operations outside a storage abstraction.
- `ClaudeService` checks `Claude:PromptTemplate` with `File.Exists` against the ambient process working directory; `PlanValidator` resolves allow-list paths against `IWebHostEnvironment.ContentRootPath`.
- At least 16 source references inject or consume raw `IConfiguration`; parsing/default/clamping behavior is distributed across `Program`, controllers, provider, LDAP, validator, CSV, and job services.
- `ClaudeService` parses `MaxTokens` with `int.Parse` and parses temperature independently in two paths. P02 owns sampling omission; P16 owns the remaining typed binding defect.
- `PlanValidator.LoadAllowedAttributes` silently falls back to built-in defaults when an explicitly configured allow-list is missing, empty, or unreadable.
- `csharp/appsettings.json` contains empty `Claude:ApiKey` and `Security:HmacSecretKey` fields next to a literal auth-token value and environment-specific model, role, DN, and path settings.
- `Program.cs:10-14` calls `ReadFrom.Configuration` and then `WriteTo.Console` and `WriteTo.File`; `appsettings.json` already declares both sinks.
- The programmatic file sink uses relative `logs/...` storage and has no `retainedFileCountLimit`; the configured sink writes the same relative path with a 30-file cap.
- `Program.cs` logs fatal exceptions directly. Controllers/provider/directory services log queries, lookup values, user/owner names, paths, provider error strings, and exception objects.
- `csharp/AdQueryOrchestrator.csproj` copies only `Configuration/*.txt`; there is no declared portable writable data root or startup write/lock probe.

## Goals

1. Bind every application setting through an owner-specific immutable options type and validate it before the listener accepts traffic.
2. Keep one canonical key and owner for each setting; do not create P02/P06/P07/P09/P14/P17/P20 aliases.
3. Require one absolute, dedicated, validated `Storage:DataRoot` with fixed child roots and no user-derived path segments.
4. Acquire one process-lifetime DataRoot lease before file-backed services or the file log sink start.
5. Make P07 artifacts, P14 state, P17 feedback, and operational logs consume typed child paths without receiving the physical root from HTTP/domain callers.
6. Own one versioned, immutable production-promotion projection and offline validator that P15 applies transactionally without reading configuration or secret values.
7. Keep secrets out of committed configuration and ordinary objects/logs while preserving standard environment/IIS override names.
8. Use restart-stable configuration snapshots; one operation never observes a mixture of old and new security/provider/storage settings.
9. Configure Serilog once with finite file size/count retention and a bounded graceful flush.
10. Replace arbitrary logging calls with closed, low-cardinality safe event schemas.
11. Remove plaintext query/model/plan transcript logging and direct controller/job filesystem writes.
12. Fail closed on explicitly configured but missing/unreadable policy or prompt files.
13. Add architecture, startup, path, secret-canary, sink, and mutation guards.

## Non-goals

- Do not change provider request capability or sampling behavior; P02 owns it.
- Do not change query, filter, LDAP, artifact, job, feedback, or health limits; their owning plans define those values.
- Do not redesign P07 artifact layout, quota, marker, lease, or publication semantics.
- Do not define P14's database schema or job transitions; P16 supplies only its validated state directory.
- Do not define P17's event contents, analyzer compatibility, consent, or retention; P16 supplies only its validated feedback directory and logging boundary.
- Do not implement P20 probes or expose configuration details in health responses.
- Do not embed IIS credentials, machine-specific paths, ACL identities, or deployment transactions in application runtime code. P16 owns the value-bearing projection and companion validator/adapter; P15 owns its target-scoped transactional application and rollback.
- Do not add a cloud secret manager, telemetry exporter, distributed tracing backend, database server, or remote log collector.
- Do not claim application checks prove volume encryption, backup, antivirus, or administrator ACL policy; P15's deployment verification owns host controls.
- Do not auto-copy, delete, or reinterpret legacy `E:\WWWOutput` content.
- Do not remove user-authorized query metadata from a P07 download merely because raw operational transcript persistence is removed; P07 owns bounded artifact metadata.

## Cross-plan authority and landing order

### P02 and P03

P02 owns `ClaudeOptions`, the one provider request builder, sampling modes, provider URL/model/endpoint fields, and compatible existing key names. P16 extends startup validation for finite numeric/provider connection values and secret-source presence; it does not add another provider options type.

P03 owns runtime and package compatibility. Any logging package change or `Microsoft.Data.Sqlite` package later used by P14 follows P03's restore/vulnerability audit. P16 removes duplicate sink registration only after P03's behavior baseline exists.

### P07

P07's `IResultArtifactStore` remains the only artifact path owner. P16 replaces only its interim `Artifacts:RootPath` producer with `IDataPaths.ArtifactRoot`. P07 keeps all relative identifiers, containment checks, reservations, staging, commit markers, cleanup, and artifact-root lease defenses.

The P16 DataRoot lease is the outer single-writer gate. P07 may retain its child lease as defense in depth; callers never bypass either gate.

### P04, P12, and P13

P12 owns policy compilation and HMAC verification order. P16 binds the HMAC secret, validates/opens the policy-file locations, and produces bounded immutable `ValidatedDirectoryPolicySources` containing no path or secret. P04's canonical `IDirectorySecurityPolicy` factory constructs the one immutable policy snapshot/version from those sources and evaluator capabilities; P12 consumes that snapshot. P16 does not construct a competing policy, sign, verify, or reinterpret a plan.

P16 intentionally supersedes two narrow P04 behaviors as the later configuration-source owner: P04's policy service no longer opens configured files, and a nonempty configured path that is missing, empty, malformed, or unreadable fails startup instead of silently using broader built-in defaults. An absent path still selects P04's documented built-in defaults. Slice 4 must update P04's durable guidance and replace its fallback-preservation characterization with absent-path-default plus broken-explicit-path-fails-startup guards; P04's attribute/operator semantics and shared-policy ownership remain unchanged.

P13 owns safe failure codes, arguments, retry semantics, and public problems. P16 maps startup validation to fixed internal readiness reasons and implements P13's safe logging schemas. It never logs public `detail`, provider bodies, exception messages, or arbitrary diagnostics.

### P14, P15, P17, P18, P20, and P21

- P14 stores its durable database only beneath `IDataPaths.StateRoot`; raw commands are protected state with finite P14 retention, not log content.
- P15 selects a P16 promotion ID, invokes P16's offline validator/opaque adapter, and transactionally applies or rolls back the resulting IIS references and logical ACL projection. P15 discovers the actual app-pool identity and performs target mutation, but it does not define or read P16 keys, values, secrets, roots, sinks, projection schema, or validation rules.
- P17 stores only its versioned feedback files beneath `IDataPaths.FeedbackRoot` and owns their schema/retention. P16 owns and lands the restart-stable `IFeedbackQueryFingerprintSecretSource`; P17 consumes that seam without rebinding configuration and owns the HMAC/domain, event-field, and cohort semantics.
- P18 spools untrusted uploads only beneath `IDataPaths.IngestionRoot` and owns opaque request files, quotas, handles, cancellation/failure cleanup, and stale-startup cleanup. It never uses OS temp or P07 staging and never publishes spool content.
- P20 consumes fixed startup/storage/logger readiness reasons and exposes no paths or values.
- P21 may move composition classes but must retain one options/root/logger owner and architecture guards.

P16 lands before P14/P15/P17/P18/P20 production wiring. A later plan adapts to P16's interfaces rather than adding a temporary second root or sink.

## Configuration registry and ownership

Create one code-owned `ApplicationConfigurationCatalog` consumed by binding, the promotion validator, package scans, and architecture tests. Documentation points to that catalog and may render it in tests; it does not maintain a second hand-written table. Each catalog row names:

- canonical section/key;
- typed options owner;
- whether the value is required, secret, path, or environment-specific;
- startup validator;
- restart requirement;
- consuming plan/component.

The catalog points to, rather than duplicates, bounds owned by P02/P06/P07/P08/P09/P10/P14/P17/P20. An architecture test fails when an application option is read through `IConfiguration` outside the composition root or an options validator.

Representative owners:

```text
Claude                 ClaudeOptions (P02, extended by P16 validation)
Storage                StorageOptions (P16)
OperationalLogging     OperationalLoggingOptions (P16)
Authorization          AuthorizationPolicyOptions (P16/P04 boundary)
Security policy files  DirectoryPolicySourceOptions (P16 sources -> P04 snapshot -> P12)
QueryBudgets           QueryBudgetOptions (P06)
Artifacts              ArtifactStoreOptions (P07, root supplied by P16)
LdapExecution          LdapExecutionOptions (P09)
Jobs                   JobOrchestrationOptions (P14)
Feedback               FeedbackStoreOptions (P17)
Feedback query key     FeedbackQueryFingerprintSecretOptions/source (P16 -> P17)
Health                 HealthOptions (P20)
```

Do not create compatibility aliases indefinitely. A renamed key receives one explicit startup migration window with a warning containing only the key name, then is removed in a separately documented version. Secrets are never echoed in a warning.

## Typed immutable options

Use `AddOptions<T>().BindConfiguration(...).Validate(...).ValidateOnStart()` or an equivalent owner-specific validator. Consumers receive `IOptions<T>` or an immutable derived snapshot, never raw `IConfiguration` or `IOptionsMonitor<T>`.

Rules:

1. Numeric and duration values parse through typed binding and must be finite, positive, and within their owner plan's ceiling. No `int.Parse`, `Math.Max`, zero-as-unlimited, or silent clamp remains in consumers.
2. Unknown enum/string modes fail startup rather than select a default branch.
3. An absent optional path means the documented built-in resource is used. A non-empty configured path that is missing, empty, malformed, or unreadable fails startup; it never falls back silently.
4. Relative read-only resource paths resolve exactly once against `IHostEnvironment.ContentRootPath`. Writable paths never resolve from the current directory.
5. Base URLs must be absolute HTTPS URIs; an explicit loopback HTTP exception is allowed only in Development and is tested.
6. Required authorization roles/origins/policy values are trimmed, deduplicated under their specified comparer, bounded, and validated before middleware construction.
7. P02's `MaxTokens` becomes a positive bounded integer in `ClaudeOptions`; both provider paths consume the same value.
8. When HMAC validation is enabled, the secret must be present before P12's verifier is constructed. Disabled mode does not fabricate a key.
9. Options are process-lifetime snapshots. A change requires a controlled restart so P12 executables, P14 commands, provider requests, and storage operations cannot observe mixed generations.

Startup reports every options failure through fixed key/reason codes where practical, but never prints the effective value. Production readiness remains false and the HTTP listener is not promoted.

## Secret configuration

Keep the P02/P12-compatible canonical names:

```text
Claude:ApiKey
Claude:AuthToken
Security:HmacSecretKey
```

Reserve exactly two additional canonical P16/P17 boundary keys; create no alias:

```text
Feedback:QueryFingerprint:ActiveKeyId   non-secret
Feedback:QueryFingerprint:ActiveKey     secret
```

When feedback is enabled, both are required. `ActiveKeyId` must match ordinal-lowercase `^[a-z0-9][a-z0-9_-]{0,31}$` and is preserved verbatim; `ActiveKey` must be canonical base64 that decodes to exactly 32 random bytes. The ID may enter a P17 event but not an operational log or metric. The key is subject to every `SecretValue` prohibition below. Missing pairs, malformed IDs/base64, and wrong decoded lengths fail startup without echoing a value. Package/default validation checks only catalog shape and the absence of assigned secret/environment overrides; protected promotion validation checks the ID and secret-reference shape. Runtime startup alone resolves and decodes the secret value.

P16 owns and lands an internal `IFeedbackQueryFingerprintSecretSource.GetActive()` returning one process-lifetime `FeedbackQueryFingerprintSecretSnapshot`. The snapshot is a non-record, non-serializable/non-display object containing the validated active ID and P16 `SecretValue`; only P17's HMAC component may receive it. P17 later owns `FeedbackQueryFingerprintKeyId`, the domain separator and canonical query bytes, HMAC-SHA-256 calculation, persisted field semantics, and rotation/cohort tests. P17 must not bind these keys again.

Rotation supplies a new pair through a new immutable promotion and controlled restart. P16 does not retain old key material: P17 persists `(query_fingerprint_key_id, query_fingerprint_sha256)` as an opaque cohort key, never verifies or recomputes an old digest, and intentionally starts a new cohort after rotation. Cross-generation rehashing or old-key retention requires a future approved migration/security decision.

Remove these keys, including empty placeholders and comments suggesting values, from every committed `appsettings*.json`. Tests scan tracked configuration and publish output for the canonical secret keys with assigned values and representative secret canaries.

Development uses .NET user secrets or process-scoped environment variables. IIS production selects a P16 promotion whose protected provider references resolve the same hierarchical keys; P15 applies only the opaque promotion/catalog bootstrap references. Rotation creates a new promotion and requires a controlled restart under the restart-stable snapshot decision.

Wrap bound values in a non-serializable/non-display `SecretValue` abstraction whose `ToString()` returns a fixed redaction token. Do not place secrets in records with generated `ToString`, validation messages, exception text, metrics, logger state, configuration dumps, job/feedback/artifact metadata, or health output. Provider/header and HMAC components receive the narrow secret value directly.

P16 does not claim environment variables or catalog references are encrypted storage. P15 documents host access and applies only P16's opaque bootstrap projection; volume/credential-store upgrades can replace the P16 secret provider without changing consumers.

## Versioned production-promotion projection

P16 owns a companion configuration assembly/tool used by both application startup and P15. It defines one closed, versioned projection envelope containing:

```text
SchemaVersion
PromotionId
Canonical non-secret option entries
Canonical secret-provider references, never secret values
DataRoot and fixed logical child/permission profile
Operational logging profile
Immutable policy/prompt resource references and digests
ProjectionSha256 over canonical value-bearing envelope bytes
```

Promotion records are immutable create-new entries in a P16-owned protected catalog outside every release and outside DataRoot, because DataRoot is itself selected by the projection. Deployment target configuration supplies the catalog location to the P16 adapter; neither the catalog path nor its contents enter P15's journal or logs. Updating configuration or rotating a secret reference creates a new promotion ID; never edit an applied record in place.

The IIS-facing bootstrap consists only of opaque P16-owned catalog/promotion references. P15 calls the P16 adapter to plan, apply, and read back those references and the logical ACL projection as an opaque transaction participant. P15 records only schema version, promotion ID, and projection hash. It never receives option values, secret references, resource paths, DataRoot, or environment-variable contents.

Ship two non-networking entry points from the same companion assembly:

- `ValidatePackageDefaults(publishRoot)` is non-elevated and requires no promotion, secret access, target environment, or app-pool identity. It uses `ApplicationConfigurationCatalog` to prove the publish output contains no secret/environment override, unknown owned key, nested deployment state/log/archive, or default inconsistent with the package schema. It returns only a schema version, publish-manifest hash, fixed outcome code, and process result.
- `ValidatePromotion(publishRoot, protectedPromotionId, targetEnvironment, appPoolIdentity)` is the elevated/read-protected preflight entry point. It validates the closed envelope/hash, invokes the same package-default core, resolves secret references internally, runs the same presence/type and bounded resource validators as startup, verifies DataRoot/catalog/release separation, and computes fixed logical ACL requirements without acquiring the live DataRoot lease or mutating IIS/files.

`ValidatePromotion` returns only `{ schema_version, promotion_id, projection_sha256, outcome_code }` with a nonzero process result on failure. P15 packaging invokes only `ValidatePackageDefaults`; P15 target preflight invokes `ValidatePromotion` through the opaque adapter.

Runtime startup loads the same immutable envelope through the same P16 library, then performs the authoritative DataRoot probe/lease and store initialization. The offline result is a pre-promotion proof, not permission to skip startup validation.

The adapter also classifies prior P16 state as `Applied(id, hash)` or `Absent` and detects colliding unmanaged owned-key state without exposing it. P15 owns the deployment journal, exact old/new pair transitions, IIS mutation, read-back timing, and rollback. P16 owns the schema, key set, value parsing, secret resolution, validation, hash, and logical ACL/environment-reference projection.

## Portable DataRoot contract

Add startup-validated options:

```text
Storage:DataRoot                    required absolute path
Storage:LeaseFileName               fixed by code; not configurable
OperationalLogging:FileSizeBytes    33,554,432 recommended
OperationalLogging:RetainedFiles    14 recommended
OperationalLogging:FlushTimeout     5 seconds recommended
```

`DataRoot` has no production default. Tests supply unique temporary roots. A Development-only local value may live in untracked user configuration, never in code or a committed machine-specific path.

Normalize once with platform path semantics and reject:

- a relative path, URI, empty value, regular file, drive/filesystem root, content root, web root, repository root, or an ancestor/descendant overlap with content/web roots;
- alternate data stream syntax, device paths, invalid characters, or a path escaping after normalization;
- an existing reparse point in the root or fixed child chain unless a separately reviewed hardened policy is approved;
- a root containing an incompatible bounded `.adquery-root.json` schema marker;
- inability to create, exclusively lock, write, flush, atomically rename within, read, and delete a fixed-shape probe.

After validation, create only fixed children:

```text
<DataRoot>/artifacts
<DataRoot>/feedback
<DataRoot>/ingestion
<DataRoot>/logs
<DataRoot>/state
```

Create/read a bounded version marker containing only schema version and application storage identifier. Hold an exclusive create/open-with-`FileShare.None` DataRoot lease for process lifetime before configuring the file sink or starting file-backed services. A second writer fails startup/readiness with a stable reason; it never selects another directory or opens shared sinks.

`IDataPaths` exposes typed child roots only to the approved P07, P14, P17, P18, and logging adapters. Controllers/domain services never receive a root or combine physical paths. Owner names, SIDs, queries, job/artifact IDs, and caller filenames never become root-level path segments; domain stores keep their own already-reviewed opaque identifier rules beneath their child.

Do not log or return DataRoot, child roots, marker/lease names, probe failures containing paths, drive information, or free-space paths. P20 receives a fixed reason and safe capacity bucket only.

## Bootstrap sequence

Use one explicit startup sequence:

1. Create a console-only bootstrap logger with a fixed minimal template and no exception destructuring.
2. Load standard base/environment configuration plus the documented out-of-repo development providers, or load the selected immutable P16 promotion envelope in production.
3. Bind and run the same pure `StorageOptions` and `OperationalLoggingOptions` validators later registered with DI.
4. Validate/create the dedicated root, verify the marker, and acquire the process-lifetime lease.
5. Configure the final Serilog pipeline once from typed options and `IDataPaths.LogRoot`; replace and dispose the bootstrap logger.
6. Register all owner-specific options with `ValidateOnStart`, construct immutable provider/options snapshots, force P04's canonical policy snapshot factory, and resolve a startup validation sentinel that forces every required value.
7. Initialize the already-landed P07 store beneath its fixed child and publish the other typed child roots as extension points. P14, P17, and P18 initialize their own stores/coordinator only when those later plans land; P16 does not invent placeholder services for them.
8. Mark startup/readiness successful and then accept/promote traffic.

Every failure unwinds acquired handles and flushes the bootstrap/final logger within the bounded timeout. Do not catch a configuration/storage failure, log it, and continue with fallback defaults.

## One bounded logging pipeline

Remove the duplicate programmatic/configured sink declarations. Configure one pipeline with exactly:

- one console sink for host capture; this is the intentionally separate non-file host sink, not another audit/file pipeline;
- one and only one structured newline-delimited JSON rolling file sink beneath `IDataPaths.LogRoot`;
- daily and size-based rolling;
- a 32 MiB per-file cap, roll-on-size, and 14 retained files by the recommended initial decision;
- no shared writer, no unlimited buffering, and a bounded five-second flush on stop;
- framework overrides that suppress routine HTTP/client internals without hiding P13 safe failure events.

Do not configure a second audit/query transcript sink. Audit-worthy actions use closed `audit_*` events in the same protected structured stream until a separately approved external audit sink exists.

Use source-generated `LoggerMessage` methods or an equivalent closed event catalog. Each event has a stable numeric ID/name, fixed template, fixed level, and exact argument schema. Allowed values include:

- opaque request/job IDs when necessary for correlation;
- a bounded one-way owner audit key, never the account name or SID;
- fixed route/operation/phase/outcome/failure enums;
- bounded numeric counts, durations, queue depths, and utilization buckets;
- P13 code/category/origin/retry fields.

The owner audit key is the full 32-byte SHA-256 digest over a fixed domain separator plus the canonical owner subject, encoded base64url without padding. It is for correlation, not authorization, and is never used as a filesystem identifier.

Forbidden values include query/context/prompt text, raw or rendered plans, provider bodies/headers/model responses, API/HMAC credentials, model identifiers/names, CSV headers/cells/comments, LDAP values/DNs/filters/GUIDs, user/account/role names, owner SIDs, physical paths/filenames, public problem detail/title, arbitrary validation text, and exception messages/stacks/objects.

This intentionally supersedes only P02's earlier allowance to log the effective model identifier. P16 is the later logging-catalog owner and removes that field because configured routing identifiers can encode environment/provider topology. P02 retains model selection in the request/outcome contract and tests, but its logging acceptance and implementation documentation must be updated in the same P16 safe-event slice so no contradictory durable instruction remains.

Expected failures log their stable descriptor only. Unexpected failures log `internal_error`, correlation, fixed component/phase, and a bounded fixed exception-class bucket; the `Exception` object and its message/stack remain in P13's process-local causal capture and are not serialized by Serilog. Debug dumps or incident capture with stronger access controls require a separate approved operational procedure.

Metrics follow the same low-cardinality field allowlist. File/log sink health never tags a root, file, machine/user identifier, or exception text.

## Remove raw transcript and direct filesystem behavior

After P07 is available:

- delete `QueryLogHelper.OutputRoot`, `GetUserDirectory`, plaintext `WriteQueryLog`, plan serialization for logging, and download-history append behavior;
- remove raw model-response, model-plan, executed-plan, query/context, output-path, and exception-message persistence from synchronous, queued, retry, CSV, and download paths;
- remove controller/job-manager `File.*` and `Directory.*` calls for results/logs;
- use P07 artifact metadata/leases for authorized results and downloads;
- emit only the closed safe completion/failure/audit events;
- keep raw provider envelopes bounded in memory only as long as P02 needs to classify/parse them, then discard them;
- preserve P17 feedback content only in the P17 store, never mirror comments/query echoes into operational logs.

There is no automatic legacy `E:\WWWOutput` migration because the owner states this application is not in production. Startup neither probes nor deletes that path. Any real legacy data discovered before implementation stops the removal slice for an explicit retention/migration decision.

## Policy and read-only resource files

Resolve prompt and allow-list files through a `IReadOnlyResourceResolver` with content-root-relative or explicitly absolute configured paths. Normalize and validate once; never use the process working directory.

- Absent prompt-template configuration selects the checked-in built-in template exactly as P02 documents.
- A configured prompt path must exist, be a regular non-reparse file, be bounded in encoded bytes, decode as strict UTF-8, and be readable; otherwise startup fails.
- Every configured allow-list must exist, be nonempty after comments/whitespace, fit P16 byte/line/count/encoding bounds, and be readable; otherwise startup fails closed. No fallback to broader defaults is allowed for an explicit path.
- P16 returns bounded immutable source lines/content without paths. P04 validates attribute/operator semantics and constructs the one immutable `IDirectorySecurityPolicy` snapshot/version; P12 consumes it and carries its version/fingerprint in compiled executables.
- Files are restart-only. No watcher/reload can change one in-flight operation or durable queued command silently.

Do not log resource paths or contents. Startup reports only fixed resource kind and failure reason.

## Deterministic tests

Use P01's canonical test project, temporary directories, injected filesystem probes, capturing configuration providers, an in-memory test sink, fake time where supported, and child-process helpers only for the exclusive-lock integration. No test uses `E:\`, the repository root as writable storage, real secrets, IIS, LDAP, provider calls, or sleeps.

### Options and secrets

1. Every registry key binds to exactly one options owner; duplicate/unknown compatibility aliases fail the architecture test.
2. Missing, malformed, zero, negative, overflowing, or out-of-range numeric/duration values fail startup without consumer clamps.
3. P02 `MaxTokens` is one positive typed value used by both request paths.
4. Invalid URL, mode, role, CORS origin, policy token, and explicitly configured missing/empty resource fail before the listener is promoted.
5. Missing HMAC or feedback-query fingerprint secrets fail only when their corresponding feature is enabled; disabled modes never fabricate or log a key. The feedback pair also rejects a malformed ID, noncanonical base64, or a decoded length other than 32 bytes.
6. Tracked `appsettings*.json`, publish output, examples, and captured logs contain no assigned canonical secret or canary value.
7. `SecretValue` cannot serialize and always renders a fixed redaction token.
8. Mid-process source changes do not alter an `IOptions<T>` snapshot; restart creates the new generation.
9. An absent allow-list path selects P04's built-in defaults, while a broken explicit path fails startup; resulting `ValidatedDirectoryPolicySources` contains bounded content but no source path or secret.

### Promotion projection

10. An envelope with an unknown/missing/duplicate key, invalid hash, inline secret, unresolved secret reference, package-default override, invalid resource, or release/DataRoot/catalog overlap fails offline with a fixed code and no value/path output.
11. Package-default validation needs no promotion/secret/identity and makes zero external mutations; promotion and runtime validation accept/reject the same typed fixture, and offline validation never acquires the live DataRoot lease.

### DataRoot

12. Relative, root, content/web/repository-overlapping, file, device/stream, escaping, and reparse fixtures fail without revealing paths.
13. A valid temporary root creates only the marker, lease, probe lifecycle, and five fixed children.
14. Marker mismatch fails without modifying the existing marker or contents.
15. A second process/instance cannot acquire the same root and cannot configure a second file sink/store.
16. Probe create/flush/rename/read/delete failures each unwind the lease and return a fixed reason.
17. `IDataPaths` gives P07/P14/P17/P18/logging the expected child and is unavailable to controllers/domain services.
18. P07 references and containment tests behave identically after its root producer changes.
19. No owner/query/job/artifact input can change a root or fixed child name.

### Logging and migration

20. One emitted event produces exactly one console record and one file record, not duplicates.
21. Final file sink options have the exact size, roll, retained-count, non-shared, and flush bounds.
22. Every catalog event accepts only its closed argument types; forbidden destructuring and direct application `ILogger.Log*` calls fail architecture tests.
23. Secret/query/context/provider/model-name/plan/CSV/LDAP/user/SID/path/exception canaries appear nowhere in captured operational or startup logs.
24. Expected and unexpected failures emit stable P13 codes without exception objects/messages/stacks.
25. Graceful and startup-failure paths flush once within the configured bound.
26. No controller/job manager/provider/directory service performs direct writable `File`/`Directory` operations outside approved P07/P14/P17/P18/P16 adapters.
27. `QueryLogHelper`, raw transcript sections, download-history append, and `E:\WWWOutput` references are absent.
28. Authorized P07 download metadata remains available while no copy is written to operational logs.
29. P17 feedback writes only under its supplied root and comments are never logged.

## Red/green guard proof

For every test-bearing slice:

1. Add the focused test and demonstrate the current behavior or missing contract fails.
2. Implement the smallest slice and make the focused test pass.
3. Temporarily restore/bypass the protected behavior.
4. Confirm the focused guard fails for the intended reason.
5. Restore the implementation and run P01's canonical verifier.
6. Commit only the restored slice.

Mandatory mutations:

- restore `Math.Max`/`int.Parse` consumer fallback; invalid-options startup guard fails;
- inject a nonempty secret into checked-in JSON; configuration secret scan fails;
- resolve a prompt from current working directory; deterministic resource-root guard fails;
- accept a configured missing allow-list and fall back; fail-closed policy guard fails;
- set DataRoot to a root/content directory or follow a reparse point; containment guard fails;
- allow a second DataRoot lease; single-writer guard fails;
- re-add either programmatic duplicate sink; one-event/one-record guard fails;
- remove file size or retained-count bound; logging-options guard fails;
- log a raw query, provider body, effective model identifier, LDAP value, path, secret, or `Exception`; canary guard fails;
- restore `QueryLogHelper.WriteQueryLog` or a controller `File.WriteAll*`; architecture/transcript guard fails.

Leave no mutation, probe file, temporary root, secret canary, or captured log in the worktree.

## Implementation slices and commits

Each slice addresses one finding, is verified, and is committed before the next. Do not amend, squash, or combine completed slices.

### Slice 1 — Configuration registry and typed binding

Commit intent: `refactor: bind validated application options`

- Add the canonical registry and owner-specific `ValidateOnStart` wiring.
- Complete P02 `ClaudeOptions` finite parsing and migrate remaining raw consumer reads.
- Add immutable restart-stable snapshots and invalid-config tests.
- Add the no-raw-`IConfiguration` architecture guard.

### Slice 2 — Portable DataRoot

Commit intent: `feat: establish a portable data root`

- Add `StorageOptions`, validator, marker, probe, outer lease, and `IDataPaths`.
- Create the five fixed children and inject only typed child paths.
- Add containment/reparse/overlap/lease/unwind tests.

### Slice 3 — Artifact-root migration

Commit intent: `refactor: place artifacts under the data root`

- Replace P07's interim root producer with `IDataPaths.ArtifactRoot`.
- Preserve P07 identifiers, child lease, quotas, layout, publication, recovery, and cleanup unchanged.
- Add P07 parity and no-physical-path tests.

### Slice 4 — Secret and resource sources

Commit intent: `security: remove secrets from checked-in configuration`

- Remove committed secret keys/placeholders and document out-of-repo providers.
- Add `SecretValue`, provider/HMAC injection, checked-in/publish scans, and conditional presence validation.
- Add the canonical feedback fingerprint pair plus the restart-stable P16 secret-source seam; do not implement P17's HMAC/event semantics or retain old key material.
- Add deterministic content-root resource resolution and fail-closed configured-file validation.
- Rewire P04's `IDirectorySecurityPolicy` factory to consume `ValidatedDirectoryPolicySources` instead of opening configured files.
- Update P04's durable file-loading/fallback guidance and replace its explicit-path-fallback characterization with absent-path-default and broken-explicit-path startup-failure guards.

### Slice 5 — Production-promotion projection

Commit intent: `feat: validate immutable production configuration`

- Add the closed promotion schema/catalog, canonical hashing, create-new records, secret references, and owned-key catalog.
- Ship the shared offline/runtime validator and opaque P15 adapter with fixed result envelopes.
- Add package-default, validation-parity, prior-state, collision, secrecy, and zero-mutation tests.

### Slice 6 — Single bounded log pipeline

Commit intent: `fix: configure one bounded logging pipeline`

- Add console bootstrap, post-lease final configuration, one console/file sink pair, JSON formatting, size/count rolling, and bounded flush.
- Delete duplicate `ReadFrom` plus programmatic sink behavior.
- Add event-duplication, sink-options, lease-order, and shutdown tests.

### Slice 7 — Safe event catalog

Commit intent: `security: constrain operational log fields`

- Add closed source-generated events and owner audit key.
- Migrate provider, directory, controller, job, artifact, feedback, startup, and health-adjacent logs.
- Remove exception destructuring and forbidden values.
- Remove P02's effective-model logging allowance and update its implementation guidance without changing request/model selection.
- Add event-schema architecture and secret/data-canary tests.

### Slice 8 — Remove query transcript storage

Commit intent: `security: remove raw query transcript logs`

- Delete `QueryLogHelper` writable/transcript behavior and direct controller/job filesystem calls after P07 migration.
- Stop retaining raw provider responses/plans outside bounded in-process classification.
- Preserve authorized artifact metadata and P13/P17 handoffs.
- Add no-transcript/no-direct-filesystem and download parity tests.

### Slice 9 — Consumer roots and operational documentation

Commit intent: `docs: operationalize portable application state`

- Supply `StateRoot`, `FeedbackRoot`, `IngestionRoot`, and fixed readiness reasons for P14/P17/P18/P20.
- Document restart-only changes, secret rotation, root lease, capacity, backup/retention boundaries, and P15 projection inputs.
- Add published-output startup and architecture checks.

Verification:

```powershell
pwsh -NoLogo -NoProfile -File scripts/verify.ps1
```

For the DataRoot lease, run the P01-owned child-process integration test from the canonical verifier. For deployment-specific ACL/provider checks, P15 runs its approved staging smoke; P16 tests remain credential-free.

## Acceptance criteria

- Every application setting has one typed owner, startup validator, and canonical key.
- No application consumer outside composition/validators injects raw `IConfiguration` or silently clamps/falls back.
- P02 sampling omission remains intact and both provider paths use one validated finite token limit.
- Checked-in configuration/publish output contains no provider, HMAC, or feedback-fingerprint secret assignment.
- The enabled feedback path receives one validated restart-stable P16 fingerprint secret snapshot; no second binding, old-key ring, serialized/displayed key, or operational-log field exists.
- Production requires one absolute dedicated DataRoot; no hard-coded drive or current-directory writable path remains.
- Root marker/probe/lease and every invalid-root test fail closed before traffic/storage sinks.
- Only P07/P14/P17/P18/logging adapters receive fixed child roots; controllers/domain callers receive no physical path.
- P07 behavior is unchanged except for its root producer.
- Serilog has exactly one console and one bounded file sink, with no duplicate event emission.
- Logs/metrics contain only closed safe fields and no raw query/provider/plan/CSV/LDAP/user/path/secret/exception data.
- Plaintext query/model/plan transcripts and direct controller/job writable filesystem calls are removed.
- Explicitly configured invalid policy/prompt files fail startup without fallback.
- P04 still owns canonical policy semantics and absent-path defaults, while P16 alone loads configured files and rejects a broken explicit path; no fallback-preservation test contradicts this.
- Configuration is restart-stable across provider, compiler, job, and storage work.
- P14/P15/P17/P18/P20 can consume DataRoot/options/logging contracts without inventing aliases.
- P15 can validate, apply, read back, and restore an immutable P16 promotion by ID/hash without seeing values; offline and runtime typed validation share one implementation.
- Canonical verification and every recorded red/green proof pass.
- Every implementation slice is committed separately.

## Rollback

Use new revert commits; do not rewrite history.

- Revert consumers before removing `IDataPaths`, typed options, or the final logger.
- Keep secrets out of committed files even if a typed consumer migration rolls back; inject the legacy key through the approved external provider.
- Keep DataRoot validation/containment if a child-store migration rolls back; point the child adapter at its fixed validated directory rather than restoring `E:\WWWOutput`.
- Do not restore duplicate or unbounded sinks. A logging rollback may return to one prior sink only with finite retention.
- Do not restore raw query/provider/plan transcript persistence to regain debugging. Use safe correlation and a separately approved incident process.
- Do not restore P04's silent fallback for a broken explicit allow-list path. Remove the explicit path or repair it; an absent path may still select the documented built-in defaults.
- P07 artifact migration rolls back only through its interface and parity tests; never move/delete artifact content automatically.
- P14/P17 must be stopped/reverted before their state/feedback child contracts are removed.
- Restore resource/config keys with their validator and documentation together; never leave a key implying an unenforced guarantee.

## Risks and mitigations

- **Fail-closed startup can expose dormant bad settings.** Report fixed key/reason codes and validate in P15 staging before promotion; do not continue with silent defaults.
- **A required DataRoot adds deployment work.** P15 supplies and ACLs one deliberate location; tests use temporary roots.
- **The outer lease prevents overlapping IIS workers.** This is intentional until all stores prove multi-writer safety. P15 performs drain/switch without overlap against one root.
- **File sink initialization depends on storage.** Keep console bootstrap minimal, validate/lease first, and never partially start file-backed services.
- **Removing raw transcripts reduces ad-hoc debugging.** Use stable correlation, safe counters, deterministic repro fixtures, and P17's explicit consented data contract.
- **Exception suppression can hide diagnostics.** Preserve process-local causal capture and stable component/phase codes; require a separately controlled incident mechanism for deeper capture.
- **Owner audit-key hashes may still be linkable.** Keep them in protected finite-retention logs, domain-separate/version them, and never use them for authorization or paths.
- **Restart-only configuration slows tuning.** It guarantees one operation/config generation and makes deployment/rollback observable. P15 automates controlled restart.
- **JSON logs can increase bytes.** Enforce finite file/count bounds and measure representative event volume before changing defaults.
- **Resource files can be reparse-linked after validation.** Open through validated handles where practical, keep restart-only snapshots, and avoid repeated path traversal.
- **Legacy data might exist despite the pre-production statement.** Stop before removal if discovered; never auto-delete or auto-migrate it.
- **P14 durable state contains raw commands by necessity.** Protect it with P16/P15 root ACLs and P14 retention; never mirror it into logs. Database encryption is a separate owner decision if host/volume controls are insufficient.

## Open owner decisions

### Decision 1 — Production DataRoot

Choose a required explicit absolute DataRoot or a platform-derived writable default. Recommendation: require an explicit dedicated root in every non-test environment; it makes storage placement, ACLs, capacity, backup, and rollback deliberate and avoids IIS/content-root surprises.

Blocked until decided: Slices 2–3, 5–6, 9 and P14/P15/P17 storage wiring.

### Decision 2 — Secret source

Choose standard out-of-repo .NET configuration providers under the existing hierarchical keys or add a remote secret-manager dependency. Recommendation: use user secrets for Development and P16 promotion-catalog secret references for IIS, with P15 applying only opaque bootstrap references; a later provider can replace the source without changing consumers.

Blocked until decided: Slices 4–5 and P15 production projection.

### Decision 3 — Raw transcript retention

Choose deletion of raw query/context/provider/plan operational transcripts or retain them under stronger controls. Recommendation: delete them; the application handles directory and model data, P13 forbids it in safe logs, and P17 should collect only explicitly consented/versioned feedback data.

Blocked until decided: Slices 7–8.

### Decision 4 — Configuration reload

Choose restart-stable immutable option snapshots or live reload. Recommendation: require controlled restart; live reload can split one provider/compiler/job/storage operation across incompatible policy generations and is not justified before production.

Blocked until decided: Slice 1 and P14 durable-command semantics.

### Decision 5 — Log topology and retention

Choose one console plus one structured rolling file sink, or console-only/external collection. Recommendation: use exactly one console and one JSON file sink capped at 32 MiB with 14 retained files and a five-second flush; it provides bounded local diagnostics without the current duplicate/unbounded behavior.

Blocked until decided: Slices 6–7 and P15 log-directory/collector configuration.

### Decision 6 — Explicit resource failure

Choose fail-closed startup for a configured policy/prompt path that is missing, empty, malformed, or unreadable, or preserve P04's prior silent policy fallback. Recommendation: fail closed for every broken explicit path while retaining built-in defaults only when the path is absent; an operator-specified security source must not silently become a different policy.

Blocked until decided: Slice 4 and P04 durable-guidance/test replacement.

## Advisory Review

### Round 1 — 2026-07-21

**Reviewer:** Headless Claude Code 2.1.217 / configured `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict:** Revisions required

- Resolved policy ownership: P16 validates and opens bounded sources, P04 constructs the canonical immutable policy snapshot/version, and P12 consumes it.
- Explicitly superseded P02's effective-model logging allowance under P16's later safe-event authority and added model-name canary/mutation coverage without changing provider selection.
- Added the P16-owned immutable promotion catalog, shared offline/runtime validator, and opaque P15 adapter; P15 owns target transaction/rollback and never reads values.
- Made `ApplicationConfigurationCatalog` the one code-owned registry, fixed the audit key at full SHA-256, and clarified the intentional one-console/one-file single pipeline.

### Round 2 — 2026-07-21

**Reviewer:** Headless Claude Code 2.1.217 / configured `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict:** Revisions required

- Explicitly superseded P04's configured-file-loading ownership and broken-explicit-path fallback while preserving absent-path built-in defaults; Slice 4 now updates P04's durable guidance and replaces the contradictory characterization guard.
- Split the P16 companion into a reduced-input, non-elevated package-default validator and the protected full promotion validator used by P15 preflight.
- Added a focused no-path/no-secret `ValidatedDirectoryPolicySources` guard.
- Added the P16-owned `ingestion` child for P18; P18 alone owns bounded private spool lifecycle below it and may not use OS temp or P07 staging.

### Round 3 — 2026-07-21

**Reviewer:** Headless Claude Code 2.1.217 / configured `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8` / maximum effort

**Verdict:** Accepted for the reviewed snapshot

- Confirmed all four round-2 targets: explicit configured-file failures, split package/promotion validation, pathless and secretless policy sources, and the P18 ingestion-root boundary.
- Found no material contradiction, missing security constraint, unowned P16 work, unverifiable acceptance criterion, or sequencing defect in that snapshot.
- Applied both optional precision comments after the verdict: bootstrap initializes only already-landed P07 while later plans own their services, and Slice 4 explicitly rewires P04's policy factory to consume validated sources.
- A concurrent P17 reconciliation then added the compile-safe P16-owned feedback fingerprint secret source, active-pair validation, P17 semantic boundary, and no-old-key rotation behavior. This cross-plan addition and the optional precision edits were not re-reviewed. The three-round limit prohibits a fourth round.

Record no more than three headless Claude review rounds. Each round must identify material findings, the resulting revision or retained disagreement, and the reviewer's final assessment. If round 3 requires revisions, apply them, record that the final text was not re-reviewed, and do not run a fourth round.
