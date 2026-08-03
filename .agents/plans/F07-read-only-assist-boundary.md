# F07 — The assist path cannot write, and a guard proves it

**Status: DEFERRED (2026-08-03, F06-D1).** Owner: *"no. defer integration. this app needs to
stand on its own properly first."* Do not start any slice here without a fresh owner go.
Deferring closes F06-Q2/Q3/Q4 as a set rather than answering them; reopening the integration
reopens all three. The document is kept whole so the analysis is not redone from scratch.

**Original status line: Draft — specification only, no owner approval to implement.** This is the design
F06-Q2 concluded was needed: if the LLM becomes a service other ExchangeAdminWeb modules
consume, the owner's condition — *"we'd have to ensure that there's no LLM to AD/Exchange
write-operation pipeline"* — has to be enforced structurally rather than asserted in prose.

Two owner questions remain open and are **not** blockers for this document, but they decide
where the code lands: **F06-Q3** (port adquery into EAW, or extract the LLM service into a
shared library) and **F06-Q4** (tenant rights for a read-only Exchange app registration). The
boundary specified here is identical under either F06-Q3 answer.

This plan is self-contained; a cold agent can implement it without the originating conversation.

## Problem

Merging adquery's LLM capability into ExchangeAdminWeb puts a model-driven query surface inside
an application whose other modules perform privileged writes against Active Directory and
Exchange Online. Measured in EAW at `9340a87`: `Services/ConferenceRoomService.cs` invokes
**14 mutating cmdlets and 14 read cmdlets from the same class**. Nine services take
`ModuleCredentialService`, which hands back a write-capable AD credential.

A rule of the form *"the assist path only calls read methods"* is therefore unenforceable by
inspection: reads and writes are neighbours behind a single service, share a credential, and
share a connection pool. Any future edit can wire a write into reach without anything failing.

Three distinct things must be true, and they fail independently:

1. **The model must not choose an operation.** It emits a plan; deterministic code executes it.
2. **The assist path must not be able to reach write code.** Not "does not call it" — cannot.
3. **The credential the assist path uses must not be able to write.** So that 1 and 2 failing
   still does not produce a write.

## Approach — three layers, deliberately redundant

Each layer alone is insufficient. Layer 3 is the real boundary; layers 1 and 2 make a breach
visible at build time rather than at incident time.

### Layer 1 — the model emits a plan, never a call (carried over, not built)

adquery's F04 architecture already guarantees this and it is the most valuable thing the port
brings. The model produces a `DirectoryQueryPlan` — a structured, validated, allow-listed
description of a *query* — and `DirectoryPlanExecutor` executes it. The plan grammar has no
verb that mutates: operations are `search`, `expand_members`, `lookup`, `expand_reports`.

**The invariant to preserve and guard:** no code path may construct a cmdlet, LDAP write, or
service call from model-authored text. This is the same rule F04-D2 established when the
guess-transform was deleted, and `ExportIsModelFreeTests` is the existing precedent for
guarding it.

**Explicitly out of scope:** giving the model tool-calling or function-calling against host
services. That would collapse layers 1 and 2 into "the model decides", which is the design this
plan exists to prevent. If that capability is ever wanted it needs its own plan and its own
owner decision.

### Layer 2 — the assist path cannot reach write code, proven by the call graph

The repo already has the mechanism: `tests/AdQueryOrchestrator.Tests/Unit/AssemblyCallGraph.cs`
walks IL transitively, resolving interface calls to their implementations
(`ReachableMethods`, `CalledMembers`, `ImplementationsOf`). It was built for `slice4-or-2`,
where a prose invariant — "export never calls the model" — was upgraded to a reachability
assertion after a review found the prose could be satisfied while the property was false.

**The guard.** From the assist service's public entry points, compute the transitive reachable
set and assert it contains **no** member matching the write surface:

- any `AddCommand`/`AddScript` argument that is a mutating cmdlet — the `Set-`, `New-`,
  `Remove-`, `Add-`, `Enable-`, `Disable-`, `Move-`, `Restore-` verb set;
- `System.DirectoryServices` write members — `DirectoryEntry.CommitChanges`,
  `Properties[...].Value` setters, `DirectoryEntries.Add`;
- host write services by type — in the EAW target, the mutation-capable services enumerated
  from the catalog rather than a hand-maintained list, so a new module cannot be added without
  either appearing in the list or failing the test.

**The failure mode to design against, learned from `f06s2-cr-2`:** a guard that enumerates a
hand-written list silently stops covering what the list omits. Derive the write surface from
something that changes when the code changes — the cmdlet verb prefix, the catalog — not from a
literal array of names.

**Non-vacuity requirement.** The guard must be proven by temporarily wiring a write-capable
service into the assist path and confirming the test reddens. A reachability test that passes
because the graph walk silently returned nothing is worse than no test; `ExportIsModelFreeTests`
already carries an over-removal sentinel for this reason and should be mirrored.

### Layer 3 — the credential cannot write (the real boundary)

Owner, 2026-08-03: *"we can simplify it by creating a read-only secret in delinea for this
app."* Correct, and it is the strongest of the three: a principal without write rights fails
closed at Active Directory and at Exchange, where no coding mistake, prompt injection, or
future refactor can reach.

The two backends need different mechanisms — this is the plumbing conclusion recorded in F06:

| Backend | Mechanism | Work required |
| --- | --- | --- |
| AD / on-prem | A Delinea secret holding a **read-only service account**, referenced by the assist module's own `DelineaSecretId` config field and resolved through the existing `ModuleCredentialService.GetCredentialsAsync(moduleId, purpose)` | **A secret and a config value.** No new code — this is the pattern nine EAW modules already use. |
| Exchange Online | App-only auth carries permission in the **app registration's role assignment**, not in a secret. Requires a **second registration** with a read-only Exchange role, its own certificate, and its own `AppId`. | A second connection pool, or a pool keyed by identity. `ExoConnectionPool` is a singleton over one identity and a borrowed runspace is already connected as whoever opened it, so the read-only identity cannot share those runspaces. **Blocked on F06-Q4.** |

**Sequencing that falls out of the split.** The AD half needs no new code, so a
directory-scoped assist module can be built, guarded, and shipped before the Exchange
registration exists. Only room questions wait on F06-Q4.

**A note on what the read-only credential does not do.** It bounds *authority*, not *exposure*.
A read-only principal can still read everything it is permitted to read, so the existing
attribute allow-lists and the bounded-reduction discipline (DATA-D1) remain load-bearing and
are not superseded by this work.

## Slices

- **Slice 1 — the assist service interface and its read-only execution surface.** Define the
  entry points and the query-only interface they execute through. No host write service is
  injectable into it; this is a type-level constraint, not a convention.
- **Slice 2 — the reachability guard.** As Layer 2. Lands with its non-vacuity proof.
- **Slice 3 — the AD read-only credential path.** Assist module gains its own
  `DelineaSecretId`; a guard asserts the assist path resolves credentials through that field
  and never through another module's.
- **Slice 4 — the Exchange read-only pool.** Blocked on F06-Q4. Second identity, second pool,
  assist path borrows only from it.

Each slice gets a `codereview codex` round at the harness's default pair per the owner's
standing directive, dispatched against a worktree pinned at the slice head.

## Verification

`pwsh -NoLogo -NoProfile -File scripts/verify.ps1` in the repository the code lands in. Every
behaviour-changing slice adds a focused regression guard and proves it fails when its targeted
behaviour is disabled.

## Risks

- **The guard becoming a list nobody updates.** The `f06s2-cr-2` lesson: derive the write
  surface from something that moves with the code. A hand-maintained denylist will rot, and its
  rot is silent.
- **Layer 3 substituting for layers 1 and 2 in practice.** "The credential can't write anyway"
  is a true statement that will be used to justify skipping the guard. It should not be: the
  credential protects production, the guard protects the next developer, and a read-only
  credential misconfigured to a write-capable account is exactly the failure the guard catches.
- **Scope creep into tool-calling.** Named above as out of scope; repeated here because it is
  the most likely direction for "make the assistant more capable" to take, and it silently
  deletes the whole boundary.
- **The read-only account being over-permissioned at creation.** The boundary is only as good
  as the account behind it. Whoever creates it should verify the rights, and the plan should
  record what was verified rather than what was requested.
