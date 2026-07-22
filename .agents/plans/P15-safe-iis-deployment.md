# P15 — Safe, Checked, and Recoverable IIS Deployment

**Status:** Draft — implementation is unauthorized. Owner decisions and prerequisite contracts are unresolved; the three-round advisory review is complete. Round 3 accepted its reviewed snapshot; later P16 terminology plus P14/P20 rollback-admission and package-identity repairs were applied without a fourth round and were not re-reviewed.

## Finding

The current IIS deployment script mutates the live application directory before it has proved that the publish, copy, IIS configuration, startup, or application probes succeeded. It has no recoverable release boundary, no durable transaction record, and no rollback path.

Several independent defects compound that design:

- `-Force` recursively removes every live entry except `logs`, although the repository documentation says the deployed `appsettings.json` contains the runtime LLM secret.
- It terminates every `w3wp` process on the server rather than only the application being deployed.
- PowerShell's terminating-error preference does not make a nonzero native exit code throw. The script does not inspect the exit codes from `dotnet`, `robocopy`, `icacls`, or `appcmd`, so it can proceed after a failed publish, incomplete copy, failed ACL change, or rejected IIS setting.
- App-pool start, authentication changes, and both HTTP checks degrade to warnings. The final line still announces successful completion.
- The live directory is edited in place and no prior content or IIS application snapshot is retained. A partial copy or bad release cannot be reverted automatically.
- Recursive ACL changes grant `IIS_IUSRS` write access to the entire application tree and grant `IUSR` read access even though anonymous authentication is disabled.
- There is no deployment lock, artifact identity, file manifest, content verification, phase journal, or interrupted-deployment recovery rule.
- The script recycles an application whose queued and running jobs are in memory, and merely prints that those jobs will be lost.
- The probe URL is inferred as `http://localhost/<app>`, can be redirected by application middleware, does not prove which release answered, and uses the current combined dependency health endpoint as a warning-only check.

Evidence was verified against repository commit `91bfcbffb09077810d9d7119fecd97544dd87bf5`. The clone was 13 commits ahead of, and not behind, reachable canonical `origin/master` at that check.

### Repository evidence

- `csharp/deploy.ps1:40-45` deletes the local publish directory, runs `dotnet publish`, discards output, does not inspect `$LASTEXITCODE`, and prints “Build completed.” unconditionally.
- `csharp/deploy.ps1:47-60` stops only the named pool first, then enumerates and kills every process named `w3wp`; the empty catch discards kill failures.
- `csharp/deploy.ps1:62-76` creates an arbitrary caller-supplied target or, under `-Force`, recursively deletes every target entry except the `logs` directory. It warns and continues after individual deletion failures.
- `README.md:36` states that the deployed copy beneath `D:\inetpub\adquery` holds the runtime LLM secret, while `csharp/appsettings.json` intentionally carries an empty key. A forced deployment can therefore delete the only configured secret and replace it with an empty default.
- `csharp/deploy.ps1:78-90` ignores `robocopy` and `icacls` exit codes. `robocopy` uses a nonstandard success range and must be interpreted explicitly.
- `csharp/deploy.ps1:90` grants recursive read/write to the broad `IIS_IUSRS` group and read to `IUSR` over binaries, configuration, static content, and logs.
- `csharp/deploy.ps1:92-125` creates or changes the IIS application and runs four `appcmd` mutations without snapshotting the old values. Native failures inside the `try` do not reliably enter `catch`, and the missing-tool path is only a warning.
- `csharp/deploy.ps1:127-136` warns and continues when the pool cannot start.
- `csharp/deploy.ps1:138-169` treats root and `/health` failures as warnings, accepts a root `401` as a warning, and does not validate response identity or content.
- `csharp/deploy.ps1:171-186` always prints “Deployment complete,” even after the warning paths above.
- `csharp/deploy.ps1:180-182` acknowledges that an app-pool recycle loses running and queued in-memory jobs but has no drain or refusal gate.
- `csharp/web.config:8-12` uses in-process IIS hosting and writes ASP.NET Core Module stdout logs beneath the live application directory.
- `csharp/Program.cs:14`, `csharp/appsettings.json:25-30`, and `csharp/web.config:10-11` all target relative `logs` paths, so the application tree currently requires runtime writes and is not an immutable release.
- `csharp/Program.cs:43-46` applies an authenticated role fallback policy. `Program.cs:111-121` enables HTTPS redirection, maps the current combined `/health` endpoint after authorization middleware, and serves the SPA fallback. A fixed localhost HTTP probe is not a stable deployment contract.
- `csharp/Program.cs:74-76` registers external Claude and internal plan-validation health checks together. `ClaudeHealthCheck.cs:19-60` contacts the provider and can expose raw exception details in health data. P20 owns their separation and sanitization.
- `README.md:56-63` and `csharp/README.md:138-149` instruct an administrator to run `deploy.ps1 -Force` and describe an in-place mirror/recycle without warning that successful rollback is impossible.
- `.gitignore:3` ignores `csharp/publish`, so the current deployment has no checked-in or retained artifact manifest.
- P01 explicitly reserves P15's ability to add a pinned Pester suite to the one canonical verification command and requires default tests to avoid IIS, credentials, Active Directory, and external providers.

### Platform constraints

- The application is framework-dependent and hosted in-process. The IIS server therefore requires a compatible ASP.NET Core Hosting Bundle and ASP.NET Core Module; P03 owns the target framework/runtime choice. Microsoft recommends framework-dependent deployment for most IIS deployments where the Hosting Bundle provides the runtime: <https://learn.microsoft.com/en-us/aspnet/core/tutorials/publish-to-iis>.
- ASP.NET Core Module can gracefully stop an application through `app_offline.htm`, and live application files may be locked. P15 avoids overwriting those files by using side-by-side releases and a target-pool cutover; it never kills unrelated workers: <https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/app-offline>.

## Admitted Findings

Each finding below predicts an observable failure and maps to exactly one implementation commit. If implementation reveals that one finding cannot be completed atomically, stop and revise this plan before splitting or batching it.

| ID | Severity | Evidence and predicted observable failure |
|---|---|---|
| P15-F0 | MEDIUM | No automated deployment guard exists. A script regression can compile the C# project and still erase, misroute, or falsely report a deployment. |
| P15-F1 | HIGH | Unchecked native exit codes at `deploy.ps1:44,80,90,118` allow a failed build/copy/configuration command to proceed and print success. |
| P15-F2 | HIGH | Arbitrary `TargetPath` plus recursive `-Force` cleanup at `deploy.ps1:62-76` can erase configuration, secrets, or an accidentally broad directory before a replacement is viable. |
| P15-F3 | HIGH | Recursive `IIS_IUSRS` write and `IUSR` read at `deploy.ps1:90` let unrelated pools modify release binaries and let an anonymous identity read deployed configuration. |
| P15-F4 | HIGH | Killing all `w3wp` processes at `deploy.ps1:56-60` can interrupt unrelated IIS applications on the same server. |
| P15-F5 | HIGH | Recycling without a drain at `deploy.ps1:180-182` loses accepted queued/running jobs with no checked refusal or terminal-state contract. |
| P15-F6 | HIGH | In-place mutation and no prior snapshot mean any post-mutation fault can leave the application unavailable with no deterministic rollback. |
| P15-F7 | HIGH | Warning-only pool/probe failures at `deploy.ps1:127-169` allow a dead, unauthorized, redirected, dependency-broken, or old process to be announced as deployed. |
| P15-F8 | MEDIUM | No package identity, hash manifest, deployment lock, or duplicate-release rule allows concurrent or tampered/mixed artifacts to become live. |
| P15-F9 | MEDIUM | The documented `-Force` workflow directs operators to the unsafe entry point and makes the risky path the normal path. |
| P15-F10 | HIGH | After the new pool starts, P14 is traffic-ready and may accept jobs during P20's multi-observation probe window, but the recorded rollback stops that pool before a new-release drain. A late probe failure can therefore interrupt accepted work. |

## Desired Outcome

- A non-elevated packaging command verifies committed source and emits one immutable, content-addressed deployment package plus a required SHA-256 value.
- An elevated deployment command accepts only that package, validates every prerequisite before downtime, and never builds from the live server workspace.
- Releases are extracted and hash-verified in a new side-by-side directory. The live directory is never recursively cleaned or overwritten.
- The IIS application must already exist, use a dedicated existing app pool, and match the expected Windows/anonymous authentication policy. Deployment validates infrastructure but does not silently provision or reconfigure it.
- P14 closes admission and proves the application has drained before P15 stops the target pool. No `w3wp` process is killed.
- One exclusive per-application deployment lock prevents concurrent mutation.
- A durable phase journal and exact IIS snapshot exist before cutover.
- Cutover changes only the target application's physical path and exact P16 environment-reference projection, preserves its app-pool assignment and authentication policy, and starts only the target pool.
- P20 liveness, readiness, and release-identity probes are hard gates. Success is published only after all required probes pass.
- Any failure after mutation attempts to restore the exact prior physical path, P16 promotion ID, and pool state. Once the new pool could have admitted work, rollback first drains that exact release; inability to prove quiescence leaves `RecoveryRequired` without stopping or switching it automatically.
- Release binaries and checked-in defaults are read/execute-only to the application identity. P16 supplies one versioned production-promotion projection for typed configuration, secret-source references, the portable data root, the sole Serilog pipeline, and required ACLs; P15 applies and verifies that projection without creating another configuration model.
- Every phase, failure, rollback, and committed release has a bounded secret-free operator record. No output path, response body, configuration value, credential, or raw exception is emitted to user-visible telemetry.

## Scope

### Included

- PowerShell deployment test foundation and deterministic fake adapters.
- Checked package creation from a clean committed revision.
- Package/manifest/hash validation and safe archive extraction.
- Canonical target identity, path containment, reparse-point, and duplicate-release checks.
- A side-by-side release directory and explicit deployment state directory.
- Per-application deployment locking.
- Existing-IIS preflight and a target-only IIS adapter.
- P14 drain handoff and bounded pool-state waits.
- A second exact-release P14 drain before any rollback stop after the new pool could have admitted work.
- P16 production-promotion, immutable-release, IIS environment-injection, and least-privilege filesystem contracts.
- Journaled physical-path/P16-promotion cutover, rollback, and interrupted-run recovery.
- P20 liveness/readiness/release-identity gates.
- Safe operator summaries, fixed exit classes, opt-in disposable-IIS verification, and documentation.
- Explicit old-release pruning as a separate command after a successful deployment.

### Excluded

- Provisioning Windows Server, the IIS role, a website, TLS certificates/bindings, DNS, service accounts, or an app pool.
- Installing or upgrading the .NET runtime, ASP.NET Core Hosting Bundle, or ASP.NET Core Module; P03 selects the runtime and server administrators install it.
- CI/CD provider selection, artifact hosting, branch protection, production credentials, or remote deployment transport.
- Inventing a secret store, portable path scheme, logging sink, data retention policy, or runtime ACL model; P16 owns those contracts.
- Job state-machine, drain, cancellation, shutdown, or recovery semantics; P14 owns them.
- Liveness/readiness/diagnostic endpoint behavior, exposure, and reason codes; P20 owns them.
- Database migration orchestration. This application currently has no database migration step; any future migration requires a separate reviewed deployment design.
- Zero-downtime multi-instance or blue/green traffic routing. This plan targets one dedicated IIS application pool with bounded maintenance downtime.
- Automatic deletion of arbitrary legacy directories, the current rollback release, deployment journals, configuration, logs, output, or failed releases.

## Dependencies and Ownership Boundaries

### P01 — verification foundation

P01 must land before P15 implementation. P15 adds a pinned PowerShell test stage to P01's one repository-root verifier; it does not create a competing verification command.

Use Pester `5.7.1` exactly for deterministic deployment tests. Record that required version in a repository dependency manifest and import it with `-RequiredVersion`; the verifier fails clearly when it is absent and never installs modules implicitly. CI or machine provisioning may install the pinned module outside the verification run.

Default verification runs under `pwsh`, requires no elevation, and uses fake filesystem/IIS/drain/probe/native-command adapters. It never imports live IIS state, changes ACLs, opens network connections, or contacts LDAP/LLM services.

### P03 — runtime modernization

P03 owns the target framework, framework-dependent versus self-contained choice, package/runtime upgrades, and SDK pin. P15 recommends preserving framework-dependent IIS publish and reads the finalized target framework/runtime identifiers into the artifact manifest.

P15 preflight verifies that the server has the compatible shared runtime and ASP.NET Core Module V2 required by the manifest. It reports fixed missing-prerequisite codes and never installs or repairs the Hosting Bundle during deployment.

### P14 — jobs, drain, and host shutdown

P14 owns:

- closing application admission;
- reporting queued/running work;
- bounded drain or policy-approved cancellation;
- terminal job outcomes across host stop;
- host-shutdown coordination.

P15 invokes P14's authenticated, bounded operator drain contract and requires a response that identifies the currently serving release and proves `accepting_work = false`, `queued = 0`, `running = 0`, and `quiescent = true`. It uses this once for the old release before cutover and again for the exact new release before any rollback stop after that pool could have admitted work. P15 does not inspect or mutate job memory directly. If the old release cannot quiesce, deployment stops before IIS mutation and returns it to normal admission when P14 permits. If the new release cannot quiesce during rollback, P15 records `RecoveryRequired` and does not stop the pool or switch path/configuration automatically.

The production cutover slices cannot land until P14's exact operator contract is approved and implemented. There is no `-AllowJobLoss`, `-ForceDrain`, or silent recycle fallback.

### P16 — configuration, storage, and logging

P16 owns:

- the typed options/configuration schema and startup validation;
- secret-source selection and validation;
- the one Serilog configuration/pipeline and its redaction rules;
- the portable `DataRoot` and P16-defined typed child roots;
- operational-log retention and the logical root/ACL envelope, while each domain plan owns its store retention; and
- a versioned production-promotion projection describing the approved IIS environment injection, secret references, paths, and application-identity ACL requirements.

P15 owns only the mechanics of applying, verifying, snapshotting, and rolling back that P16 projection during release promotion. It does not define a second key set, environment naming convention, secret store, data root, log sink, redaction policy, or configuration validator.

Before P15 may switch IIS to an immutable release:

- no secret may exist only inside the current application directory;
- the production configuration source must survive physical-path changes;
- Serilog and ASP.NET Core Module stdout logs must write outside release directories or be disabled under P16's policy;
- query artifacts, ingestion spools, feedback, and other mutable state must use P16-owned typed child paths;
- P16 must identify exactly which external directories the app-pool identity may write; and
- P16 must expose an offline promotion validator that evaluates the new release against the selected typed projection and secret sources and returns only a fixed success/failure code plus projection ID/hash; P15 receives no secret value.

P15 packages only non-secret defaults and asks P16's authoritative catalog/validator to prove every secret-backed field is empty. It consumes a P16-owned projection ID/hash, applies its exact IIS environment-reference and ACL plan through a narrow adapter, and invokes P16's validator before promotion. Protected projections remain in P16's state so rollback can restore the prior `Applied` ID or `Absent` state without journaling values. P15 never copies, merges, preserves, prints, or backs up a secret-bearing `appsettings` file.

Landing order is coordinated rather than duplicated: P15's test/path primitives may land first, P16 then lands the typed promotion/package-validation contract, and P15's package, ACL, cutover, and rollback slices bind to that authoritative contract. If P16 changes type names or transport, P15 adapts its consumer; it does not recreate equivalent configuration logic.

### P20 — health and deployment probes

P20 owns separate, bounded liveness, readiness, and diagnostics endpoints; authentication/exposure; cached dependency state; response schema; and sanitized reason codes.

P15 consumes:

- a liveness probe proving the new process is running;
- a readiness probe proving the instance is eligible to serve normal work;
- a fixed release identity returned by the probe contract on both ready and unready responses.

P20's deployment response is bounded and parseable on both `200` and `503`, always carries the immutable serving release ID plus truthful liveness/traffic-readiness state, and never redefines readiness as merely “safe to probe before admission.” P15 uses that identity to target rollback drain. An unavailable, malformed, or wrong-release deployment response cannot prove rollback authority and therefore yields `RecoveryRequired` once admission was possible. P15 does not parse today's combined `/health` body or create another health endpoint. The final probe-gated cutover slice cannot land until P20's contract is approved and implemented.

Release identity has one producer/consumer boundary. P20-F4 owns the project-level `AssemblyMetadata` emission and the application's strict startup parser. P15-F8/Slice 3 owns generating the canonical ID before publish, passing it as the checked MSBuild property `AdQueryReleaseId`, reading the published entry assembly's metadata offline without loading or executing it, and requiring exactly one matching value before the manifest/archive is accepted. P15 does not add a second runtime identity source or parser.

### IIS infrastructure and operator ownership

Server administration owns the website, dedicated app pool, identity, bindings/TLS, Windows Authentication installation, and production probe credential. P15 validates their desired state and fails before downtime on drift. It does not create a missing application, change an app-pool identity, enable a server role, alter bindings, or broadly restore `applicationHost.config`.

P15 snapshots only fields it may restore: application physical path, P16 promotion ID/hash, and initial pool state. Windows/anonymous authentication and app-pool assignment are validate-only invariants, not deployment mutations.

## Owner Decisions Required

### P15-D1 — release topology

**Recommendation:** Replace in-place mirroring with immutable side-by-side releases and switch only the IIS application's physical path. Use an explicit release root such as `D:\inetpub\adquery-releases`; keep deployment state under an ACL-restricted path such as `C:\ProgramData\ADQuery\deployment`.

The alternative—backup then overwrite one live directory—retains locked-file, partial-copy, secret-preservation, and rollback ambiguity. It is not recommended.

Blocks: P15-F2, F6, F8, and final cutover.

### P15-D2 — IIS infrastructure policy

**Recommendation:** Require the IIS site, application, and dedicated app pool to exist and validate their assignment/authentication settings without mutating them. Keep first-time IIS provisioning as a separate administrator runbook, not a mode of the release deployment command.

Automatic bootstrap broadens rollback to server infrastructure and makes a typo capable of creating or changing the wrong application.

Blocks: IIS adapter and preflight slices.

### P15-D3 — production configuration migration

**Recommendation:** Require P16 to externalize secrets, establish typed startup/secret-source validation, define the sole Serilog pipeline and portable `DataRoot`, and emit a versioned production-promotion projection before P15's production cutover. P15 applies that projection transactionally; it never preserves or merges a secret-bearing file from the old release directory.

File preservation avoids immediate key loss but perpetuates an unversioned mutable release and can silently retain obsolete schema. External configuration makes both cutover and rollback deterministic.

Blocks: immutable release, ACL, cutover, and rollback slices.

### P15-D4 — accepted-work policy

**Recommendation:** Require a successful exact-release P14 drain before every target-pool stop that could interrupt accepted work: once for the old release before cutover and, if the new pool ever reached a state where admission was possible, again before rollback. If old-release quiescence cannot be proved within 120 seconds, abort before mutation and keep it serving; if new-release quiescence cannot be proved, enter `RecoveryRequired` without stopping or switching it. P15 owns this deployment wait ceiling and clamps it to any shorter P14-reported bound; P14 continues to own drain semantics and state.

The alternative intentionally loses accepted work and needs a user-visible retry/recovery contract that does not exist.

Blocks: drain, cutover, probe, and rollback-quiescence slices.

### P15-D5 — post-cutover gate

**Recommendation:** Require liveness and readiness carrying the expected release ID to pass three consecutive times, two seconds apart, within a 60-second overall monotonic deadline. Do not follow redirects, bypass TLS validation, accept warning statuses, or accept a response from another release.

One success is vulnerable to startup races; an unbounded wait leaves the app unavailable. P20 owns endpoint semantics, not these deployment retry parameters.

Blocks: probe and commit slices.

### P15-D6 — artifact trust

**Recommendation:** Initially require a clean committed revision, a complete per-file SHA-256 manifest, and an explicit expected package SHA-256 supplied to the elevated command through a separate operator step. State plainly that hashes provide integrity, not signer identity; add signing only after an approved certificate/attestation design exists.

Accepting an adjacent sidecar automatically lets an attacker replace both package and hash. Requiring signing now would invent certificate ownership and distribution that the repository does not have.

Blocks: package and preflight slices.

### P15-D7 — retained releases

**Recommendation:** Keep the current release and two prior committed releases. Prune only through a separate `Remove-AdQueryRelease` command with `-WhatIf`/confirmation, containment and manifest checks, and refusal to remove current, rollback, failed, legacy-external, or journal-referenced paths.

Automatic cleanup inside a successful deployment adds a new destructive failure after cutover. Unlimited retention creates avoidable disk pressure.

Blocks: retention tooling only; it does not block safe cutover.

**All decisions must be recorded and this plan changed to `Approved` before implementation begins.**

## Safety and Correctness Invariants

1. Packaging is non-elevated; deployment is elevated. No compile or restore occurs in the elevated deployment process.
2. The packaging source is a clean committed revision. The exact 40-character commit ID is recorded.
3. The package contains no production secret, environment-specific configuration, logs, output, feedback, deployment state, or prior release.
4. Every packaged file has one normalized relative path, finite length, byte length, and SHA-256 hash.
5. Archive entries containing absolute paths, drive/UNC prefixes, `..`, alternate separators, case-insensitive collisions, links/reparse points, duplicate names, or unmanifested files are rejected before extraction.
6. The caller supplies the expected package SHA-256 explicitly. A package mismatch performs no target mutation.
7. All native commands use one checked invocation wrapper. Unexpected exit codes are terminating failures with bounded fixed diagnostics.
8. The canonical release root and state root are explicit, non-root, non-overlapping, non-reparse directories carrying a matching deployment-target marker.
9. Every derived staging, release, journal, and prune path is proven contained beneath its canonical owner root before creation, move, or deletion.
10. No recursive delete targets a caller-supplied path, live physical path, current release, rollback release, root, source tree, configuration path, logs, output, or deployment-state root.
11. Exactly one deployment lock is held from preflight snapshot through commit or completed rollback.
12. The package is fully extracted and re-hashed before the release directory becomes eligible for IIS.
13. A release directory is immutable after validation. Deployment never overlays or repairs a directory with the same release ID.
14. A second package with an existing release ID succeeds only as an idempotent no-op when every manifest byte/hash and committed journal already match; otherwise it fails closed.
15. IIS site, application, dedicated app pool, app-pool assignment, bindings used by the explicit probe URI, and authentication policy are validated before drain.
16. One app pool is dedicated to this IIS application. A shared pool is a preflight failure.
17. P15 never enumerates, kills, suspends, or recycles arbitrary `w3wp` processes.
18. No IIS mutation occurs until P14 has closed admission and proved zero queued/running work.
19. Pool stop/start affects only the configured pool and has a monotonic timeout. Timeout is failure, never a sleep-and-assume-success path.
20. The pre-mutation journal records the exact old physical path, old release identity when known, initial pool state, old P16 promotion state (`Applied` ID/hash or `Absent`), new P16 promotion ID/hash, new release identity, and transaction phase; it records no projection values.
21. The only normal cutover mutations are the target application's physical path, the exact P16-owned IIS environment projection, and the target pool's required stop/start transition.
22. If the pool was not running at preflight, an update deployment refuses to proceed; first installation or intentionally stopped maintenance uses a separate administrator procedure.
23. No success is committed until P20 liveness/readiness return the expected release identity for the required consecutive observations.
24. Redirects, authentication failures, TLS failures, timeouts, malformed bodies, mismatched release IDs, degraded/unready states, or dependency failures are probe failures.
25. Every failure after the first live mutation attempts rollback exactly once through the recorded snapshot, subject to the no-work-loss gate below.
26. Rollback never restores global IIS configuration. Through the same target-application commit boundary used for cutover, it restores the target physical path and the recorded prior P16 promotion state, then restores the target pool state. `Applied` reapplies the prior ID; `Absent` removes exactly the P16-owned environment-reference entries introduced by the new projection. Rollback is not verified until path, promotion state, pool state, and required old-release liveness all read back successfully.
27. If the current target path/promotion pair equals neither the complete recorded old pair nor the complete recorded new pair during recovery, automation stops without overwriting the external change.
28. Failed or rolled-back new releases remain for diagnosis and are never selected as a future rollback target automatically.
29. A rollback failure returns a distinct nonzero outcome, preserves the journal, and prints bounded manual recovery identifiers without claiming completion.
30. Once the durable journal reached `PoolStartRequested` or the new pool was observed starting/running, rollback may stop that pool or restore the old path/projection only after P14 proves the exact new release is non-accepting and quiescent. Unavailable/malformed/wrong-release identity, drain timeout, or a stopped/unreachable process whose prior admission cannot be disproved records `RecoveryRequired` with no further live mutation.
31. Releases grant the app-pool identity read/execute only. Broad groups and `IUSR` receive no P15 grant.
32. Write permission is granted only to P16-named external mutable directories and only to the actual configured app-pool identity.
33. P15 treats the P16 promotion projection as opaque, validates only its closed envelope/ID/hash through P16, and never persists or logs its values.
34. Logs, console output, journals, metrics, and probes contain no configuration values, secrets, response bodies, credentials, user queries, or raw exception messages.
35. The current unsafe `-Force` semantics do not remain as an alias, compatibility mode, or hidden fallback.
36. Every deterministic test runs without administrator rights, IIS, network, wall-clock sleep, or production paths.

## Target and Tool Contract

### Repository layout

Add the following implementation surface after P01 lands:

```text
scripts/
  New-AdQueryDeploymentPackage.ps1
  Initialize-AdQueryDeploymentTarget.ps1
  Invoke-AdQueryIisDeployment.ps1
  Remove-AdQueryRelease.ps1
  deployment/
    AdQuery.Deployment.psd1
    AdQuery.Deployment.psm1
    required-modules.psd1
tests/
  Deployment/
    Package.Tests.ps1
    Paths.Tests.ps1
    Lock.Tests.ps1
    IisScope.Tests.ps1
    Drain.Tests.ps1
    Transaction.Tests.ps1
    Probes.Tests.ps1
    Permissions.Tests.ps1
    Architecture.Tests.ps1
```

`required-modules.psd1` records Pester `5.7.1` for verification and `IISAdministration` `1.1.0.0` for the elevated target adapter. Test code imports only the deployment module and fake adapter; it does not import `IISAdministration`.

All public scripts require PowerShell 7.4 or newer, use strict mode, set terminating PowerShell errors, restore caller location in `finally`, expose `SupportsShouldProcess`, and return process-level fixed outcomes. Do not load `WebAdministration` through a Windows PowerShell compatibility session and do not mix `IISAdministration`, `WebAdministration`, and `appcmd` mutation paths.

### Required parameters

The elevated deployment command has no destructive implicit target. Require explicit values for:

- `PackagePath`;
- `ExpectedPackageSha256` as exactly 64 hexadecimal characters;
- `SiteName`;
- `ApplicationPath` in normalized IIS form;
- `AppPoolName`;
- `ReleaseRoot`;
- `StateRoot`;
- `ProbeBaseUri`;
- P16 `PromotionId` (the protected promotion store and schema remain P16-owned);
- drain and probe credential mode from the approved P14/P20 contracts.

`Initialize-AdQueryDeploymentTarget.ps1` is a separate one-time command. It validates all names and canonical paths, requires the IIS objects to exist/match, creates only the release/state directory structure, applies administrator-only state ACLs, and writes a marker containing schema version plus a random instance ID and the expected site/application/pool/path tuple. It never creates or changes IIS objects.

Every later command requires that marker and exact tuple. `-WhatIf` performs all safe reads and package validation but acquires no mutating lock, writes no marker/journal/release, drains nothing, changes no pool, and sends no state-changing request.

### Narrow adapters

Keep orchestration pure by injecting narrow operation sets:

```text
IDeploymentFileSystem
  canonicalize, inspect attributes/ACLs, create, extract, hash, atomic move, delete owned staging

IIisDeploymentAdapter
  snapshot application/pool/policy, validate dedicated pool/binding,
  stop/start target pool, wait state, set target physical path

IDrainClient
  begin exact-release drain, await non-accepting quiescence,
  resume-old-on-precutover-abort

IDeploymentProbeClient
  bounded deployment envelope on 200/503: liveness, traffic readiness,
  immutable serving release identity

IProductionPromotionAdapter
  load P16 projection envelope by ID, validate through P16, plan/apply/read-back
  approved IIS environment references and filesystem ACLs, restore prior projection

IDeploymentClock
  monotonic timestamp and bounded delay

INativeCommandRunner
  executable, literal argument array, allowed exit codes, bounded safe result
```

These may be PowerShell classes or internal module functions accepting operation scriptblocks; names may follow repository conventions. The contract matters: production adapters are selected only by the elevated entry point, while Pester passes deterministic fakes.

`IIisDeploymentAdapter` uses the pinned `IISAdministration` module and one `ServerManager`/commit boundary per snapshot or mutation. Dispose/reset its manager after commit. It never exposes a live IIS object to orchestration or performs an unrecorded mutation.

## Package Contract

### Build and publish

`New-AdQueryDeploymentPackage.ps1`:

1. Resolves repository root from `$PSScriptRoot` and requires a clean tracked and untracked worktree except ignored artifact outputs.
2. Reads the exact `HEAD` commit and refuses a symbolic/unborn/dirty source.
3. Invokes P01's canonical verification command and requires exit zero.
4. Generates the canonical release ID, then publishes the application framework-dependently to a unique temporary directory using the P03-approved SDK/target, the checked native runner, and the explicit MSBuild property `AdQueryReleaseId=<release_id>`.
5. Requires the primary application DLL, `.deps.json`, `.runtimeconfig.json`, `web.config`, static assets, and configuration allow-list/prompt files expected by the project. A bounded offline PE/metadata reader must find exactly one `AssemblyMetadata` entry named `AdQueryReleaseId` in the entry assembly and require its value to equal the generated ID; missing, duplicate, malformed, or mismatched metadata fails before manifest/archive creation, and the assembly is never loaded or executed for this check.
6. Invokes P16's package-default validator and rejects any nonempty field in P16's authoritative secret-source catalog, environment-specific `appsettings.Production.json`, P16-owned mutable child directory, deployment state, nested archive, reparse point, or file outside the publish output. P15 carries no duplicate secret-field or child-name list.
7. Sorts normalized relative paths ordinally and writes one canonical UTF-8 manifest.
8. Creates a ZIP at a temporary output name, computes its SHA-256, then atomically renames the package and writes a non-secret build summary.
9. Removes its owned temporary directory in `finally`; cleanup failure makes packaging fail and is never mistaken for a valid package.

### Manifest

Use a versioned closed schema such as:

```json
{
  "schema_version": 1,
  "release_id": "20260721T220000Z-91bfcbffb090",
  "source_commit": "91bfcbffb09077810d9d7119fecd97544dd87bf5",
  "target_framework": "net10.0-windows",
  "deployment_mode": "framework-dependent",
  "entry_assembly": "AdQueryOrchestrator.dll",
  "files": [
    { "path": "AdQueryOrchestrator.dll", "length": 1, "sha256": "<64 hex>" }
  ]
}
```

The target framework example follows P03's planned modernization; implementation reads the landed project and does not hardcode this example. `release_id` is generated by the packaging tool from UTC timestamp plus the commit prefix, must match `^[0-9]{8}T[0-9]{6}Z-[0-9a-f]{12}$`, and is never accepted from arbitrary user input. The manifest value is written only after the published entry assembly proves the same exact metadata value.

Set finite package limits in the module: 1 GiB compressed/uncompressed total, 20,000 entries, and the platform's supported canonical path rules. Validate checked arithmetic before allocation or extraction. Owner approval of P15-D6 approves these conservative safety values; tune them only from measured packages in a later reviewed change.

### Validation and extraction

Before target mutation:

1. Stream-hash the package and constant-time compare the normalized value with `ExpectedPackageSha256`.
2. Read the manifest with strict duplicate-key rejection and closed schema validation.
3. Enumerate archive metadata without extraction; validate count, sizes, normalized paths, duplicates/case collisions, and exact manifest membership.
4. Create one transaction-owned staging directory under the marked release root.
5. Extract each accepted regular file without following links.
6. Re-hash every extracted file and reject missing, extra, length-mismatched, or hash-mismatched content.
7. Apply immutable-release ACLs.
8. Atomically rename staging to `releases/<release_id>` on the same volume.

No release directory is reused. On a pre-cutover failure, remove only the transaction's validated staging directory; retain a completed release directory for a retry with the exact same verified package.

## Preflight and Locking

Preflight is ordered to fail before downtime:

1. Validate PowerShell, elevation, required module versions, native tools, package/hash/manifest, and target marker.
2. Canonicalize paths and reject roots, overlaps, reparse points, source/publish/current/config/state paths, and paths outside the marker.
3. Acquire an exclusive file handle with `FileShare.None` on the fixed state-root lock file. Hold the handle through commit or completed rollback; process exit releases it without stale-lock guessing.
4. Refuse a nonterminal journal unless invoked in explicit recovery mode.
5. Verify free space for extracted content plus one bounded reserve; use checked sizes from the manifest.
6. Snapshot IIS and require the named site/application/pool, exact app-pool assignment, dedicated pool, running initial state, expected authentication, and a probe URI matching a configured binding.
7. Verify the target runtime and ASP.NET Core Module match the artifact contract.
8. Resolve the P16 production-promotion projection by ID, verify its hash/closed envelope, run P16's offline typed configuration and secret-source validation against the new release, and prove its `DataRoot`/mutable directories are not beneath either old or new release. Snapshot the prior P16 promotion state as `Applied` ID/hash or `Absent`; `Absent` is permitted only when no P16-owned or colliding unmanaged environment entry exists. A colliding unmanaged entry is an existing IIS environment key in P16's owned-key set whose provenance is not represented by a P16 promotion; P16's adapter classifies it without exposing the value to P15.
9. Probe the old release through P20 and require a stable readiness baseline plus its release identity when known. A legacy first migration may lack a P20 release identity; the journal then records that identity as unknown and rollback verifies path, prior P16 promotion state, pool state, and liveness instead.
10. Stage, verify, ACL, and finalize the new release without changing IIS.
11. Write and durably flush the `Prepared` transaction journal before invoking P14 drain.

Every preflight failure leaves the old application untouched and returns a fixed nonzero category. No `-Force` parameter bypasses a failed invariant.

## Deployment Transaction

### Durable state

Write a versioned JSON journal through write-temp, flush, and atomic replace. It contains only:

- schema/transaction/release/source identifiers;
- site, normalized application path, pool, canonical old/new paths;
- old release identity if available;
- old P16 promotion state as either `Applied` with ID/hash or `Absent`, plus the new P16 promotion ID/hash, never projection values;
- initial pool state;
- manifest/package hashes;
- monotonic phase sequence plus UTC audit timestamps;
- old/new P14 drain generations and quiescence observations, never bearer tokens;
- fixed outcome/failure/rollback codes;
- probe attempt counts and status classes, never bodies.

Phases are monotonic:

```text
Prepared
  -> Draining
  -> Quiescent
  -> PoolStopped
  -> PathSwitched
  -> PoolStartRequested
  -> PoolStarted
  -> Probing
  -> Committed

Any post-mutation failure:
  -> RollingBack
  -> RollbackQuiescent (required if new-release admission was possible)
  -> RolledBack | RecoveryRequired
```

Journal the intent before each live mutation and the observed result after it. Flush each transition. Never infer completion from the presence of a release directory alone.

### Drain and stop

1. Call P14 to close admission for the old release.
2. Poll P14 with the injected monotonic clock until queued and running counts are both zero or 120 seconds expires.
3. Revalidate the IIS snapshot and lock ownership immediately before stop; abort if external state changed.
4. Stop only the configured dedicated pool through `IIisDeploymentAdapter`.
5. Poll the pool state to `Stopped` within the approved bound. Never enumerate or kill processes.

If drain or stop fails before the physical path changes, ask P14 to resume the old release when its contract permits, restore the initial pool state if P15 changed it, mark the journal with the fixed pre-cutover failure, and exit nonzero.

### Cutover and probes

1. In one target-application commit boundary, change the physical path from the journaled old path to the verified new release and apply the exact P16-owned IIS environment-reference projection.
2. Re-read IIS state and require exact equality with the new path and P16 projection hash plus unchanged pool assignment/authentication policy.
3. Flush `PoolStartRequested` before asking IIS to start only the configured pool, then wait for `Started` within a monotonic bound and flush `PoolStarted`.
4. Probe the explicit URI without redirects or TLS bypass. Parse P20's bounded deployment contract on both `200` and `503`, require the exact new `release_id`, and treat only truthful traffic-readiness success as a qualifying observation.
5. Require three consecutive successful observations two seconds apart within 60 seconds; reset the consecutive count on any permitted transient non-success, but never extend the absolute deadline.
6. Revalidate IIS physical path and release identity, then atomically record `Committed` and update the separate last-known-good pointer.

Only `Committed` returns success. The command prints a bounded summary containing release/transaction IDs and fixed phase/outcome codes, not response bodies or configuration.

## Rollback and Crash Recovery

### Automatic rollback

For a failure at or after `PathSwitched`:

1. Transition and flush `RollingBack` exactly once.
2. Determine from both the durable journal and current IIS state whether new-release admission was ever possible. A journal state at or beyond `PoolStartRequested`, or a target pool observed `Started`/`Starting` against the new path, requires the rollback drain; only proof that no start was requested and the new pool never ran permits the drain to be skipped. This deliberately treats a crash or timeout after the start request but before the started observation as admission-possible.
3. When required, read P20's deployment contract and require the exact new release ID even if it returns `503`; call P14 `BeginDrain(expected new release ID)` and poll through the same absolute drain bound until `accepting_work=false`, `queued=0`, `running=0`, and `quiescent=true`. Flush `RollbackQuiescent` only after that proof. An unavailable/malformed/wrong-ID deployment response, drain failure/timeout, or a stopped/unreachable pool whose earlier admission cannot be disproved records `RecoveryRequired` and performs no pool stop, path switch, or projection change.
4. Stop only the target pool after the required quiescence proof (or the never-started proof); wait within the same bounded pool-state policy.
5. Re-read the complete target path/promotion pair. Only when it equals the recorded new pair, restore the recorded old physical path and prior P16 promotion state through the same target commit boundary: reapply the old ID for `Applied`, or remove exactly the P16-owned entries introduced at cutover for `Absent`. Only when it already equals the complete recorded old pair, continue idempotently. For every other combination—including an old-path/new-projection or new-path/old-projection mixed pair—record `RecoveryRequired` and do not overwrite an external change.
6. Restore the initial target-pool state. Normal updates require it to have been running, so start and wait.
7. Probe the old release's P20 liveness and expected old release identity when known. Readiness failure is reported separately because an external dependency outage may affect both releases.
8. Read back the exact old path, prior P16 promotion state (`Applied` or `Absent`), and initial pool state; record `RolledBack` only when all three plus required rollback liveness are proven.
9. Exit nonzero even after a successful rollback; deployment did not succeed.

Do not delete the failed new release or its journal. Do not roll back ACLs on an immutable unused release. Do not restore an `applicationHost.config` backup.

### Interrupted deployment

On the next invocation, a nonterminal journal blocks normal deployment. `Invoke-AdQueryIisDeployment.ps1 -Recover` acquires the same lock, validates the journal/marker, snapshots current IIS state, and performs only the deterministic rollback algorithm above.

There is no automatic “resume forward” path. If state matches neither recorded path, a manifest/hash no longer matches, or the required old release is absent, recovery stops with a fixed manual-intervention code and the journal remains. The operator receives exact transaction/release identifiers and safe paths from the protected local journal, never guessed commands against a broad directory.

## Least-Privilege Filesystem Policy

P15 consumes the actual configured app-pool identity from IIS. It does not assume `IIS_IUSRS`, `IUSR`, or `IIS AppPool\<name>` when the pool uses a custom identity.

- Release root/state administration: local Administrators and SYSTEM full control; application identity has no state-root access. P16's protected promotion store is outside releases and governed by P16.
- Individual immutable release: application identity read/execute only; no create/write/delete/owner/ACL rights.
- P16 `DataRoot` children: only the exact rights in P16's ACL projection, never inherited onto release or configuration.
- Production configuration/secret source: only P16-defined access for the application identity; P15 applies references and validates the projection result without copying or reading values.
- Disable inherited broad grants when the approved host policy permits; otherwise preflight fails and reports the unexpected principal/right without silently weakening the requirement.

Represent desired ACL changes as a pure plan first. Pester compares the plan to fake current ACLs. The production adapter applies only the validated delta and reads it back before cutover. No recursive grant starts at a caller-supplied root.

## Failure and Exit Contract

Use one internal result type with phase, fixed code, mutation-started flag, rollback-attempted flag, and rollback outcome. Raw native/IIS/HTTP exceptions remain local diagnostic causes and are never serialized into the journal or standard console summary.

Recommended process exit classes:

```text
0   committed and verified
10  package or source verification failed; no live mutation
11  target/IIS/runtime/configuration preflight failed; no live mutation
12  deployment lock or recovery-required state blocked execution
20  drain or pre-cutover stop failed; old path retained
30  cutover/start/probe failed; rollback verified
40  rollback or recovery could not be verified; manual intervention required
```

An unexpected exception is mapped by the phase into one of these nonzero classes after rollback rules run. No warning path returns zero. Release pruning is a separate command and cannot change a completed deployment's result.

## Implementation Slices and Commits

Each slice closes exactly one admitted finding, receives its own guard proof, and is committed before the next starts. Do not amend, squash, combine, or leave a completed slice uncommitted.

The production cutover entry remains fail-closed until Slices 7–10 have all landed. Those commits stay independently testable behind that gate; no intermediate Slice 8/9 workflow may start a new pool without Slice 10's rollback-quiescence protection.

### Slice 1 — P15-F0 deterministic deployment verification

**Commit:** `test(deploy): establish deterministic deployment guards`

- Add the deployment module skeleton, closed result/state types, fake adapter seams, pinned module manifest, and Pester test directory.
- Extend P01's canonical verifier with exact Pester `5.7.1` invocation.
- Add a test proving no default suite imports IIS, requires elevation, sleeps, or uses network/production paths.
- Do not change the production deployment entry point yet.

Guard proof: temporarily point the canonical Pester stage at an empty deployment-test fixture; confirm the verifier exits nonzero because zero deployment tests were discovered, restore the real path, and run canonical verification green.

### Slice 2 — P15-F1 checked package/native execution

**Commit:** `fix(deploy): fail on package command errors`

- Add the checked native runner and non-elevated package builder.
- Require clean committed source, canonical verification, checked publish, required outputs, and atomic package creation.
- Add fake exit-code tests, including the explicit `robocopy` success range if any implementation retains `robocopy`; otherwise no `robocopy` special case exists.
- Never log native output that may contain paths or configuration.

Guard proof: mutate the native runner to accept exit `1` from `dotnet`; the failure-propagation test must fail. Restore and verify green.

### Slice 3 — P15-F8 package identity and deployment lock

**Commit:** `feat(deploy): verify immutable package identity`

- Extend the Slice 2 package builder to generate the release ID before publish, pass it as the checked `AdQueryReleaseId` MSBuild property, and verify the P20-emitted entry-assembly metadata offline against the same manifest ID.
- Add the closed manifest, archive limits, expected-hash requirement, safe-entry validation, complete re-hash, duplicate-release semantics, and exclusive lock.
- Add missing/duplicate/malformed/mismatched assembly metadata, omitted/substituted MSBuild property, tamper, extra/missing file, duplicate/case collision, zip-slip, oversized archive, same-ID mismatch, and concurrent-lock tests.
- Do not invoke IIS.

Required guard proofs:

1. Omit or substitute the `AdQueryReleaseId` publish property; the published-assembly/manifest identity test must fail before archive creation.
2. Bypass one extracted-file hash comparison; the tampered-content test must fail.

Restore each mutation and verify green.

### Slice 4 — P15-F2 contained side-by-side release paths

**Commit:** `fix(deploy): replace destructive live mirroring`

- Add target initialization/marker, canonical containment/reparse/overlap checks, owned staging, same-volume atomic finalization, and immutable release layout.
- Remove all new-workflow support for arbitrary live-root cleanup and `-Force`.
- Add root, parent, sibling-prefix, source overlap, state overlap, reparse, malformed marker, and owned-staging cleanup tests.
- Do not switch IIS yet.

Guard proof: mutate the containment check to use an unsafe string prefix; the sibling path fixture must fail. Restore and verify green.

### Slice 5 — P15-F3 least-privilege release ACLs

**Commit:** `fix(deploy): restrict release filesystem rights`

- Add the P16 promotion-consumer adapter plus the pure ACL application planner and production filesystem adapter; configuration types, keys, paths, sinks, and secret validation remain in P16.
- Grant the actual pool identity read/execute on immutable release content and apply only the ACL/environment-reference projection supplied by P16.
- Reject broad inherited grants and unexpected mutable paths before cutover.
- Add virtual/custom identity, inheritance, broad group, `IUSR`, and external mutable-directory tests.

Guard proof: reintroduce recursive `IIS_IUSRS` modify rights in the ACL planner; the least-privilege test must fail. Restore and verify green.

### Slice 6 — P15-F4 target-only IIS control

**Commit:** `fix(deploy): scope IIS control to the target pool`

- Add the pinned `IISAdministration` adapter, snapshot, validate-only infrastructure policy, dedicated-pool check, and bounded target-pool state transitions.
- Add an architecture guard forbidding `GetProcessesByName`, `w3wp`, `Kill`, `WebAdministration`, raw `appcmd`, and unbounded sleeps in production deployment code.
- Add two-site/shared-pool and unrelated-worker fakes proving no foreign operation is emitted.

Guard proof: mutate orchestration to emit a foreign-pool stop operation; the exact operation-log test must fail. Restore and verify green.

### Slice 7 — P15-F5 accepted-work drain

**Commit:** `fix(deploy): require a quiescent application`

- Integrate the approved P14 drain client, release correlation, absolute monotonic deadline, pre-stop revalidation, and pre-cutover abort/resume behavior.
- Add queued/running convergence, new-work rejection, timeout, wrong-release, cancellation, and external-IIS-drift tests.
- Add no `AllowJobLoss` or force bypass.

Guard proof: bypass the nonzero-running guard; the fake operation log must show that IIS stop would have occurred and the test must fail. Restore and verify green.

### Slice 8 — P15-F6 journaled cutover and rollback

**Commit:** `feat(deploy): make IIS cutover recoverable`

- Add atomic journal transitions, exact old/new release and P16 promotion IDs/hashes, physical-path/environment-projection switch, pool-state restoration, rollback-once state machine, last-known-good pointer, and `-Recover` rollback.
- Inject a failure before and after every live phase and assert final IIS state, journal state, calls, and nonzero result.
- Reject third-party path drift rather than overwriting it.

Guard proof: disable restoration after a post-switch fault; the phase-matrix test must fail on old-path/pool-state assertions. Restore and verify green.

### Slice 9 — P15-F7 hard release-aware probes

**Commit:** `fix(deploy): gate success on release readiness`

- Integrate P20 liveness/readiness/release identity, explicit binding-matched URI, TLS/redirect/auth policy, absolute probe deadline, consecutive-success rule, and bounded safe diagnostics.
- Add start timeout, redirect, 401/403, TLS failure, malformed body, wrong release, transient readiness, permanent dependency failure, and success tests.
- Ensure every post-switch failure enters Slice 8 rollback and never prints completion; Slice 10 adds the mandatory new-release quiescence gate before rollback mutation.

Guard proof: accept a probe carrying the old release ID; the identity test must fail. Restore and verify green.

### Slice 10 — P15-F10 quiescent post-start rollback

**Commit:** `fix(deploy): quiesce a failed release before rollback`

- Extend the journal/recovery state machine with durable admission-possible and `RollbackQuiescent` evidence.
- On every rollback after the new pool could admit, consume P20's exact release identity on `200` or `503`, invoke P14 drain for that release, and require non-accepting quiescence before pool stop or path/projection restoration.
- Enter `RecoveryRequired` without further live mutation when release identity or quiescence cannot be proved; preserve the distinct never-started rollback path.
- Add post-start probe-failure, accepted queued/running convergence, unavailable/malformed/wrong-ID probe, drain timeout, stopped-after-start ambiguity, never-started, and interrupted-recovery tests.

Guard proof: bypass the rollback drain and allow the fake pool-stop operation before `RollbackQuiescent`; the exact operation-order test must fail. Restore and verify green.

### Slice 11 — P15-F9 retire the unsafe workflow

**Commit:** `docs(deploy): require the recoverable IIS workflow`

- Remove `csharp/deploy.ps1` rather than retain `-Force` compatibility.
- Update both READMEs with separate package, target-initialization, deploy, recovery, and prune commands; prerequisites; decision-approved paths; exit classes; and explicit non-production integration steps.
- Add an architecture/doc guard that no executable or documentation references `deploy.ps1 -Force`, whole-tree mirroring, global worker killing, or configuration inside a release.
- Document that IIS provisioning, P16 configuration, and P14/P20 contracts are prerequisites, not actions hidden inside deployment.

Guard proof: restore the legacy `-Force` command in a README fixture; the documentation architecture guard must fail. Restore and verify green.

## Deterministic Test Matrix

Use Pester `5.7.1`, `TestDrive:`, fake adapters, a fake monotonic clock, and exact operation logs. Randomized test values use a fixed seed. No default test touches the real registry, ACLs, IIS provider, process list, network, `D:\inetpub`, `C:\ProgramData\ADQuery`, or an external executable.

### Package and native execution

1. Dirty tracked and untracked source is rejected before verification/publish.
2. Canonical verification failure prevents publish.
3. Every unexpected native exit fails; no later operation occurs.
4. Required publish files are enforced.
5. Known secret-bearing configuration and mutable directories are excluded/rejected.
6. Package hash mismatch, malformed manifest, duplicate JSON key, unknown field, invalid release ID, wrong source commit, or missing/duplicate/malformed/mismatched entry-assembly `AdQueryReleaseId` metadata fails. Omitting or substituting the publish property cannot produce an accepted archive.
7. Missing, extra, changed, length-mismatched, and hash-mismatched files fail.
8. Absolute, drive, UNC, parent traversal, mixed-separator, duplicate, case-collision, link/reparse, excessive-count, excessive-size, and arithmetic-overflow archives fail before extraction.
9. Exact validated package produces deterministic normalized manifest ordering.

### Paths, marker, release, and lock

10. Volume root, release-root parent, source, state, live path, sibling-prefix confusion, and reparse paths are rejected.
11. Missing/malformed/mismatched target marker fails without writes.
12. `-WhatIf` performs zero mutations, drains, pool operations, probes, and journal writes.
13. Staging cleanup removes only the transaction-owned contained staging directory.
14. Existing same-ID exact release is an idempotent retry only with matching committed state; mismatch fails.
15. A held lock rejects the second deployment without changing IIS.
16. Process exit releases the lock; a file existing without a held handle is not misclassified as stale ownership.

### IIS scope, configuration, and permissions

17. Missing site/application/pool, wrong pool assignment, stopped pool, shared pool, auth drift, and binding mismatch fail before drain.
18. Runtime or ASP.NET Core Module mismatch fails before drain.
19. Missing/invalid P16 projection, typed startup or secret-source validation failure, promotion hash mismatch, or `DataRoot` beneath a release fails.
20. The exact IIS operation log contains only target-pool stop/start plus target-app path and P16 environment-projection set/read.
21. Unrelated sites, apps, pools, and fake worker processes receive zero operations.
22. Release ACL grants only the actual pool identity read/execute; broad group/`IUSR` write/read mutations fail.
23. Only P16-authorized external mutable directories receive the exact write rights.

### Drain and transaction

24. Old release baseline failure prevents staging from becoming live.
25. Drain closes admission, correlates release, and reaches zero before pool stop.
26. Drain timeout, cancellation, wrong release, or counts that rise/never settle prevents IIS mutation.
27. IIS drift between preflight and stop aborts.
28. Pool-stop timeout retains/restores the old application.
29. Failure at each phase before `PathSwitched` leaves the old path, old P16 promotion, and required initial pool state.
30. Failure at/after `PathSwitched` invokes rollback once and exits nonzero.
31. Successful rollback restores old path, prior `Applied` or `Absent` P16 promotion state, and pool state before recording `RolledBack`, never `Committed`.
32. Rollback failure records `RecoveryRequired`, retains both releases/journal, and never claims completion.
33. Re-running recovery is idempotent when path/promotion state is the recorded old or new pair; a third path, third projection, or mixed pair refuses mutation.
34. A first migration with prior P16 promotion state `Absent` removes only the new projection's owned entries and proves the old environment is absent before `RolledBack`.
35. Journal temp-write/flush/replace failure cannot advance the live mutation beyond the last durable intent.
36. A probe failure after the new pool starts calls P14 drain for the exact P20-reported new release and emits no pool-stop/path/projection operation until non-accepting quiescence is proven.
37. Queued/running work admitted during the probe window converges under the rollback drain; rising/nonzero counts, timeout, or wrong release cannot be bypassed.
38. An unavailable/malformed deployment response, wrong release ID, drain failure, or a stopped process after recorded start enters `RecoveryRequired` with no further live mutation.
39. A transaction that proves no `PoolStartRequested` intent was flushed and the new pool never started may roll back without a drain; interrupted recovery derives that proof from both durable phase and current IIS state rather than current state alone.

### Probes, outcomes, and leakage

40. A pool-start failure after `PoolStartRequested` is admission-possible and requires drain/quiescence or `RecoveryRequired`; only a failure before that durable intent may use the never-started proof.
41. Redirect, TLS, transport, timeout, 401/403, malformed schema, liveness failure, readiness failure, and wrong release fail deployment and enter the applicable safe rollback/quiescence path.
42. A P20 `503` with the exact release ID remains parseable for rollback targeting but never counts as a ready observation.
43. Transient failures reset the consecutive count and never extend the absolute deadline.
44. Exactly three qualifying observations commit; fewer do not.
45. P06/P13 application errors are irrelevant to deployment outcome mapping; P15 emits only fixed deployment codes.
46. Package contents, journals, logs, console output, and fake metrics omit seeded secrets, configuration values, response bodies, credentials, and raw exception text.
47. Only `Committed` exits zero.
48. No production deployment source references `-Force`, global process enumeration/kill, `SilentlyContinue`, raw native mutation without the checked runner, or unbounded `Start-Sleep`.

## Red-Green Guard Protocol

For every slice:

1. Add the focused guard and make it pass with the completed slice.
2. Temporarily reverse only the protected production behavior through a local patch.
3. Run the focused guard and record the expected failure and assertion.
4. Restore the production behavior through a patch.
5. Run the focused Pester file and P01's canonical verifier green.
6. Confirm no mutation, temporary archive, journal, fake IIS state, or test result remains outside ignored artifacts.
7. Commit only the restored one-finding slice.

A guard that passes when its behavior is reversed is vacuous and must be replaced. Do not use a live IIS mutation as a red/green proof.

## Verification

### Canonical automated verification

P15 extends P01's `scripts/verify.ps1` with one exact-version Pester stage and uses that repository-root command for every slice. Until P01 lands, the current fallback remains:

```powershell
dotnet build csharp/AdQueryOrchestrator.csproj -c Release --nologo
```

The fallback is insufficient to complete P15 because it cannot run deployment guards.

The Pester stage must:

- import Pester with `-RequiredVersion 5.7.1`;
- run `tests/Deployment` with CI/noninteractive settings;
- write NUnit/JUnit-compatible results beneath P01's ignored artifacts tree;
- make zero tests silently skip due to missing IIS/elevation/network;
- return nonzero on failed tests, discovery errors, missing pinned module, or zero discovered deployment tests.

### Static inspection

After each production wiring slice, search the deployment surface for forbidden paths:

```text
GetProcessesByName
w3wp
.Kill(
-Force
-ErrorAction SilentlyContinue
Import-Module WebAdministration
appcmd
Invoke-WebRequest without the probe adapter
Remove-Item -Recurse against an unproven path
```

The executable architecture test owns this list; the command above is an implementation-time diagnostic, not a second verifier.

### Opt-in disposable IIS integration

After all automated guards pass, run a separate administrator-only integration script on an explicitly disposable Windows/IIS host. It must require a site/application/dedicated-pool tuple carrying a generated test suffix and paths beneath a generated test root; refuse the production names/default paths.

Verify:

- package and deploy a benign local test build;
- unrelated test site/pool remains continuously available;
- injected bad package fails before mutation;
- injected new-release liveness/readiness failure drains the exact new release before restoring the prior path; an injected rollback-drain failure leaves `RecoveryRequired` without a stop/switch;
- target pool stop/start is bounded and no worker kill occurs;
- a simulated interrupted journal is recovered to the prior release;
- ACLs match the planned principals/rights;
- a second concurrent invocation is rejected;
- expected release identity is observed;
- no secret/network/AD/LLM dependency is required.

Record commands, host role/version, module/runtime versions, generated fixture names, outcomes, and cleanup. Do not run this check against production and do not claim it was run when no disposable fixture exists.

## Acceptance Criteria

P15 is complete only when:

- P01, P03, P14, P16, and P20 prerequisite contracts used here are landed.
- All owner decisions are durably recorded and plan status is `Approved`.
- Pester `5.7.1` is pinned and invoked by the one canonical verifier.
- Default deployment tests are deterministic, non-elevated, network-free, IIS-free, and non-skipping.
- Packaging requires clean committed source, canonical verification, checked publish, exact entry-assembly/manifest release identity, closed manifest, safe archive, and explicit expected package hash.
- The elevated deploy command never compiles/restores and accepts no arbitrary in-place target or `-Force` bypass.
- Target marker, path containment, archive limits, reparse checks, complete hashes, duplicate-release rules, and exclusive lock all fail before live mutation.
- P16 is the sole owner of typed configuration, secret-source validation, the Serilog pipeline, portable `DataRoot`, and production-promotion schema; P15 consumes that projection without a parallel model.
- Production configuration/logs/state are external to immutable releases, and the approved P16 IIS environment/ACL projection is applied, read back, and included by ID/hash in rollback.
- Release binaries are read/execute-only to the actual pool identity; no broad `IIS_IUSRS`/`IUSR` grant remains.
- Existing site/application/dedicated pool/auth/binding/runtime are validated, not silently provisioned or repaired.
- P14 proves old-release quiescence before cutover stop and exact new-release quiescence before any rollback stop after admission was possible; no accepted work is intentionally discarded.
- No code enumerates or kills `w3wp`; only the target dedicated pool is controlled.
- The journal is durable before mutation and every injected phase failure reaches the specified old/new path, pool, journal, and exit outcome.
- P20 liveness/readiness/release identity satisfy the approved consecutive/absolute deadline gate before `Committed`, and its exact-ID `200`/`503` deployment envelope safely targets rollback drain without weakening traffic readiness.
- Every post-switch failure enters rollback once; rollback mutates a possibly admission-capable new release only after exact-release quiescence, and any inability to prove that state is explicit `RecoveryRequired` without global IIS restore.
- Only `Committed` returns zero or prints completion.
- Old-release pruning is a separate contained, confirmation-aware action that protects all referenced releases.
- The unsafe legacy script and `deploy.ps1 -Force` documentation are gone.
- Every slice has recorded revert-fails/restore-passes evidence and canonical verification passes.
- The disposable IIS integration matrix is either recorded successful or explicitly reported not run; production is never used as the fixture.
- Advisory review is resolved in no more than three substantive rounds.

## Implementation Rollback

Use new revert commits; do not rewrite history.

- Revert documentation only together with the entry point it describes; never restore instructions for an unsafe script.
- The package/test foundation may remain if later IIS wiring is reverted; it is non-mutating and independently useful.
- If P14/P16/P20 integration is unavailable, retain the new deploy command in a fail-closed unavailable state. Do not restore forced in-place deployment.
- Revert consumers before removing manifest, marker, journal, result, or adapter contracts.
- A production release rollback uses the journaled runtime procedure and prior P16 `Applied` ID or `Absent` state, not a git revert or reconstructed configuration.
- Do not delete release/state directories merely because code support is reverted. Operators must use the last compatible safe tooling or a reviewed manual recovery runbook.
- Never restore global `w3wp` killing, broad recursive ACL grants, warning-as-success, secret-bearing live-root preservation, or unchecked native calls as a compatibility fallback.

## Risks and Mitigations

- **P14/P16/P20 are later plans.** Implement non-mutating foundations first, but block production cutover until their exact contracts land. Adapt names, not ownership.
- **Side-by-side releases consume disk.** Preflight required space and use separate confirmed pruning; never trade rollback safety for automatic deletion.
- **External dependency readiness can fail independently of a release.** Baseline the old release before drain, distinguish rollback liveness from readiness, and report fixed cause classes without claiming rollback repairs providers.
- **The new release may accept work during readiness observations.** Treat readiness as truthful traffic eligibility, drain the exact new release before rollback stop, and prefer `RecoveryRequired`/manual intervention over interrupting work when identity or quiescence cannot be proven.
- **Physical-path changes may interact with IIS configuration locking or shared configuration.** Validate dedicated local ownership before mutation; reject unsupported shared-config mode until separately tested.
- **A custom app-pool identity complicates ACLs.** Read the configured identity and require P16's explicit principal mapping; never guess or broaden.
- **Hashing large packages costs time and I/O.** Stream bounded content once before extraction and once after; correctness outranks saving one verification pass.
- **SHA-256 does not authenticate the publisher.** Require the expected hash through a separate operator step and do not claim signing. Add signatures only with approved key ownership.
- **A process crash can occur between IIS mutation and journal-result flush.** Record intent first and make recovery compare actual state with exact old/new paths before one deterministic rollback.
- **IIS module behavior varies by server.** Pin `IISAdministration`, validate the target version, isolate it behind an adapter, and prove real behavior only on a disposable host.
- **Stopping a dedicated pool still causes maintenance downtime.** Drain first and keep switch/start/probe bounded. Zero-downtime routing is explicitly outside this plan.
- **The old installation may not have a release manifest or applied P16 projection.** The first migration may use its exact canonical path as rollback identity and journal prior promotion state `Absent`; rollback then clears only the P16-owned entries introduced by cutover and verifies absence. Do not ingest or copy legacy configuration. Later rollbacks require release manifests and explicit promotion IDs.
- **Probe credentials can be overprivileged.** Server administration/P20 own a narrow operator credential; P15 never accepts inline passwords or logs credential material.
- **Manual IIS changes can race deployment.** Locking protects P15 instances only; re-snapshot immediately before mutation and refuse unexpected state.
- **Strict preflight may initially block deployment.** That is the intended safety behavior. Fix infrastructure/configuration provenance rather than add a force bypass.

## Advisory Review

Use no more than three headless Claude Code review rounds with the currently configured model, maximum effort, structured JSON, `Read`/`Grep`/`Glob` only, strict empty MCP configuration, no session persistence, and no `--model` override. Each substantive round records the harness/model provenance, assessment, material findings, revisions or retained disagreements, and remaining optional comments.

If Round 3 requires changes, apply them, explicitly record that the final revisions were not re-reviewed, and do not run Round 4. A review invocation that crashes or returns no parseable substantive result does not count as a round, but its orphaned process must be identified and terminated before retrying.

### Round 1 — 2026-07-21T22:54:39Z

- Harness: Claude Code 2.1.217, configured `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8`, maximum effort, structured JSON, `Read`/`Grep`/`Glob` only, strict empty MCP, no session persistence, and no `--model` override.
- Assessment: `revisions_required`.
- Required finding `P15-R1-01` (`high`): Invariant 26 said rollback restored only physical path and pool state even though cutover and the detailed rollback algorithm also mutate/restore the P16 environment-reference projection. A literal implementation could mark rollback successful while the old release ran under the new release's configuration; the first-migration prior-absence case was unspecified.
- Revision: the journal now records prior promotion state as `Applied` ID/hash or `Absent`; cutover and rollback use the same target-application commit boundary; `Applied` reapplies the prior projection, while `Absent` removes exactly the P16-owned entries introduced by cutover. `RolledBack` requires read-back proof of old path, prior promotion state, pool state, and liveness. Global IIS restore remains forbidden.
- Optional comment applied: Slice 1's guard now points the active Pester stage at an empty fixture and requires the canonical verifier to fail on zero discovered deployment tests; it no longer removes the stage that would run the sentinel.
- Optional comment applied: first-migration old-release identity is required only when known, while readiness remains mandatory and rollback uses path/promotion/pool/liveness proof.
- Optional comment applied: package secret-field checks now invoke P16's authoritative package-default/secret-source catalog rather than maintain P15 names.
- Optional comment applied: the 120-second drain ceiling is P15's deployment wait, clamped to a shorter P14-reported bound; P14 retains drain semantics/state ownership.
- Confirmed strengths: repository evidence and one-finding commits were accurate; P14/P16/P20 ownership was preserved; side-by-side release, lock, archive/path, IIS scope, deterministic Pester, and fault-injection contracts were substantive and implementable.

Round 2 reviewed the reconciled P16 rollback state, first-migration behavior, guard clarifications, and the complete transaction for adjacent regressions.

### Round 2 — 2026-07-21T23:00:52Z

- Harness: Claude Code 2.1.217, configured `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8`, maximum effort, structured JSON, `Read`/`Grep`/`Glob` only, strict empty MCP, no session persistence, and no `--model` override.
- Assessment: `accepted`.
- Required changes: none.
- Confirmed: prior P16 `Applied`/`Absent` state is value-free and durable; cutover and rollback use one target-app path/promotion boundary; mixed/third state fails closed; rollback read-back includes path, promotion, pool, and liveness; first migration is deterministic; global IIS restore remains forbidden.
- Confirmed: zero-test Pester failure is non-vacuous, unknown legacy release identity does not weaken readiness, package secret checks derive solely from P16, and P15's 120-second ceiling does not absorb P14 semantics.
- Optional precision applied: rollback now enumerates exact new-pair restore, exact old-pair idempotence, and `RecoveryRequired` for every other combination, including both mixed pairs.
- Optional precision applied: Invariant 26 now includes required rollback liveness in its verification gate.
- Optional precision applied: a colliding unmanaged entry is defined as an existing key in P16's owned-key set without P16 promotion provenance; P16 classifies it without exposing values to P15.

### Round 3 — 2026-07-21T23:03:44Z

- Harness: Claude Code 2.1.217, configured `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8`, maximum effort, structured JSON, `Read`/`Grep`/`Glob` only, strict empty MCP, no session persistence, and no `--model` override.
- Assessment: `accepted`.
- Required changes: none. No fourth round is warranted or permitted.
- Confirmed: rollback branches on the exact current physical-path/P16-promotion pair: the exact new pair restores the journaled old pair, the exact old pair continues idempotently, and every mixed or third state enters `RecoveryRequired` without mutation.
- Confirmed: `RolledBack` requires read-back proof of the old physical path, the exact prior `Applied` ID/hash or `Absent` promotion state, target-pool state, and rollback liveness.
- Confirmed: colliding unmanaged environment entries are defined and classified by P16 without exposing values; first-migration absence, global-restore prohibition, P16 ownership, journal secrecy, and retry idempotence remain coherent.
- Optional comments retained without revision: the exact-old branch could repeat that it performs no path/promotion mutation before pool/read-back checks, but the algorithm is already unambiguous; the exhaustive pair branch belongs in its current canonical algorithm/invariant rather than being duplicated in Invariant 26.

Round 3 made no required change to the reviewed snapshot. After P16 landed, P15 replaced its stale `output` child wording with P16's fixed typed-child contract and narrowed P16's retention ownership to operational logs/root controls while domain plans retain their own store policies. This precision alignment was not re-reviewed; the three-round limit prohibits a fourth round.

### Post-review cross-plan finding — rollback admission race

During P20 planning, the P14/P15/P20 composition exposed a new HIGH finding: P14's new process becomes `Accepting`, P20 readiness truthfully means eligible for normal work, and P15 observes readiness multiple times before commit. The prior automatic rollback stopped the new pool before any P14 drain, so a late probe failure could interrupt work admitted during that window.

P15-F10 now requires an exact-release P14 drain and durable `RollbackQuiescent` proof before any rollback stop/path/projection mutation once new-release admission was possible. P20 supplies a bounded exact-release deployment envelope on both `200` and `503`; unavailable/malformed/wrong-ID state or drain failure produces `RecoveryRequired` without further live mutation. A separately guarded never-started path remains eligible for direct rollback. This repair was made after all three Claude rounds and was not re-reviewed; no fourth round was run.

### Post-review cross-plan finding — release identity package binding

During P20's independent consistency audit, the project-side identity producer and package-side consumer were found not to share an exact finding/commit boundary. P20-F4 emits and parses the runtime assembly metadata, while P15 previously generated only the manifest ID and did not explicitly pass or verify the corresponding build property.

P15-F8/Slice 3 now extends the package builder with checked `AdQueryReleaseId` property injection and a non-executing published-assembly metadata check before manifest/archive acceptance. Its required mutation proves an omitted or substituted property cannot produce a package. This repair was made after all three Claude rounds and was not independently re-reviewed; no fourth round was run.
