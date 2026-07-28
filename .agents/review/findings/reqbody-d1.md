# reqbody-d1: REQBODY-D1 — restore an independent 2 MiB request-body cap

**Severity**: MEDIUM — a mis-wired or mis-sized cap either (a) fails to bound the raw request body (leaving the front-door memory-exhaustion exposure that CSV-KILL-D1 opened when it deleted the P05 Slice 2 cap), or (b) sizes/targets the limit wrongly so legitimate query/follow-up requests are rejected with 413. No auth, crypto, schema, migration, or serialization surface is touched; the change is host-options wiring plus a config knob and a guard test.

**Status**: In progress (pending review)
**Branch**: (none — committed directly to master, this repo's non-branch policy for F01/CSV-KILL/REQBODY slices; reviewed post-hoc over a pinned SHA range because history rewrite is forbidden)
**Commit**: `84ac4f6` (feat(api): restore an independent 2 MiB request-body cap (REQBODY-D1))

## Evidence
REQBODY-D1 (`.agents/decisions.md`, Approved 2026-07-28, owner: "yeah 2MB limit should suffice"). CSV-KILL-D1 (`c777a67`) removed the P05 Slice 2 transport body cap (`IISServerOptions`/`KestrelServerOptions` wiring sourced from the deleted `CsvEnrichmentLimitsOptions.MaxRequestBodyBytes`), which was the app's only request-body-size limit. This change restores a feature-independent cap:
- `csharp/Program.cs` — reads `RequestLimits:MaxRequestBodyBytes` (default `2L * 1024 * 1024` = 2,097,152) and configures both `KestrelServerOptions.Limits.MaxRequestBodySize` and `IISServerOptions.MaxRequestBodySize`.
- `csharp/appsettings.json` — `RequestLimits:MaxRequestBodyBytes: 2097152`.

Reviewed range `4d17284..84ac4f6` (single commit). Diff: 5 files. Product: `csharp/Program.cs`, `csharp/appsettings.json`. Test: `tests/AdQueryOrchestrator.Tests/Unit/RequestBodyLimitTests.cs` (new). Docs: `.agents/decisions.md`, `.agents/state.md`.

## Predicted observable failure
- **Cap not wired / reverts to host default.** If the wiring is absent, the effective IIS body cap is the host default (30,000,000 bytes) and Kestrel's is 30 MiB — neither equals 2 MiB, so the raw front-door body is effectively unbounded relative to the owner's ruling. `RequestBodyLimitTests.IisServerBodyCap_IsTwoMebibytes` / `KestrelBodyCap_IsTwoMebibytes` fail (they resolve `IOptions<IISServerOptions>` / `IOptions<KestrelServerOptions>` from the running `WebApplicationFactory<Program>` and assert `== 2 MiB`).
- **Wrong target.** Wiring only Kestrel (inert under IIS in-process hosting) without `IISServerOptions` would leave the deployed host uncapped; the IIS-targeted assertion fails.
- **Build break.** A dangling reference or wrong option type surfaces under warnings-as-errors in `scripts/verify.ps1`.

## What
REQBODY-D1: restore a transport request-body-size limit of 2 MiB, owned by the host and independent of any feature, resolving the open owner y/n left by CSV-KILL-D1 (which had removed the app's only body cap along with the CSV feature it was sourced from). The host returns 413 for an over-cap body; no application code path is required. This is unrelated to `FollowUp:MaxContextBytes`, which bounds per-turn model exposure.

## Approach
Added a config-driven cap in `Program.cs` before the Swagger registration: `RequestLimits:MaxRequestBodyBytes` (default 2 MiB) is applied to `IISServerOptions.MaxRequestBodySize` (authoritative under the current IIS in-process hosting model) and `KestrelServerOptions.Limits.MaxRequestBodySize` (inert in-process; protects a future direct/Kestrel host) — mirroring the wiring the deleted P05 Slice 2 cap used, but with a fixed feature-independent default rather than a value sourced from CSV options. The appsettings knob makes the value tunable without a rebuild.

## Files changed
- `csharp/Program.cs` — added the `maxRequestBodyBytes` config read + Kestrel/IIS options wiring.
- `csharp/appsettings.json` — added the `RequestLimits` section.
- `tests/AdQueryOrchestrator.Tests/Unit/RequestBodyLimitTests.cs` — new guard (IIS + Kestrel caps each == 2 MiB).
- `.agents/decisions.md`, `.agents/state.md` — decision + state record.

## Guard proof
- `RequestBodyLimitTests.IisServerBodyCap_IsTwoMebibytes` and `.KestrelBodyCap_IsTwoMebibytes` — resolve the configured host options from `WebApplicationFactory<Program>` and assert each equals 2,097,152.
- Non-vacuity (coder, 2026-07-28): with the wiring block removed from `Program.cs`, the filtered run went **red** — both tests failed against the host defaults (2 Failed / 0 Passed). Restoring the wiring returned both to green (2 Passed).
- Full `scripts/verify.ps1` at head `84ac4f6`: Release build 0 warnings/0 errors, 140 tests passed/0 skipped, publish smokes (401 + Swagger hidden in Production; Swagger JSON/UI in Development) + vulnerability audit clean.

## Coder dispute (if any)
None.

## Known gaps
- The guard asserts the configured option values, not an end-to-end 413 for an over-cap body. The 413 behavior is the framework's own contract for these options under IIS/Kestrel; the test proves the app sets them to 2 MiB, which is the part this change owns. An in-process `WebApplicationFactory` (TestServer) does not exercise the IIS/Kestrel body-limit middleware the way a real host does, so an end-to-end oversized-POST test would not faithfully reproduce the deployed 413 path — hence the options-value assertion.
- Under IIS in-process hosting the Kestrel limit is inert; it is set anyway to protect a future direct-Kestrel host and is guarded so the two never silently diverge.

## Reviewer comments

(pending dispatch)
