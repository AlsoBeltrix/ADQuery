# P03 — Dependency Security and .NET Runtime Modernization

**Status:** Reviewed. Implementation is unauthorized. Owner decisions remain pending.

## Finding

The application directly references a vulnerable Negotiate authentication package, targets a short-lived runtime nearing end of support, restores a mixed-major dependency graph, and does not pin its SDK. The eventual .NET 10 migration also requires an explicit IIS hosting prerequisite and Windows Authentication compatibility proof.

This plan deliberately separates the urgent security servicing update from the broader runtime and package modernization. The security fix must not wait for the migration.

## Evidence

Evidence was verified against commit `0649177` on 2026-07-21.

- `csharp/AdQueryOrchestrator.csproj:4` targets `net9.0-windows`.
- `csharp/AdQueryOrchestrator.csproj:12` requests `Microsoft.AspNetCore.Authentication.Negotiate` `9.0.0`.
- `Program.cs:38-46` configures Negotiate authentication and requires the `ANALOG\ADEXNLQ_Users` role for the fallback authorization policy.
- `UserController.cs:7-20` provides an authenticated, role-protected identity endpoint suitable for the Windows Authentication smoke test.
- `dotnet list csharp/AdQueryOrchestrator.csproj package --vulnerable --include-transitive` reports two High advisories against the direct Negotiate reference:
  - `GHSA-2p3q-h3hg-jcqq` / `CVE-2026-47303`
  - `GHSA-8prm-248r-h957` / `CVE-2026-47300`
- Both advisories identify `9.0.0` through `9.0.17` as affected and `9.0.18` as the patched .NET 9 package. They identify `10.0.0` through `10.0.9` as affected and `10.0.10` as the patched .NET 10 package.
- The CVE-2026-47300 advisory specifically calls out Negotiate authentication with LDAP role retrieval, which overlaps this application’s Windows role authorization model.
- `dotnet list ... package --include-transitive` resolves a mixture of 8.x and 9.x Microsoft packages. Direct references include:
  - `Microsoft.AspNetCore.Authentication.Negotiate` `9.0.0`
  - `Microsoft.AspNetCore.OpenApi` `9.0.0`
  - `Microsoft.Extensions.Http` `9.0.0`
  - `System.DirectoryServices` `8.0.0`
  - `System.Text.Json` `9.0.0`
  - `ClosedXML` `0.105.0`
  - `Serilog.AspNetCore` `8.0.2`
  - `Serilog.Sinks.File` `6.0.0`
  - `Swashbuckle.AspNetCore` `6.4.0`
- No `global.json` exists at the recorded evidence commit. P01 creates a .NET 9 pin before the runtime migration, so P03 modifies that file rather than adding a second SDK contract. On the inspected machine, `dotnet --info` selects SDK `10.0.302` implicitly.
- The inspected machine has patched shared frameworks `Microsoft.AspNetCore.App 9.0.18` and `10.0.10`, but deployment-host runtime state is unverified.
- `csharp/web.config:8-12` launches the application framework-dependently through `dotnet` with IIS in-process hosting.
- `csharp/deploy.ps1:44` publishes framework-dependently without an explicit target framework or runtime prerequisite check.
- Microsoft’s support policy lists .NET 9 as STS ending 2026-11-10 and .NET 10 as LTS ending 2028-11-14. These dates and current patch versions are volatile and must be rechecked immediately before implementation.
- The .NET IIS documentation states that the Hosting Bundle supplies the .NET runtime and ASP.NET Core Module required by IIS. Installing it before IIS requires a repair after IIS installation.

Authoritative references:

- [GHSA-2p3q-h3hg-jcqq](https://github.com/advisories/GHSA-2p3q-h3hg-jcqq)
- [GHSA-8prm-248r-h957](https://github.com/advisories/GHSA-8prm-248r-h957)
- [.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy)
- [.NET 10 IIS Hosting Bundle](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/iis/hosting-bundle?view=aspnetcore-10.0)

## Desired Outcome

- No direct or transitive NuGet dependency is reported vulnerable.
- The urgent Negotiate fix can be released independently on .NET 9.
- The application subsequently targets supported .NET 10 LTS on Windows.
- The SDK feature band is selected deliberately through `global.json`.
- Microsoft dependency majors align with the target framework, redundant shared-framework references are removed where proven unnecessary, and third-party major upgrades are isolated.
- A framework-dependent IIS deployment is blocked unless the patched .NET 10 Hosting Bundle and ASP.NET Core Module are present.
- Automated verification proves startup, authorization policy, package security, build, and publish compatibility.
- A production-matched IIS staging smoke proves actual Negotiate, role authorization, and LDAP behavior.

## Scope

### Included

- Immediate servicing of the .NET 9 Negotiate package and hosting runtime.
- SDK selection through a root `global.json`.
- Migration from `net9.0-windows` to `net10.0-windows`.
- Alignment or removal of direct Microsoft package references.
- Isolated Serilog and Swashbuckle major-version upgrades.
- Framework-dependent Release publish verification.
- Runtime/package-policy checks integrated into the canonical P01 verification entry point.
- Documentation of IIS Hosting Bundle and Windows deployment prerequisites.
- Automated authorization-policy coverage and manual Windows Authentication compatibility testing.

### Excluded

- Deployment-script safety, process targeting, rollback automation, or native exit-code handling; P15 owns those changes.
- Liveness/readiness endpoint redesign; P20 owns that change.
- Logging topology and portable storage paths; P16 owns those changes.
- LLM request compatibility; P02 owns that change.
- Application architecture decomposition; P21 owns that change.
- Switching to Linux, containers, or non-IIS hosting.
- Central package management for this single-project repository.
- A self-contained deployment unless the owner rejects the recommended framework-dependent model.

## Dependencies and Ordering

- The urgent Negotiate servicing slice has **no dependency on P01** and must proceed first once this plan is approved.
- P01 must land before the .NET 10 migration so the canonical verification command, test project, and CI gate exist.
- P02 should land before the final end-to-end LLM smoke, but it does not block package patching, compilation, publishing, startup, or Windows Authentication verification.
- P15 must consume this plan’s runtime preflight contract before any production deployment automation is considered safe.
- A production-matched IIS staging host, an approved-group test account, and an authenticated non-member test account are required before migration completion.
- Installing or repairing a Hosting Bundle and restarting IIS services are external administrative actions. They require a separate explicit deployment authorization.

## Owner Decisions Required

### D1 — Release separation

**Recommendation:** Ship the patched .NET 9 Negotiate package as an independent first release, then perform the .NET 10 migration after P01. Combining them delays a High-severity authentication fix and makes authentication regressions harder to attribute.

### D2 — Hosting model

**Recommendation:** Retain framework-dependent, IIS in-process hosting. It matches `web.config`, keeps artifacts smaller, and centralizes runtime servicing. The cost is an explicit requirement to keep the IIS Hosting Bundle patched. Self-contained deployment shifts servicing responsibility into every application publish.

### D3 — SDK selection

**Recommendation:** Modify P01's root `global.json` to the approved .NET 10 `10.0.300` feature band with `rollForward: latestPatch` and `allowPrerelease: false`. This accepts patched SDKs within the tested feature band without silently moving to a new feature band.

### D4 — Package modernization boundary

**Recommendation:** Align required Microsoft packages with .NET 10, remove redundant shared-framework references after compile proof, and update Serilog and Swashbuckle in separate commits. Do not combine unrelated third-party major upgrades with the target-framework change.

### D5 — Migration promotion gate

**Recommendation:** Do not promote the .NET 10 artifact until the production-matched IIS matrix passes with real domain identities. Automated fake-auth tests protect policy wiring, but they cannot prove Kerberos/NTLM negotiation or domain role expansion.

## Implementation Stages and Commits

Each stage is a separate commit. Do not amend, squash, or combine commits. Stop on the first unexplained build, audit, test, publish, or staging regression.

### Stage 0 — Refresh the baseline

No repository mutation.

1. Record the implementation base commit.
2. Run:
   - `dotnet --info`
   - `dotnet list csharp/AdQueryOrchestrator.csproj package --include-transitive`
   - `dotnet list csharp/AdQueryOrchestrator.csproj package --outdated`
   - `dotnet list csharp/AdQueryOrchestrator.csproj package --vulnerable --include-transitive`
   - the P01 canonical verification command, if available
3. Recheck the two advisories and .NET support policy.
4. Resolve the current patched release in each selected major. `9.0.18` and `10.0.10` are security floors from the reviewed advisories, not permanent “latest” aliases.
5. Confirm that package sources are the intended trusted feeds.
6. Save the command output as implementation evidence without committing machine-specific paths or installed-version inventories.

### Stage 1 — Patch Negotiate while retaining .NET 9

**Commit:** `fix(deps): patch Negotiate authentication on net9`

1. Change only `Microsoft.AspNetCore.Authentication.Negotiate` from `9.0.0` to the current patched 9.0 servicing release, with `9.0.18` as the minimum acceptable version.
2. Do not change `TargetFramework`, other packages, SDK selection, application code, or deployment scripts in this commit.
3. Restore, build, publish, and run the vulnerability audit.
   - If P01 package lock files already exist, regenerate and commit every affected lock file in this same commit before running locked verification.
   - If P01's temporary attribution gate already exists, replace it in this same security commit with the unconditional parsed zero-vulnerability gate. If Stage 1 precedes P01, P01 installs the unconditional gate directly when it later lands.
4. Confirm that neither named GHSA appears in the resolved graph.
5. Confirm the staging IIS host has the matching or newer patched .NET 9 Hosting Bundle/shared framework before deploying this artifact.
6. Restart the application after runtime or package servicing, as required by the advisory.
7. Run the Windows Authentication smoke subset before accepting the commit.

This slice is urgent and must not wait for P01 or the .NET 10 migration.

### Stage 2 — Atomically migrate the SDK, application, tests, and Microsoft dependencies

**Prerequisite:** P01 completed.

**Commit:** `build(dotnet): migrate the windows solution to net10`

P01 requires the SDK pin, application target, test target, and lock files to move together. Do not split these changes: a `net9.0-windows` test project cannot reference the migrated `net10.0-windows` application.

1. Modify P01's root `global.json` to the approved D3 policy.
2. Change both `csharp/AdQueryOrchestrator.csproj` and `tests/AdQueryOrchestrator.Tests/AdQueryOrchestrator.Tests.csproj` to `net10.0-windows`.
3. Align required Microsoft packages with the same patched .NET 10 servicing release:
   - `Microsoft.AspNetCore.Authentication.Negotiate`: at least `10.0.10`;
   - `System.DirectoryServices`: current supported 10.0 servicing release.
4. Inspect each remaining Microsoft direct reference:
   - Remove `Microsoft.Extensions.Http` if `AddHttpClient` compiles and tests through the ASP.NET Core shared framework without it; otherwise pin it to the aligned 10.x servicing release.
   - Remove `System.Text.Json` if all usages compile and test through the shared framework; otherwise pin it to the aligned 10.x servicing release.
   - Remove `Microsoft.AspNetCore.OpenApi` if no application API requires it after the Swashbuckle graph is resolved; otherwise pin it to the aligned 10.x servicing release.
5. Confirm `ClosedXML` compatibility with .NET 10 through its supported target assets plus the repository's XLSX tests and publish smoke. Do not upgrade it unless compatibility evidence requires an isolated follow-up commit.
6. Do not use floating or ranged package versions. Inspect the resolved transitive graph for mixed Microsoft majors; retain a mixed major only when a third-party dependency requires it and record the reason.
7. Regenerate and commit the application and test-project package lock files after all target/package edits and before any locked restore.
8. Extend P01’s canonical verification rather than creating a competing entry point:
   - require `global.json`;
   - reject preview SDK selection;
   - print the selected SDK;
   - fail if the selected SDK is outside the approved .NET 10 feature band.
9. Update CI setup to install/use the same feature band.
10. Resolve .NET 10 compile errors narrowly. Do not refactor unrelated behavior.
11. Run canonical verification, package audit, Release publish, and startup smoke.
12. Verify from the repository root and from `csharp/` that SDK resolution is identical.
13. Inspect the generated `.runtimeconfig.json` and `.deps.json` to confirm the .NET 10 target and intended dependency versions.
14. Document the SDK prerequisite separately from the deployment runtime prerequisite.

If removing a shared-framework reference produces ambiguity or changes runtime behavior, retain the explicit aligned 10.x reference and record the reason.

### Stage 3 — Update Serilog as one dependency family

**Commit:** `chore(deps): align serilog with aspnet core 10`

1. Upgrade `Serilog.AspNetCore` to its stable .NET 10-compatible major.
2. Upgrade `Serilog.Sinks.File` only as required for that family’s supported graph.
3. Regenerate and commit every affected package lock file in the same commit before locked verification.
4. Do not address the duplicate logger/sink configuration finding; P16 owns that behavior change.
5. Verify startup, structured request logging, exception logging, rolling file creation, and graceful flush.
6. Confirm configuration binding still recognizes the existing `Serilog` section.

### Stage 4 — Update Swashbuckle independently

**Commit:** `chore(deps): update swashbuckle for dotnet 10`

1. Upgrade `Swashbuckle.AspNetCore` to the selected stable .NET 10-compatible release.
2. Make only API-compatibility changes required by that upgrade. In particular, inspect `OpenApiInfo` namespace/type changes instead of adding redundant package references to mask compilation failures.
3. Regenerate and commit every affected package lock file in the same commit before locked verification.
4. In Development, verify generation of `/swagger/v1/swagger.json` and loading of the Swagger UI.
5. Verify production behavior remains unchanged: Swagger is not exposed outside Development by the current startup condition.

### Stage 5 — Record runtime and hosting prerequisites

**Commit:** `docs(runtime): document dotnet 10 iis prerequisites`

1. Update the root and C# README runtime statements to `net10.0-windows`.
2. Document:
   - the approved SDK feature band for builds;
   - framework-dependent deployment;
   - the minimum patched .NET 10 Hosting Bundle/runtime;
   - IIS installation-before-bundle ordering and the repair requirement;
   - required ASP.NET Core Module V2 presence;
   - x64/app-pool assumptions;
   - required application restart after runtime servicing.
3. Give P15 a single preflight contract:
   - verify patched `Microsoft.AspNetCore.App 10.0.x`;
   - verify ASP.NET Core Module V2;
   - verify the selected app pool and application;
   - abort before copying files if prerequisites fail.
4. Do not implement P15’s deployment mutations in this plan.

### Stage 6 — Production-matched staging acceptance

No production mutation and no commit unless documentation evidence changes.

1. Publish the exact candidate commit framework-dependently in Release mode.
2. Install or repair the approved patched .NET 10 Hosting Bundle on a production-matched staging IIS host under separately authorized administration.
3. Restart the required IIS services or host according to Microsoft guidance.
4. Deploy the candidate to staging.
5. Execute the complete compatibility matrix.
6. Retain the patched .NET 9 artifact and runtime as the rollback candidate until migration acceptance.
7. Record results, host OS/IIS/runtime versions, candidate commit, and artifact identity in the release evidence location chosen by P01/P15. Do not put machine-specific inventory in `.agents/state.md`.

## Automated Verification

Run the canonical P01 verification command after every implementation commit. The resulting pipeline must include:

1. Restore with locked, exact direct-package versions.
2. Release build.
3. The focused automated test suite.
4. Vulnerability audit of top-level and transitive packages.
5. Framework-dependent Release publish.
6. Startup/DI smoke against the published output.
7. Development-only Swagger document generation after the Swashbuckle upgrade.
8. Authorization-policy integration coverage using a test authentication handler:
   - authenticated approved-role principal receives `200` from `/api/user/info`;
   - authenticated principal without `ANALOG\ADEXNLQ_Users` receives `403`;
   - unauthenticated principal receives `401`;
   - the successful response reports the expected identity.
9. A runtime baseline check that rejects:
   - a missing/unapproved `global.json`;
   - either project targeting anything other than the approved Windows .NET 10 TFM after Stage 2;
   - preview SDK selection;
   - reported vulnerable packages.

Once the application graph has no vulnerability, replace P01's temporary application-versus-solution attribution comparison with one unconditional parsed zero-vulnerability gate. If urgent Stage 1 lands before P01 is implemented, P01 must install the unconditional gate directly rather than creating the temporary comparison.

Fake authentication is suitable only for policy-wiring regression coverage. It is not a substitute for the IIS Windows Authentication matrix.

## Vulnerability Guard Proof

The security guard must produce a red-to-green proof, not merely a successful `dotnet list` process exit. The .NET package-list command can report vulnerabilities while returning success, so P01’s wrapper must parse structured output and fail when any vulnerability is present.

When urgent Stage 1 runs before P01, use the current Release build plus explicit before/after machine-readable package-audit assertions for this proof; do not pretend the package command's exit code is a gate. Once P01 exists, use its canonical verification wrapper.

### Baseline red

Before Stage 1, run the canonical vulnerability gate against `9.0.0`. It must fail and identify both:

- `GHSA-2p3q-h3hg-jcqq`
- `GHSA-8prm-248r-h957`

### Patched green

After Stage 1, run the same gate. It must pass and resolve Negotiate to at least `9.0.18`.

### Revert-fails/restore-passes proof

After adding or updating the automated guard:

1. Use a temporary patch to restore only the Negotiate reference to `9.0.0`.
2. Force dependency reevaluation.
3. Run the canonical verification and confirm it fails because the vulnerability gate reports the advisories.
4. Reapply the patched package version without using history rewrite or destructive Git restoration.
5. Force dependency reevaluation again.
6. Run the full canonical verification and confirm it passes.
7. Record both command results in the implementation review evidence.

Repeat the resolved-graph audit after Stage 2. The .NET 10 package must resolve to at least `10.0.10`, and the IIS host must run an equal or newer patched 10.0 shared framework.

## Compatibility Matrix

| Area | Environment/case | Expected result | Evidence required |
|---|---|---|---|
| SDK selection | Clean Windows build host with approved SDK feature band | Root and `csharp/` select the approved stable SDK | `dotnet --version`, CI log |
| Restore/build | Windows Release build | Restore and build succeed with no newly introduced warnings | Canonical verification log |
| Package security | Final resolved graph | No vulnerable top-level or transitive packages | Structured audit output |
| Publish | Framework-dependent `net10.0-windows` Release publish | Publish succeeds; runtime config targets .NET 10 | Publish log and runtimeconfig inspection |
| Startup | Published output under test configuration | Host starts and dependency injection resolves | Automated startup smoke |
| Swagger | Development environment | Swagger JSON and UI load successfully | Automated/manual smoke |
| IIS module | Production-matched staging IIS | ASP.NET Core Module V2 loads the in-process app | IIS/module inventory and successful request |
| Anonymous request | No Windows credentials | Request is challenged; protected API does not return data | HTTP status and IIS/app log |
| Approved user | Domain user in `ANALOG\ADEXNLQ_Users` | `/api/user/info` returns `200`, correct identity, and authenticated state | Sanitized response/status |
| Disallowed user | Authenticated domain user outside the role | Protected endpoint returns `403` | HTTP status and authorization log |
| Role expansion | Direct and nested membership cases used operationally | Authorization behavior matches the current accepted baseline | Account/membership case results |
| Kerberos | Domain-joined client where SPN configuration supports Kerberos | Successful access; negotiated mechanism recorded | IIS authentication diagnostics |
| NTLM fallback | Approved environment where fallback is intentionally supported | Behavior matches deployment policy | IIS authentication diagnostics |
| Directory access | Approved identity executes a minimal read-only query | LDAP query succeeds without permission expansion | Sanitized request/result log |
| LLM path | P02-compatible configuration | One minimal plan-generation request succeeds | Sanitized request/result log |
| CSV/XLSX | Representative small export | Existing export formats still open and contain expected rows | Smoke artifact/checksum |
| Logging | Startup, request, and exception cases | Existing configured log outputs remain functional | Sanitized log excerpts |
| Rollback | Last patched .NET 9 artifact on staging | Application starts and approved-user auth succeeds | Rollback drill result |

If production policy does not permit NTLM, replace the NTLM row with proof that NTLM is rejected and Kerberos remains functional.

## IIS Preflight Contract

Before deploying the .NET 10 artifact, the deployment process must establish all of the following:

- The server OS and IIS version support the selected .NET 10 Hosting Bundle.
- IIS is already installed; otherwise repair or reinstall the Hosting Bundle after IIS installation.
- The patched .NET 10 Hosting Bundle is installed.
- `dotnet --list-runtimes` contains a non-vulnerable `Microsoft.AspNetCore.App 10.0.x`.
- `%PROGRAMFILES%\IIS\Asp.Net Core Module\V2` exists and contains the active module.
- The application pool architecture matches the published artifact.
- The application retains in-process hosting through `AspNetCoreModuleV2`.
- Windows Authentication is enabled and Anonymous Authentication is disabled for the intended application scope.
- The application pool identity retains existing directory permissions.
- Required SPNs and kernel-mode/app-pool credential settings match the deployment’s accepted Kerberos design.
- The last patched .NET 9 artifact remains available for rollback.

P15 must make these checks fail closed before application files are replaced.

## Rollback

### Urgent servicing slice

Do not roll back to Negotiate `9.0.0` or any version below the advisory’s patched floor. If the first patched version causes a regression:

1. Stop promotion.
2. Prefer a newer patched .NET 9 servicing release.
3. Retain the patched Hosting Bundle.
4. Diagnose Windows Authentication in staging.
5. Escalate if no patched version preserves required behavior.

A vulnerable package is not an acceptable rollback target.

### .NET 10 migration

1. Retain the last verified .NET 9 artifact built with patched Negotiate.
2. Keep the patched .NET 9 shared runtime installed during the migration window.
3. On failure, restore the previous artifact through P15’s recoverable deployment mechanism and restart only the target application pool.
4. Re-run approved-user, disallowed-user, anonymous, and minimal LDAP checks.
5. Leave the .NET 10 Hosting Bundle installed unless its presence is proven to cause the failure; major runtimes are side-by-side, while the shared IIS module must still be validated.
6. Revert repository changes only through new commits. Do not rewrite history.
7. Never restore a package graph that fails the vulnerability gate.

The independent package-family commits permit Serilog or Swashbuckle rollback without undoing the .NET 10 target or Negotiate security floor.

## Risks and Mitigations

- **Windows Authentication regression:** Automated fake-auth tests cannot exercise Negotiate. Require real IIS staging tests with domain accounts before promotion.
- **LDAP role-resolution behavior:** One advisory overlaps LDAP-backed role retrieval. Exercise the same direct/nested membership patterns relied upon operationally.
- **Hosting Bundle mismatch:** Framework-dependent output will fail if the server lacks the required runtime or module. Make the P15 preflight fail closed.
- **OpenAPI breaking changes:** Swashbuckle and Microsoft.OpenApi majors may change namespaces and APIs. Isolate that upgrade and test Swagger directly.
- **Logging package changes:** Serilog major upgrades may alter configuration binding or sinks. Isolate the family and preserve existing behavior; defer topology cleanup to P16.
- **Directory API changes:** `System.DirectoryServices` remains Windows-specific and may change behavior across majors. Run representative LDAP reads under the real app-pool identity.
- **SDK pin staleness:** A feature-band pin improves repeatability but requires maintenance. Permit latest patches in the approved band and keep the vulnerability gate current.
- **Volatile version facts:** Patch releases and support data can change after this plan is reviewed. Refresh them immediately before implementation and never select a version below the documented security floor.
- **Migration scope creep:** Do not combine architecture, deployment-safety, health, LLM, logging-layout, or query-performance changes with this plan.

## Completion Criteria

P03 is complete only when:

- The urgent .NET 9 Negotiate servicing commit is independently verified and no named advisory remains.
- P01’s canonical verification is established and green.
- `global.json` selects the approved stable .NET 10 feature band.
- The project targets `net10.0-windows`.
- Required Microsoft package versions align with a patched supported .NET 10 release.
- Redundant shared-framework references are removed or each retained reference has a recorded reason.
- Serilog and Swashbuckle upgrades are independently verified.
- The resolved graph contains no reported vulnerable package.
- Framework-dependent Release publish and startup checks pass.
- The vulnerability guard has documented revert-fails/restore-passes proof.
- The full production-matched IIS and Windows Authentication matrix passes.
- Runtime and Hosting Bundle prerequisites are documented and handed to P15.
- Rollback to the last patched .NET 9 artifact has been demonstrated in staging.
- Required owner decisions are durably recorded and the plan status is explicitly changed to `Approved` before implementation begins.

## Advisory Review

### Round 1 — 2026-07-21T20:07:39Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort  
**Verdict:** Revisions required

- Combined the SDK pin, application/test target frameworks, aligned Microsoft dependencies, and regenerated lock files into one atomic migration commit as required by P01.
- Added explicit lock-file regeneration to every package-changing stage.
- Made the P01 vulnerability-gate replacement explicit, covered the pre-P01 security-patch ordering, added ClosedXML compatibility proof, and required retained Microsoft packages to align to 10.x.

### Round 2 — 2026-07-21T20:09:59Z

**Reviewer:** Headless Claude Code 2.1.216 / configured model / maximum effort  
**Verdict:** Accepted

- Confirmed the cross-plan migration and lock-file repairs and found no remaining implementation blocker.
- Assigned vulnerability-gate replacement to Stage 1 when P01 already exists and defined the pre-P01 red/green evidence path.

Record no more than three headless Claude review rounds. Each round must list the material critique, the resulting plan revision or retained disagreement, and the reviewer’s final assessment.
