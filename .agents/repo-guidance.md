# Repository Guidance

This file extends `AGENTS.md` with repository-specific facts and commands.

## Verification

- Canonical automated verification: `pwsh -NoLogo -NoProfile -File scripts/verify.ps1`.
- Run the canonical command for every code or dependency change. It validates the SDK and target-framework contract, restores locked dependencies, verifies formatting, builds with warnings as errors, executes the full test suite with TRX/Cobertura output, and parses direct/transitive vulnerability reports as a zero-finding gate.
- Test and dependency-audit artifacts are written beneath ignored `artifacts/` paths.
- Every behavior-changing plan must add a focused regression guard and prove that guard fails when its targeted behavior is temporarily disabled.
