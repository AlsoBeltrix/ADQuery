# slice0: P05 Slice 0 capacity-evidence harness

**Severity**: MEDIUM — a wrong measurement harness yields wrong D1 caps, but it ships no endpoint behavior and no checked-in limit defaults; the blast radius is the evidence, not production.
**Status**: Verified (Dispatch 3, accepted, guard_confirmed)
**Branch**: (none — committed directly to master; reviewed post-hoc over a pinned SHA range because Slice 0 is already committed and history rewrite is forbidden)
**Commit**: `43848b1` (harness), `0fc970c` (state)

## Evidence
Reviewed range `8e3bf1a..0fc970c`. New code under `tests/AdQueryOrchestrator.Tests/Benchmarks/`:
- `CsvCapacityByteModel.cs` — closed-form JSON/CSV/NDJSON/LDAP-filter/BER byte calculators.
- `CsvCapacityByteModelTests.cs` — cross-checks the calculators against the real encoders (`JsonSerializer` web serialization, `QueryController.GenerateFileContent`).
- `ProviderRequestMeasurement.cs` / `...Tests.cs` — drives the real `ClaudeService` through a capturing `HttpMessageHandler`; asserts request bytes are invariant to row count.
- `BatchEnrichmentModel.cs` / `...Tests.cs` — standalone model of the planned batch structures; `LdapCalls = ceil(unique/batch)`, dedup ratio, reconstruction.
- `CapacityHttpHarness.cs` / `...Tests.cs` — self-hosts the real `/api/query/csv-enrich` endpoint via `WebApplicationFactory<Program>` with faked provider/AD/writer/auth.
- `CapacityMatrixRunner.cs` / `CapacityMatrixTests.cs` — env-gated (`ADQUERY_CAPACITY_MATRIX`) matrix writing to ignored `artifacts/`.
Plus `.agents/plans/P05-slice0-capacity-evidence.md` (evidence + D1 derivation) and a `.agents/state.md` update.

## Predicted observable failure
If the harness were miswired, the failure modes that matter are: (1) the matrix runs during normal CI (violates inertness — `scripts/verify.ps1` would execute it and possibly write to `artifacts/` or contact faked deps unexpectedly); (2) the byte calculators diverge from the real encoders, so D1 caps derive from wrong numbers; (3) the HTTP harness silently hits the production output root (`E:\WWWOutput`), a live provider, or real AD. Guarded by: the matrix test skipping under `verify.ps1` (observed: SKIP, 170 executed), the cross-check tests, and the row-count-invariance test.

## What
Slice 0 of P05: a read-only capacity-evidence harness that measures CSV enrichment memory/byte/LDAP-call scaling to derive the D1 cap values, without changing endpoint behavior, committing limit defaults, or touching live provider/AD/output-root.

## Approach
Two measurement modes (actual-HTTP via `WebApplicationFactory`, benchmark-only structure model) plus closed-form byte calculators, each cross-checked against the real encoder it mirrors. Inertness via an env-gated matrix test that `Assert.Skip`s unless `ADQUERY_CAPACITY_MATRIX` is set — a skipped test does not count toward the executed-test gate, so `verify.ps1` stays inert. All external deps faked; result writer points at a temp dir.

## Files changed
See Evidence. 12 new benchmark files + 1 evidence doc + state.md update; 2085 insertions.

## Guard proof
- `CsvCapacityByteModelTests` — the 12 cross-checks assert the calculators equal the real encoders' byte counts. Previously guard-proven: injecting a spurious `,"csvData": ` field into the JSON model turned 4 tests red; restoring made them green. Confirms the guard is not vacuous.
- `ProviderRequestMeasurementTests` — row-count-invariance test proves row cell data is never sent to the provider (`digitDelta` between 10 and 100000 rows).
- Inertness: `scripts/verify.ps1` observed to SKIP the matrix test with 170 tests executed, 0 warnings, vuln audit clean.

## Coder dispute (if any)
None.

## Known gaps
- All heap/working-set figures are workstation GC (`ServerGc=False` on `ASHBIAMWEB1`); deployment uses server GC. Recorded as a caveat in the evidence doc; D1 memory-derived caps must be re-confirmed on a server-GC host.
- P06/P07 have not landed; the NDJSON figure uses P05's model of P07's canonical encoding, and the active-CSV count is a local gate. Recorded in the evidence doc.
- Reviewed post-hoc over a SHA range rather than on a `fix/` branch, because the work is already committed to master and history rewrite is forbidden by repo Git Safety invariants.

## Reviewer comments

### Dispatch 1 — FAILED (environment, not a code judgment) — 2026-07-23
`Reviewer: codex / gpt-5.6-sol / max / standard (inline, session-only)`
- Harness: codex-cli 0.144.6. Reviewed SHA 0fc970c, base 8e3bf1a.
- Dispatched `-s workspace-write` (owner-approved) for the independent worktree guard proof.
- **Outcome:** codex returned a schema-valid envelope (exit 0) but `verdict: invalid`, `guard_confirmed: false`. The reason is an environment failure, not a code judgment: codex's Windows `workspace-write` sandbox helper failed to initialize — 53 shell-command events failed with `windows sandbox: helper_unknown_error: setup refresh had errors`. It could not read the diff, create a worktree, or run verification. In substance this is a **failed dispatch**, not a verdict on the code.
- Follow-up probe: codex `-s read-only` works on this host (ran `git rev-parse`, returned 0fc970c, zero sandbox errors). The failure is specific to the write sandbox.
- Transcript: `.agents/review/dispatch.local.jsonl` (machine-local).
- **Status:** finding remains In progress / pending a working reviewer path (owner decision required).

### Dispatch 2 — FAILED (environment, not a code judgment) — 2026-07-23
`Reviewer: codex-commercial (MCP) / default configured model+effort / workspace-write`
- Transport: `codex-commercial` MCP server (tool `codex`), per owner instruction "use the codex-commercial mcp". Sandbox `workspace-write`, `approval-policy: never` — chosen to preserve the owner-approved autonomous worktree guard proof.
- **Outcome:** no verdict returned. The MCP call ran headless in the background (task `kdgtgz0bw`) and was aborted after 1800s of zero progress events (idle timeout). No verdict JSON, no transcript captured.
- **Likely cause:** the same broken Windows `workspace-write` sandbox as Dispatch 1. Read-only codex works on this host; write mode does not. The worktree guard proof requires write access, so it stalled and produced no output rather than erroring cleanly.
- In substance this is a **failed dispatch**, not a verdict on the code.
- **Status:** finding remains In progress. Two consecutive autonomous-write dispatches have now failed on the same environmental root cause across two transports (CLI, MCP). Owner decision required on the reviewer path.

### Dispatch 3 — ACCEPTED — 2026-07-23
`Reviewer: codex / (owner-run interactive, default model+effort) / workspace-write`
- Transport: owner ran codex manually in `D:\source\adquery` with write access (the two headless dispatches could not reach the worktree guard proof; the interactive session could). Verdict returned as the mandated single JSON object.
- **Verdict: accepted. `guard_confirmed: true`.** Reviewed 0fc970c against base 8e3bf1a.
- Independently re-ran the guard proof: injecting a spurious field into the JSON byte calculator made all four cross-check cases FAIL; restoring made all 12 byte-model tests PASS. Confirms the guard is not vacuous.
- Independently ran canonical verification: `verify.ps1` passed at 0fc970c — 170 executed, 1 skipped (matrix NotExecuted, total 171), warning-free build, publish smoke, vuln audit clean.
- Confirmed all four review targets: inertness (`CapacityMatrixTests.cs:17` skips before resolving artifacts; `verify.ps1:123` sums executed only), no live external access (`CapacityHttpHarness.cs:63,219` all deps faked + temp writer, not `E:\WWWOutput`; `ProviderRequestMeasurement.cs:44` capturing handler against `provider.invalid`), byte-calculator cross-checks (`CsvCapacityByteModelTests.cs:22,80` vs `JsonSerializer` and `QueryController.GenerateFileContent`), and no endpoint/config change (`P05-slice0-capacity-evidence.md:6` — no production csharp, no appsettings limit defaults).
- **Status:** VERIFIED. The harness conforms to the finding and the guard proof holds independently.
