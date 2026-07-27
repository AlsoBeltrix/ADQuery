# slice-c1: F01 Slice C1 — follow-up context byte cap + server-side enforcement

**Severity**: MEDIUM — a mis-sized or unenforced cap would let over-limit follow-up context be persisted, logged, and transmitted to the model, or (via a wrong drop order / code-point split) ship a corrupt or oversized context fragment. Server-side data-path change (persistence/logging/transmission input); no auth, crypto, schema, migration, or wire-format path touched.
**Status**: Verified — round 1 reopened (retry enqueue path unenforced), repaired `8549ecc`, round 2 accepted/guard_confirmed (awaiting owner-gated merge)
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

### Round 1 — reopened (2026-07-27)

Reviewer: codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh/standard (`--profile review`, danger-full-access, owner-authorized 2026-07-27). Dispatched headless one-shot from the agent's own tool (no owner `!` relay). Reviewed `95bb721..8b716b5`.

Verdict: **reopened**, `guard_confirmed: true`. Envelope validated fail-closed: exit 0, single schema-valid JSON, `verdict` in enum, `reviewed_sha`==`8b716b5`, `base_sha`==`95bb721`. (Post-run `codex_login` token-refresh ERROR lines are the documented Portkey noise after `turn.completed`, not an auth failure.)

Material defect (accepted — the reviewer is right; my finding wrongly filed this as an out-of-scope "known gap"):
- `csharp/Controllers/QueryController.cs:1324` + `csharp/Services/QueryJobManager.cs:104,107` — the retry-with-alternate-model endpoint is a **second client-reachable persistence path**. It builds a `QueryJob` directly from `originalJob.Context` and calls `EnqueueJobAsync`, which appended `[FORCE_MODEL: …]` to `job.Context` and stored it **without** `EnforceStored`. An authenticated owner of an in-cap job can retry it and persist/log context exceeding `FollowUp:MaxContextBytes`. Observable failure: the retry job's `Context` UTF-8 byte count exceeds the cap and the job log writes the over-cap context.

Additional defect found during repair (coder): `EnqueueJobAsync` appended the directive without stripping any prior one, so retry-of-a-retry **chained** `[FORCE_MODEL: …]` directives — unbounded context growth across repeated retries, independent of the cap breach.

### Repair (coder, 2026-07-27) — commit `8549ecc`

`EnqueueJobAsync` now strips any prior `FORCE_MODEL` directive, bounds the user context via `EnforceStored` (fail-closed: dropped whole if over cap), then appends one fresh directive after enforcement. The directive regex is centralized in a single compiled static (`ForceModelDirective`) shared by the append and consume sites. Added three guards to `QueryJobManagerContextEnforcementTests`: `EnqueueJobAsync_OverCapContext_DropsUserContext_KeepsDirective`, `EnqueueJobAsync_InBoundsContext_BoundsAndAppendsDirective`, `EnqueueJobAsync_RepeatedRetry_DoesNotChainDirectives`. Proven non-vacuous: bypassing strip+enforce fails the over-cap-drop and no-chain tests (over-cap persists the raw "xxx…"); restoring passes. Full `scripts/verify.ps1`: 240 passed, 1 skipped, 0 warnings, publish smoke + vuln audit clean (up from 237).

### Round 2 — accepted (2026-07-27, repair-delta redispatch)

T5 note: a reopen escalates one tier on redispatch, but this machine has no owner-confirmed frontier tier→pair (`harnesses.local.json` `"tiers": {}`). Per the playbook fail-closed rule the frontier dispatch was surfaced to the owner, who authorized re-checking the repair on the available standard-tier codex reviewer (owner ruling 2026-07-27, one-line y/n ask). Recorded as a standard-tier repair-delta redispatch, not a satisfied T5 escalation.

Reviewer: codex/@azure-openai-eus2-global/gpt-5.5-dzs/xhigh/standard (`--profile review`, danger-full-access, owner-authorized). Repair-delta re-review of `8b716b5..8549ecc` (base = pre-repair head, head = post-repair), mandate narrowed to closing the reopened defect + no adjacent regression.

Verdict: **accepted**, `guard_confirmed: true`. Envelope validated fail-closed: exit 0, single schema-valid JSON, `verdict` in enum, `reviewed_sha`==`8549ecc`, `base_sha`==`8b716b5`.

Reviewer's own worktree guard proof (detached worktree at `8549ecc`): shipped `QueryJobManagerContextEnforcementTests` passed 6/6; replacing the `EnqueueJobAsync` fix with `job.Context` failed the over-cap and repeated-retry guards; restore → 6/6; `scripts/verify.ps1` passed (240 passed, 1 skipped, 0 warnings, publish smoke + vuln audit clean). Matches the coder's repair non-vacuity result.

Confirming comments (no defects): `QueryJobManager.cs:115` closes the retry over-cap path and repeated-retry directive chaining; `QueryJobManager.cs:253` consume site uses the same regex shape, still strips the directive before `GenerateExecutionPlanAsync`, and append-after-enforcement ordering is correct because the directive is server-generated control metadata.

Merge remains owner-gated (accepted ≠ merge authority).
