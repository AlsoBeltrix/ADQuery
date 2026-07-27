# slice-c1: F01 Slice C1 — follow-up context byte cap + server-side enforcement

**Severity**: MEDIUM — a mis-sized or unenforced cap would let over-limit follow-up context be persisted, logged, and transmitted to the model, or (via a wrong drop order / code-point split) ship a corrupt or oversized context fragment. Server-side data-path change (persistence/logging/transmission input); no auth, crypto, schema, migration, or wire-format path touched.
**Status**: In progress — pending review
**Branch**: (none — committed directly to master, this repo's non-branch policy for F01 slices; reviewed post-hoc over a pinned SHA range because history rewrite is forbidden)
**Commit**: `8b716b5` (feat(followup): enforce server-side context byte cap (F01 Slice C1))

## Evidence
Reviewed range `95bb721..8b716b5`. Diff (11 files, +538/-1):
- `csharp/Configuration/FollowUpOptions.cs` (new) — `FollowUpOptions` with `SectionName="FollowUp"`, `ContextTransportCodeUnitLimit=2000`, `MaxContextBytes` defaulting to the transport limit.
- `csharp/Configuration/FollowUpOptionsValidator.cs` (new) — `IValidateOptions<FollowUpOptions>`: fails `<=0` ("zero never means unlimited") and `>ContextTransportCodeUnitLimit`.
- `csharp/Configuration/FollowUpServiceCollectionExtensions.cs` (new) — `AddFollowUpConfiguration` binds the section with `.ValidateOnStart()` and registers the validator.
- `csharp/Services/FollowUpContextEnforcer.cs` (new) — `IFollowUpContextEnforcer`: `Compose` (fixed drop order values→plan→prior-question, never splits a UTF-8 code point, drops entirely if the highest-priority component alone overflows) and `EnforceStored` (opaque over-cap string dropped whole, never a fragment).
- `csharp/Services/QueryJobManager.cs:62` — single enforcement point: `EnforceStored(context)` before the `QueryJob` is built (before persistence/logging/transmission).
- `csharp/Program.cs` — `AddFollowUpConfiguration` + `AddSingleton<IFollowUpContextEnforcer, FollowUpContextEnforcer>`.
- `csharp/Controllers/QueryController.cs:405` — `/config` exposes advisory `followUpMaxContextBytes` (server value authoritative).
- `csharp/appsettings.json` — `FollowUp:MaxContextBytes` = 2000.
- 3 new test files (enforcer, options/host-startup, CreateJobAsync enforcement point).

## Predicted observable failure
If `CreateJobAsync` did not enforce the cap, an over-cap client context would be persisted verbatim and later logged/transmitted — guarded by `QueryJobManagerContextEnforcementTests.CreateJobAsync_OverCapContext_StoresBoundedContext` (reverting the `EnforceStored` call persists the 100-char over-cap string; enforcement stores null). If `Compose` dropped components in the wrong order or split a code point, refinement context would be corrupt or oversized — guarded by `FollowUpContextEnforcerTests` (drop-order, no-split, whole-drop). If the cap were mis-configured (0, negative, or above the `[StringLength(2000)]` transport guard), the host would silently accept an unusable value — guarded by `FollowUpOptionsTests` (validation + `ProductionRegistration_InvalidCapFailsHostStartup`).

## What
F01 Slice C1 (FOLLOWUP-D1/D2): the authoritative server-side UTF-8 byte bound on follow-up context. A configurable, startup-validated cap (`FollowUp:MaxContextBytes`, default = the 2000 transport code-unit guard) is enforced at exactly one point — `QueryJobManager.CreateJobAsync` — before the job's context is persisted, logged, or handed to model transmission. Over-cap opaque context is dropped whole (fail-closed), never truncated into a fragment. `Compose` (for the C2 last-turn builder) drops whole components in a fixed priority and never splits a UTF-8 code point.

## Approach
Options-pattern trio mirrors the repo's established `CsvEnrichmentLimits` idiom (`SectionName` const, `IValidateOptions<T>` + `.ValidateOnStart()`, "zero never means unlimited"). The validator ceiling equals `ContextTransportCodeUnitLimit` so an in-bounds byte input can never be pre-empted by the binding-time `[StringLength(2000)]` on `QueryRequest.Context` (each UTF-16 code unit is ≥1 UTF-8 byte). `FollowUpContextEnforcer` measures UTF-8 bytes (`Encoding.UTF8.GetByteCount`); `Compose` tries candidate subsets from most- to least-complete and returns the first that fits (whole components only — no split); `EnforceStored` is the fail-closed backstop for an already-assembled opaque string. `CreateJobAsync` is the single enforcement point for the shipped async path (`EnqueueJobAsync` retry and the `QueryController` direct-construction path are out of scope per the plan). `/config` carries an advisory copy for client pre-truncation UX only.

## Files changed
- `csharp/Configuration/FollowUpOptions.cs:1` — options + constants.
- `csharp/Configuration/FollowUpOptionsValidator.cs:1` — startup validation.
- `csharp/Configuration/FollowUpServiceCollectionExtensions.cs:1` — bind + validate + register.
- `csharp/Services/FollowUpContextEnforcer.cs:1` — enforcer (`Compose`/`EnforceStored`).
- `csharp/Services/QueryJobManager.cs:62` — single enforcement point.
- `csharp/Program.cs` — DI registration.
- `csharp/Controllers/QueryController.cs:405` — advisory `/config` copy.
- `csharp/appsettings.json` — `FollowUp` section.
- `tests/AdQueryOrchestrator.Tests/Unit/FollowUpContextEnforcerTests.cs` (new) — enforcer guards.
- `tests/AdQueryOrchestrator.Tests/Unit/FollowUpOptionsTests.cs` (new) — options/validation/host-startup guards.
- `tests/AdQueryOrchestrator.Tests/Unit/QueryJobManagerContextEnforcementTests.cs` (new) — enforcement-point guard.

## Guard proof
- `QueryJobManagerContextEnforcementTests`, `FollowUpContextEnforcerTests`, `FollowUpOptionsTests` — green at head `8b716b5`.
- Non-vacuity (coder, 2026-07-27): disabling `EnforceStored` in `CreateJobAsync` (store `context` verbatim) turned `CreateJobAsync_OverCapContext_StoresBoundedContext` red (persists the "xxx…" over-cap string); restricting `Compose` to the `keepAll` candidate only turned the two drop-order enforcer tests red. Both temporary edits restored, all green.
- Full `scripts/verify.ps1` at head: 237 passed, 1 skipped, 0 warnings, publish smoke (401 + Swagger hidden in Production; Swagger JSON/UI in Development) + vuln audit clean (up from 228 — the new C1 unit tests).

## Coder dispute (if any)
None.

## Known gaps
- `EnqueueJobAsync`'s retry path and the direct `QueryJob` construction in `QueryController` do not pass through `EnforceStored`; the plan designates `CreateJobAsync` as the single enforcement point for the shipped async query path. Recorded here for the reviewer to grade explicitly.
- `MaxContextBytes` default 2000 is the transport-permitted maximum, not a measured "typical" size; C2 will size the operative value from a real assembled payload per the plan's open item.
- `Compose` is not yet wired into a caller — C2 introduces the last-turn builder that consumes it. C1 ships and guards it as the byte-bound primitive so C2 depends on reviewed code.

## Reviewer comments
_(pending dispatch)_
