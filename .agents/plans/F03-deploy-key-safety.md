# F03 — Deploy can never wipe the live API key

**Status:** Approved (owner, 2026-07-28). DPAPI machine-scope; owner runs the deploy. F03-D1 resolved: preserve the deployed `appsettings.json` by default, opt-in `-OverwriteConfig` to replace from repo.

## Problem

`csharp/deploy.ps1` with `-Force` recursively deletes every entry in the deployed
directory `D:\inetpub\adquery` except `logs` (`deploy.ps1:66-76`), then robocopies the fresh
publish over it. The deployed `appsettings.json` holds the live Claude API key (the repo copy
ships `ApiKey: ""` by design). A forced deploy therefore erases the only configured secret and
replaces it with the blank default. This actually happened on 2026-07-28 (owner ran the deploy,
key wiped, re-entered manually).

Owner decisions (2026-07-28):
- Move the key to a DPAPI-encrypted store that survives deploys — "save the key to an
  encrypted dpapi that never has to change." **Machine-scope** (owner: "I don't know or care
  … I'm trying to solve the deploy problem" → take the simplest correct option).
- The owner runs the deploy on the server (agent is not admin on this host).

## Relationship to P15

`.agents/plans/P15-safe-iis-deployment.md` is a reviewed-but-**unauthorized** plan for a full
release-management rebuild (immutable side-by-side releases, package hashing, journaled
cutover/rollback, 11 slices). Its P15-D3 invariant is exactly this one: *no secret may live
only inside the application directory; P15 never preserves/merges/backs-up a secret-bearing
appsettings file.* F03 is the **minimal targeted fix** that satisfies that invariant now
without adopting P15's full scope: relocate the secret out of the web root and encrypt it, so
the destructive copy can no longer touch it. F03 does not implement P15 and does not change
P15's status. If P15 is later approved, it supersedes both `deploy.ps1` and this store.

## Approach

Two independent changes:

### A. App reads the key from a DPAPI-encrypted file outside the web root

- Store: `C:\ProgramData\ADQuery\claude-apikey.dat` — DPAPI machine-scope
  (`DataProtectionScope.LocalMachine`), so any process on that server (incl. the app-pool
  identity) can decrypt it, and it is **outside** `D:\inetpub\adquery`, so no deploy touches it.
- Add package `System.Security.Cryptography.ProtectedData` to `csharp/AdQueryOrchestrator.csproj`
  (DPAPI is not in-box for `net10`). `ProtectedData` is Windows-only; the project is already
  `net10.0-windows`.
- New `csharp/Security/ProtectedApiKeyProvider.cs`: reads the store path (config knob
  `Claude:ApiKeyFile`, default `C:\ProgramData\ADQuery\claude-apikey.dat`), DPAPI-decrypts it,
  returns the key. Missing file / decrypt failure → return null (do not throw at startup; the
  existing missing-key UX in `ClaudeService.cs:98,148` already handles a blank key gracefully).
- Wire in `Program.cs` / `LlmProviderServiceCollectionExtensions.cs`: after binding
  `LlmProviderOptions`, if `ApiKey` is blank and the store file exists, populate
  `options.ApiKey` from the provider. Precedence: an explicit non-blank `Claude:ApiKey` in
  config still wins (so nothing breaks for anyone setting it directly); the DPAPI store is the
  fallback source. No secret is ever logged (the existing startup line logs only a bool).
- A tiny operator step to write the store once (owner runs it on the server, as themselves or
  as the app-pool account — machine-scope works either way):
  `New-AdQueryApiKeyStore.ps1` prompting for the key and writing the DPAPI blob. Document it.

### B. deploy.ps1 stops deleting the deployed appsettings.json

Even with the key relocated, the deploy overwriting the deployed `appsettings.json` would
still clobber the **model routes** (the just-swapped Claude-primary order) if the deployed copy
was ever hand-edited. Two-part hardening:

- The `-Force` cleanup (`deploy.ps1:66-76`) must **never** remove `appsettings.json` (add it to
  the preserve set alongside `logs`), and robocopy must not overwrite a deployed
  `appsettings.json` that differs from the repo copy without an explicit `-OverwriteConfig`
  switch. Default deploy preserves the deployed config; `-OverwriteConfig` opts into replacing
  it (used when config changes ship, e.g. the model-route swap).
- Because the secret now lives in the DPAPI store, the preserved/overwritten `appsettings.json`
  no longer carries any secret — the model routes are non-secret and safe to ship from the repo.
  So the intended normal path becomes: deploy **does** ship the repo `appsettings.json` (model
  routes), and the key comes from the untouched DPAPI store. That means `-OverwriteConfig` is
  the default and the "preserve" concern is moot for the secret — but the switch still lets an
  operator pin a hand-edited deployed config if needed.

Resolve the A/B interaction as one decision below before implementing B.

## Owner decision needed (one)

**F03-D1 — deployed appsettings.json policy. RESOLVED (owner, 2026-07-28): preserve the
deployed copy by default; require an explicit `-OverwriteConfig` switch to replace it from the
repo.** So the default deploy never touches the deployed `appsettings.json` (neither the
`-Force` cleanup nor robocopy overwrites it); shipping a config change (e.g. the model-route
swap) is a deliberate `-OverwriteConfig` run.

## Slices

Each slice is one commit, guard proven red→green, `verify.ps1` green before commit.

### Slice 1 — DPAPI key provider (change A)

**Commit:** `feat(config): load the Claude API key from a DPAPI store`

- Add the `ProtectedData` package, `ProtectedApiKeyProvider`, config knob, and DI wiring.
- Add `New-AdQueryApiKeyStore.ps1` + README note.

Guard: unit test — given a DPAPI blob written at a temp path with a known plaintext, the
provider decrypts it and the bound `LlmProviderOptions.ApiKey` equals the plaintext; given a
blank config key and a present store, the store value is used; given a non-blank config key,
config wins. Prove red→green by temporarily returning null from the provider (options key stays
blank → test fails). Note: DPAPI blobs are machine-specific, so the test writes its own blob at
runtime rather than committing a fixture.

### Slice 2 — deploy.ps1 protects config (change B)

**Commit:** `fix(deploy): stop the forced deploy from wiping deployed config`

- Implement the F03-D1 decision in `deploy.ps1`.
- Guard: this is a PowerShell script with no current automated test harness (P15's Pester
  foundation is unbuilt). Guard by a focused Pester test *only if* the owner wants the P01
  Pester stage stood up for it; otherwise verify by a documented manual dry-run on the server
  (`-WhatIf`-style: list what the cleanup would remove and assert `appsettings.json` is not in
  the set). State clearly in the commit which verification was used.

## Verification

`pwsh -NoLogo -NoProfile -File scripts/verify.ps1` green before each commit. Slice 2's script
behavior is verified per the guard note above (no C# test covers `deploy.ps1`).
