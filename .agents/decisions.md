# Settled Decisions

## P01-D3 — Establish the verification foundation directly on .NET 10

- Status: Approved
- Date: 2026-07-22
- Authority: Repository owner
- Decision: Establish the new solution, SDK pin, application target, test target, and package locks directly on .NET 10. Do not create or release an interim .NET 9 verification foundation or a standalone .NET 9 Negotiate servicing commit.
- Constraints: Pin the stable `10.0.300` SDK feature band with `latestPatch` roll-forward and prerelease SDKs disabled; target `net10.0-windows`; use exact package versions; align required Microsoft packages to a patched 10.0 servicing release; remove redundant shared-framework references only with compile and test proof; keep unrelated third-party major upgrades in separate commits; perform no deployment or IIS mutation.
- Consequence: P01 Slice 1 absorbs P03's local SDK/runtime/Microsoft-package migration scope and must finish with a zero-vulnerability resolved graph. P03's proposed .NET 9 Stage 1 is superseded, while its later third-party, documentation, and production-matched acceptance work remains independently attributable.

## P01-D1 — CI host and Windows runner

- Status: Approved
- Date: 2026-07-22
- Authority: Repository owner
- Decision: GitHub is the authoritative merge host. P01 Slice 6 will add one GitHub Actions workflow using `windows-latest` and will invoke the unchanged repository-root `scripts/verify.ps1` entry point rather than duplicate verification commands.
- Constraints: Do not add CI to Gitea, do not maintain a second workflow for the Gitea remote, and do not configure branch protection, repository secrets, or other external GitHub state without separate authorization.
- Consequence: The local GitHub remote is named `origin`; the Gitea remote remains configured as `gitea`. P01 Slice 6 and its checked-in workflow are authorized.

## P01-D2 — Existing-file formatting baseline

- Status: Approved
- Date: 2026-07-22
- Authority: Repository owner
- Decision: Normalize existing C# whitespace once in an isolated formatter-only commit before functional fixes, then enforce `dotnet format ADQuery.sln whitespace --verify-no-changes --no-restore` through the canonical verification script.
- Constraints: The normalization commit must be mechanically whitespace-only, independently reviewable, and verified before any functional slice begins. It must not include analyzer or code-style rewrites that can change behavior.
- Consequence: P01 Slice 4 may perform the one-time normalization, and Slice 5 may enable the repository-wide whitespace gate after that normalization is proven.
