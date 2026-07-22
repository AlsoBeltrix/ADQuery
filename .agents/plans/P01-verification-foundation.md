# P01 — Verification Foundation and CI

Status: **Reviewed — implementation is not authorized**

Owner approval: P01-D2 approved; P01-D1 and full-plan approval pending

Implementation dependency: This foundation should land before behavior-changing plans P02 and P04–P21. The urgent dependency-security work in P03 may land first if necessary, using the existing build and package-audit commands.

Review status: Accepted after 2 advisory rounds

## Problem

The repository has one ASP.NET Core application project but no automated test project, solution file, pinned .NET SDK, repository-wide analyzer configuration, canonical verification script, or checked-in CI workflow. The existing verification entry point only compiles the application, so regressions in security validation, request construction, concurrency, serialization, CSV handling, and deployment behavior can compile successfully and remain undetected.

The application is Windows-specific and integrates with Active Directory, Windows Authentication, an external LLM endpoint, IIS, the filesystem, and a Python analysis tool. Verification must keep deterministic tests separate from credentialed or environment-dependent checks.

## Repository evidence

- `csharp/AdQueryOrchestrator.csproj` is the only project file.
- The application targets `net9.0-windows`.
- The application references `System.DirectoryServices` and configures Negotiate authentication, so its supported execution and integration-test environment is Windows.
- No `*.sln`, `*.slnx`, `global.json`, test project, `Directory.Build.props`, or repository `.editorconfig` exists.
- No checked-in GitHub Actions, Azure Pipelines, GitLab CI, or other CI definition exists.
- The canonical command currently recorded in `.agents/repo-guidance.md` is: `dotnet build csharp/AdQueryOrchestrator.csproj -c Release --nologo`.
- `.agents/repo-guidance.md` records that no automated test or CI entry point exists and requires each behavior-changing plan to add a focused regression guard.
- `PlanPreprocessor` is deterministic and does not require Active Directory or network access, making it suitable for initial characterization tests.
- `ClaudeService` accepts an injected `HttpClient`, and several other application components already expose interfaces. Later plans can therefore test external interactions with in-process fakes instead of live services.
- `.gitignore` does not currently exclude repository-level test-result or coverage-output directories.
- The canonical repository host and the CI runner platform are not documented. A CI provider must not be inferred from remote names alone.

## Goals

1. Provide one repository-root command that restores, analyzes, builds, tests, and reports dependency vulnerabilities.
2. Pin the SDK feature band used by local and automated verification.
3. Add a conventional solution and deterministic test project.
4. Establish initial characterization tests without contacting Active Directory, IIS, or an LLM provider.
5. Enable compiler and .NET analyzer failures as build failures without suppressing unexplained diagnostics.
6. Enforce a stable formatting baseline.
7. Make CI invoke the same script used locally rather than duplicating verification logic.
8. Produce machine-readable test and coverage artifacts.
9. Leave clear extension points for later JavaScript, PowerShell, and Python test suites.
10. Replace the temporary build-only verification guidance with the canonical command after that command is proven.

## Non-goals

- Do not fix the runtime, dependency, security, performance, concurrency, or correctness findings owned by P02–P21.
- Do not upgrade the application target framework or application dependencies; P03 owns those changes.
- Do not require live Active Directory access, Windows domain membership, IIS, deployed secrets, or external LLM calls in the default verification command.
- Do not establish a numerical code-coverage gate before representative tests exist.
- Do not add broad mocking frameworks when small in-process fakes or framework types are sufficient.
- Do not configure branch protection, repository secrets, hosted runners, or other external state as part of repository implementation.
- Do not silently suppress analyzer, compiler, restore, or test failures to make verification green.
- Do not combine behavior fixes discovered while adding characterization tests into this plan.

## Technical design

### Repository structure

Add:

```text
ADQuery.sln
global.json
Directory.Build.props
.editorconfig
scripts/
  verify.ps1
tests/
  AdQueryOrchestrator.Tests/
    AdQueryOrchestrator.Tests.csproj
    Unit/
      PlanPreprocessorTests.cs
```

Add both the application and test projects to `ADQuery.sln`.

Use a conventional `.sln` rather than `.slnx` for the initial foundation because the repository currently has no pinned tooling baseline and `.sln` has broader compatibility across supported .NET and Windows development tools. Conversion to `.slnx` is not necessary for verification correctness.

### SDK selection

Before adding the pin, confirm that a stable `9.0.3xx` SDK is installed on the implementation machine and record that machine-specific fact in `.agents/machines.md`. Add `global.json` with the .NET 9 `9.0.300` feature band and `rollForward` set to `latestPatch`. This matches the current `net9.0-windows` target while allowing patched SDK servicing releases within the same feature band; it deliberately does not cross to another feature band.

P03 must update `global.json` in the same commit that changes the target framework to .NET 10. P01 must not perform that runtime migration.

### Shared build policy

Create `Directory.Build.props` in Slice 1 with only the properties required for reproducible restore/build:

- `RestorePackagesWithLockFile` enabled.
- Deterministic builds enabled.
- Continuous-integration build metadata enabled only when the caller sets an explicit `ContinuousIntegrationBuild` property.

Slice 1 must not set analyzer level or warning policy. Extend that same file in Slice 3 with:

- `Nullable` enabled.
- `ImplicitUsings` enabled.
- `EnableNETAnalyzers` enabled.
- `AnalysisLevel` set to the pinned SDK's recommended analysis level.
- `TreatWarningsAsErrors` enabled.

The root warning policy also applies to the test project. Both application and characterization-test code must be warning-clean under the selected analyzer set before Slice 3 can land.

Do not add a blanket `NoWarn`. If enabling recommended analyzers exposes existing diagnostics:

1. Classify each diagnostic.
2. Fix only non-behavioral, unambiguous diagnostics within an isolated commit.
3. Route diagnostics requiring semantic or architectural changes to the owning P02–P21 plan.
4. Use a narrowly scoped `.editorconfig` severity override only when a documented false positive or intentional rule conflict is proven.
5. Leave the foundation blocked if unexplained errors remain.

Package lock files for both projects must be committed. Normal dependency updates regenerate them intentionally; verification restores in locked mode.

### Test project

Create `tests/AdQueryOrchestrator.Tests/AdQueryOrchestrator.Tests.csproj` with:

- Target framework `net9.0-windows`.
- A project reference to `csharp/AdQueryOrchestrator.csproj`.
- The SDK-provided xUnit template package set, pinned to explicit stable versions compatible with `net9.0-windows`.
- `Microsoft.NET.Test.Sdk`.
- `xunit`.
- `xunit.runner.visualstudio` marked as a private asset.
- `coverlet.collector` marked as a private asset.

Before committing package versions, check them with the repository vulnerability-audit command. Do not add a general-purpose mocking package. Later plans should prefer fake `HttpMessageHandler`, in-memory configuration, temporary directories, and small test doubles implementing existing interfaces.

Initial tests must characterize existing `PlanPreprocessor` behavior:

1. `EnsurePlanLimit` applies the requested positive limit to `DirectoryQueryPlan.ResultLimit`.
2. `EnsurePlanLimit` does not raise an existing stricter plan limit.
3. `EnsurePlanLimit` applies the effective limit to the projection row's search step.
4. `EnsurePlanLimit` does not raise an existing stricter step limit.
5. `ApplyCustomMappings` recursively normalizes filter operators.
6. `ApplyCustomMappings` trims filter attributes and values.
7. A configured license alias maps to `extensionAttribute11`.
8. Projection filters receive the same normalization as step filters.

Construct configuration with `ConfigurationBuilder().AddInMemoryCollection(...)`. The custom-alias case sets `CustomMappings:ExtensionAttributes:ExtensionAttribute11` to the alias under test. Do not read `appsettings.json`, environment variables, domain state, or machine paths in these tests.

These are characterization tests, not approval of every current behavior. Any current behavior believed incorrect must be documented under its owning plan rather than changed here.

### Formatting policy

Add a root `.editorconfig` defining only repository-wide mechanical conventions initially:

- UTF-8 text.
- Final newline.
- Four-space indentation for C# and PowerShell.
- Two-space indentation for JSON and YAML.
- No trailing whitespace.
- C# conventions compatible with the SDK formatter.
- An explicit line-ending policy selected in the owner decision below.

Do not mix mechanical formatting and behavior changes. If the owner approves normalization, apply `dotnet format ADQuery.sln whitespace --no-restore` once in an isolated commit, verify that the diff is whitespace-only, and then add this enforcement stage to the canonical script:

```powershell
dotnet format ADQuery.sln whitespace --verify-no-changes --no-restore
```

If the owner declines normalization, the canonical script omits `dotnet format --verify-no-changes`; `.editorconfig` governs new and manually touched text, but there is no claim of a repository-wide formatting gate. Do not invent a suppressions baseline. Do not run an unrestricted analyzer/code-style rewrite because some analyzer fixes can change behavior.

### Canonical verification script

Add `scripts/verify.ps1`. It must:

- Be invokable from any working directory.
- Resolve the repository root relative to `$PSScriptRoot`.
- Work under PowerShell 7 on a Windows runner.
- Set terminating PowerShell errors.
- Inspect `$LASTEXITCODE` after every native command and throw on nonzero exit.
- Restore the solution in locked mode.
- Verify formatting without modifying files only when the owner-approved normalization slice has landed.
- Build the entire solution in Release with no restore and warnings treated as errors.
- Run all tests in Release with no rebuild or restore.
- Write TRX and Cobertura coverage output beneath `artifacts/test-results/`.
- Run the dependency vulnerability report for direct and transitive packages.
- Restore the caller's working directory in `finally`.
- Return a nonzero process exit code for any failed required stage.

The canonical invocation is:

```powershell
pwsh -NoLogo -NoProfile -File scripts/verify.ps1
```

The script's required sequence after owner-approved normalization is:

```text
dotnet restore ADQuery.sln --locked-mode
dotnet format ADQuery.sln whitespace --verify-no-changes --no-restore
dotnet build ADQuery.sln -c Release --no-restore --nologo -warnaserror
dotnet test ADQuery.sln -c Release --no-build --no-restore --nologo
dotnet list ADQuery.sln package --vulnerable --include-transitive
```

If normalization is declined, omit the `dotnet format` line and record that formatting is not a canonical verification stage. All remaining stages are unchanged.

The test command must add a TRX logger, a deterministic results directory, and `XPlat Code Coverage`.

The package-list command returns success even when it reports vulnerabilities, so the generic native-exit check is not a vulnerability gate. During P01, run machine-readable audits for the application graph and the solution graph, parse both, and fail only when the solution introduces a vulnerable `(package, resolved version, advisory)` tuple not already present in the application graph. Always print the known application findings. This temporary attribution check proves test tooling adds nothing while P03 owns the existing application vulnerability. After P03 removes that finding, P03 must replace the comparison with an unconditional zero-vulnerability gate.

Add `/artifacts/` to `.gitignore`. CI may publish those files, but they must not be committed.

### CI adapter

After the owner identifies the repository host and runner, add one minimal workflow for that platform:

1. Check out the repository.
2. Install the SDK specified by `global.json`.
3. Ensure PowerShell 7 is available.
4. Cache only NuGet's package cache, keyed by all project files, lock files, and `global.json`.
5. Invoke `scripts/verify.ps1` without reproducing its dotnet commands in workflow YAML.
6. Publish TRX and Cobertura artifacts even when tests fail.
7. Use a Windows runner.
8. Grant read-only repository permissions unless artifact publication requires a narrowly scoped additional permission.
9. Do not load deployment or LLM secrets.
10. Do not contact Active Directory or the configured LLM endpoint.

The workflow must be pull-request and branch-update verification only. Deployment remains owned by P15.

A required-check or branch-protection change is external state and needs separate explicit authorization after the checked-in workflow succeeds.

## Dependency ordering

```text
P03 urgent package patch may land independently
              |
              v
P01 SDK/solution/test/verification foundation
              |
              +--> P02, P04–P14, P17–P21 add focused automated guards
              |
              +--> P15 may extend verification with Pester
              |
              +--> P17 may extend verification with Python tests
              |
              +--> P18–P19 may extend verification with pinned JavaScript tooling
```

- P01 must not absorb tests for every later finding.
- Each later plan must add its own focused guard and extend `scripts/verify.ps1` only when a new test runtime is required.
- P03 must update the SDK pin, test target, and solution lock files together with the runtime migration.
- P15, P17, P18, and P19 must pin any additional PowerShell, Python, or JavaScript tooling they introduce.

## Implementation slices

Each numbered slice is one commit. Do not start the next slice until the current slice is verified and committed.

### Slice 1 — Pin tooling and establish the solution

Commit intent: `build: pin sdk and establish solution`

- Add `global.json`.
- Add `ADQuery.sln`.
- Add the existing application project.
- Add `Directory.Build.props` containing only `RestorePackagesWithLockFile`, deterministic-build behavior, and conditional CI metadata; do not enable analyzers, analysis level, or warning-as-error policy in this slice.
- Add the application package lock file.
- Run locked restore and the existing Release build.
- Confirm project output is unchanged except for deterministic build metadata.

Verification:

```powershell
dotnet --version
dotnet restore ADQuery.sln
dotnet restore ADQuery.sln --locked-mode
dotnet build ADQuery.sln -c Release --no-restore --nologo
```

### Slice 2 — Add the test host and characterization guards

Commit intent: `test: establish deterministic unit test project`

- Add the xUnit project and project reference.
- Add explicit, audited test package versions.
- Add the `PlanPreprocessor` characterization tests.
- Add the test project to the solution.
- Commit the test-project lock file.
- Add `/artifacts/` to `.gitignore`.

Verification:

```powershell
dotnet restore ADQuery.sln
dotnet restore ADQuery.sln --locked-mode
dotnet build ADQuery.sln -c Release --no-restore --nologo
dotnet test ADQuery.sln -c Release --no-build --no-restore --nologo
dotnet list ADQuery.sln package --vulnerable --include-transitive
```

Guard proof:

- For the stricter-limit test, temporarily replace the `Math.Min` choice in `PlanPreprocessor.EnsurePlanLimit` with the requested limit. Run only the affected test and confirm it fails. Restore the source and confirm it passes.
- For the alias test, temporarily bypass the assignment to `extensionAttribute11`. Run only the affected test and confirm it fails. Restore the source and confirm it passes.
- Leave no mutation in the worktree.
- Record the exact failing and passing filtered commands in the commit evidence or plan progress record.

### Slice 3 — Enable analyzers and mechanical conventions

Commit intent: `build: enforce analyzer and editor baselines`

- Extend the existing `Directory.Build.props`; do not create a competing props file.
- Add `.editorconfig`.
- Enable the agreed analyzer policy.
- Confirm both the application and test project are warning-clean under the root policy.
- Retain command-line `-warnaserror` as an intentional defense in depth even though the root props file sets `TreatWarningsAsErrors`.
- Resolve only unambiguous non-behavioral diagnostics.
- If semantic fixes are required, stop and route them to their owning plans rather than weakening the gate.

Verification:

```powershell
dotnet restore ADQuery.sln --locked-mode
dotnet build ADQuery.sln -c Release --no-restore --nologo -warnaserror
dotnet test ADQuery.sln -c Release --no-build --no-restore --nologo
```

### Slice 4 — Normalize existing whitespace

Owner decision required before this slice.

Commit intent: `style: normalize repository whitespace`

- Run the SDK formatter's whitespace pass.
- Include no analyzer fix, rename, refactor, generated file, or behavior change.
- Inspect the entire diff and confirm it is mechanical.
- Run build and tests after normalization.

Verification:

```powershell
dotnet format ADQuery.sln whitespace --no-restore
dotnet format ADQuery.sln whitespace --verify-no-changes --no-restore
dotnet build ADQuery.sln -c Release --no-restore --nologo -warnaserror
dotnet test ADQuery.sln -c Release --no-build --no-restore --nologo
```

### Slice 5 — Add the canonical verification entry point

Commit intent: `build: add canonical verification command`

- Add `scripts/verify.ps1`; include format verification only if Slice 4 landed, and otherwise omit that stage explicitly.
- Run it from the repository root and from a different working directory.
- Confirm native-command failures propagate as a nonzero script exit.
- Update `.agents/repo-guidance.md` so the script becomes the sole canonical automated verification command.
- Retain the dependency-change audit note, pointing to the script rather than duplicating its commands.

Verification:

```powershell
pwsh -NoLogo -NoProfile -File scripts/verify.ps1
Push-Location csharp
try {
    pwsh -NoLogo -NoProfile -File ..\scripts\verify.ps1
} finally {
    Pop-Location
}
```

Failure propagation check:

- Temporarily make one isolated test assertion fail.
- Run `scripts/verify.ps1` and confirm a nonzero exit.
- Restore the test.
- Run the full script and confirm success.
- Do not commit the temporary failure.

### Slice 6 — Add the CI adapter

Owner decision required before this slice.

Commit intent: `ci: run canonical verification on windows`

- Add the selected provider's minimal workflow.
- Invoke only `scripts/verify.ps1`.
- Upload test and coverage artifacts.
- Confirm a normal run passes.
- In a temporary branch or uncommitted workflow test change, prove a test failure makes the CI job fail; restore it without rewriting committed history.
- Do not enable deployment, secrets, or live integration checks.

External branch protection remains blocked until separately authorized.

## Red/green guard strategy

Every later behavior-fix plan must use this foundation as follows:

1. Add a focused test that reproduces the reported failure.
2. Run it against the unmodified behavior and record the expected failure.
3. Implement the smallest fix.
4. Run the focused test and record success.
5. Temporarily revert only the implementation change, without rewriting history, and confirm the test fails.
6. Restore the implementation change and run `scripts/verify.ps1`.
7. Commit the single finding only after the focused and full guards pass.

For characterization tests added by P01, use the temporary production mutations specified in Slice 2. A test that continues passing after its targeted behavior is disabled is vacuous and must be replaced.

Live integration checks must be separate, opt-in commands with explicit prerequisites. They must never be silently skipped while reporting success.

## Acceptance criteria

- `ADQuery.sln` contains the application and test projects.
- `global.json` resolves a patched SDK in the selected .NET 9 feature band.
- Locked restore succeeds from a clean package state.
- Both projects have committed package lock files.
- The initial deterministic tests pass without network, domain, IIS, or LLM access.
- The initial tests have recorded mutation-based guard proof.
- Release build fails on compiler or enabled analyzer warnings.
- No blanket warning suppression was introduced.
- If normalization was approved, formatting verification is non-mutating and passes; if it was declined, the canonical script explicitly has no repository-wide formatting stage.
- `scripts/verify.ps1` succeeds from the repository root and another working directory.
- A forced test failure produces a nonzero script exit.
- TRX and Cobertura files are produced beneath ignored `artifacts/test-results/`.
- Parsed application-versus-solution audit output reports no vulnerability introduced by test tooling, without treating the package command's zero exit code as proof.
- `.agents/repo-guidance.md` names `scripts/verify.ps1` as the canonical command.
- The selected CI workflow runs on Windows and calls the canonical script.
- A deliberately failing test fails the CI job.
- CI requires no production secret or network access beyond package restore.
- No application behavior finding from P02–P21 was changed.
- The worktree is clean after every committed slice.

## Rollback

Each slice is independently reversible with a new revert commit; do not rewrite history.

- Removing the CI adapter leaves local verification intact.
- Removing the verification script requires restoring the prior build command in `.agents/repo-guidance.md` in the same revert.
- Reverting whitespace normalization does not affect tests or build policy but will make the formatting gate fail; revert the gate and normalization together only if the owner explicitly abandons formatting enforcement.
- Reverting analyzer policy must not retain undocumented suppressions.
- Reverting the test project removes regression protection and must also restore the prior verification guidance.
- Runtime or dependency rollback belongs to P03, not this plan.

## Risks and mitigations

- **Windows-only test target limits runner choice.** Use a Windows runner; do not pretend pure unit tests prove Linux support for a Windows-targeted application.
- **A one-time formatter pass creates a large blame-only diff.** Isolate it in one commit before functional changes and inspect it for mechanical changes only.
- **Recommended analyzers may expose semantic findings.** Stop and route those findings to their owning plans; do not weaken the gate or mix fixes into the foundation.
- **SDK pinning can become stale.** P03 owns the near-term .NET 10 update; future SDK changes must update `global.json`, target frameworks, and lock files together.
- **Package locks create maintenance work.** Regenerate and audit them only in intentional dependency commits.
- **Coverage can look authoritative before it is representative.** Publish coverage without a percentage gate; add a threshold only after representative component suites exist.
- **CI YAML can diverge from local verification.** Keep all commands in `scripts/verify.ps1` and make CI a thin adapter.
- **External-service tests can become flaky or leak credentials.** Keep them opt-in and outside the default verification path.
- **The current vulnerability remains visible during P01.** Do not baseline it away; P03 must remove it and make vulnerability detection a hard failure.

## Open owner decisions

### Decision 1 — CI host and Windows runner

The repository does not identify which host controls merges or which Windows runner is available. Choose the merge-controlling host and its supported Windows runner for Slice 6. Recommendation: add CI only on that authoritative host and have it call `scripts/verify.ps1`; do not maintain duplicate workflows.

Blocked until decided: Slice 6 and any external required-check configuration.

### Decision 2 — Existing-file formatting baseline

Choose whether to normalize existing C# whitespace once or enforce formatting only after a manually maintained baseline. Recommendation: perform one isolated formatter-only commit before functional fixes because the repository is currently inactive; the cost is a large blame-only diff, while the benefit is a simple deterministic gate.

Decision: Approved on 2026-07-22. The canonical record is `.agents/decisions.md` under `P01-D2 — Existing-file formatting baseline`.

Result: Slice 4 will perform one isolated whitespace-only normalization commit, and Slice 5 will enable `dotnet format ... --verify-no-changes` after that commit is proven.

## Review history

| Round | UTC | Reviewer | Outcome | Changes |
|---:|---|---|---|---|
| 1 | 2026-07-21T19:57:44Z | Headless Claude Code 2.1.216, configured model, maximum effort | Revisions required | Split reproducibility from analyzer properties across Slices 1 and 3; made formatting verification conditional on the owner's normalization choice; defined parsed vulnerability attribution; added SDK feature-band and test-warning checks. |
| 2 | 2026-07-21T19:59:48Z | Headless Claude Code 2.1.216, configured model, maximum effort | Accepted | Confirmed both required repairs; retained command-line warning enforcement intentionally and made the alias-test configuration key explicit. |
