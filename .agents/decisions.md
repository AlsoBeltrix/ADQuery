# Settled Decisions

## MODEL-D1 — Claude primary, gpt-5.5 alternate

- Status: Approved
- Date: 2026-07-28
- Authority: Repository owner ("the alternate model returned 2 … that's better. swap the models back")
- Decision: `Claude:Model` (primary) is `@gcp-vertexai-us-global-integration/anthropic.claude-opus-4-8`; `Claude:AlternateModel` (retry) is `@azure-openai-eus2-global/gpt-5.5-dzs`. Reverses commit 47daf22.
- Evidence: On the owner's "who is the CFO?" query the Claude route returned 2 records (the current CFO plus a prior CFO whose account still exists) versus 7 from gpt-5.5; the tighter answer is preferred as the default. The wide-OR person search in `Configuration/prompt_template.txt` is the underlying cause of the over-matching and is not changed by this decision.
- Consequence: Committed `1152036`. The deployed copy carries the swap only after a deploy that ships the repo `appsettings.json` (see F03-D1).

## F03-D1 — Deployed appsettings.json is preserved by default on deploy

- Status: Approved
- Date: 2026-07-28
- Authority: Repository owner
- Decision: Once the Claude API key lives in the DPAPI store (F03 change A), the deployed `appsettings.json` is non-secret (model routes only). The deploy preserves the deployed copy by default — neither the `-Force` cleanup nor robocopy overwrites it — and requires an explicit `-OverwriteConfig` switch to replace it from the repo. Shipping a config change (e.g. the MODEL-D1 route swap) is a deliberate `-OverwriteConfig` run.
- Scope: Governs `csharp/deploy.ps1` (F03 Slice 2). DPAPI is machine-scope (`DataProtectionScope.LocalMachine`); the store lives at `C:\ProgramData\ADQuery\claude-apikey.dat`, outside the web root, so no deploy touches the secret. Detail in `.agents/plans/F03-deploy-key-safety.md`.
- Relationship to P15: F03 is the minimal targeted fix satisfying P15-D3's "no secret only inside the app directory" invariant; it does not implement or authorize the full P15 release-management rebuild.

## REQBODY-D1 — Restore an independent 2 MiB transport request-body cap

- Status: Approved
- Date: 2026-07-28
- Authority: Repository owner ("yeah 2MB limit should suffice")
- Decision: Restore a transport request-body-size limit of 2 MiB (2,097,152 bytes), owned by the application host and independent of any feature. Killing CSV enrichment (CSV-KILL-D1) also removed the P05 Slice 2 body cap, which had been sourced from the deleted CSV options and was the app's only request-body-size protection. Rather than leave the front door uncapped, a plain 2 MiB cap is restored so a single oversized request cannot exhaust server memory.
- Scope: Wire the cap into `IISServerOptions.MaxRequestBodySize` (authoritative under the current IIS in-process hosting model) and `KestrelServerOptions.Limits.MaxRequestBodySize` (inert in-process; protects a future direct/Kestrel host), driven by a config knob `RequestLimits:MaxRequestBodyBytes` defaulting to 2 MiB. The host returns 413 for an over-cap body; no application code path is required. This is unrelated to `FollowUp:MaxContextBytes`, which bounds what a follow-up turn sends to the model per turn — the two knobs govern different things (raw front-door size vs. model exposure) and never move together.
- Constraints: One concern, landed with a red→green guard proving the configured host body cap equals 2 MiB (a test that fails when the wiring is reverted to the host default of 30 MB). Passes `scripts/verify.ps1`.
- Consequence: Resolves the open owner y/n left by CSV-KILL-D1. The app again rejects oversized requests at the host; 2 MiB comfortably exceeds any legitimate query/follow-up payload (the follow-up context transport guard is 2,000 code units).

## CSV-KILL-D1 — Remove the CSV enrichment feature entirely

- Status: Approved
- Date: 2026-07-28
- Authority: Repository owner
- Decision: The CSV enrichment feature (upload a CSV, the LLM generates an enrichment plan, the backend does per-row AD lookups and merges results) is removed from the product in full — server endpoint, service, validators, options, configuration, and tests. F01 Slice A had parked it in the UI only; the server `POST api/query/csv-enrich` endpoint remained live and reachable but had no UI and no user. The owner declined to invest further in hardening a feature that "will never get used or seen."
- Scope: Delete the enrichment-only surface (the `csv-enrich` action and its helpers, `CsvEnrichmentService`, `CsvEnrichmentPlanValidator`, `CsvEnrichmentRequestValidator`, `CsvEnrichmentFilterEvaluator`, `CsvEnrichmentLimitsOptions` + validator + DI extension, `CsvEnrichmentResultPublication`, `CsvEnrichmentPlan` model, `GenerateCsvEnrichmentPlanAsync` and its prompt builders, and every enrichment test). The CSV **download format** for ordinary query results (`BuildCsv`/`EscapeCsv`/the `"csv"` branch of `GenerateFileContent` and the download path) is a separate concern and is KEPT.
- Constraints: One concern, landed with a red→green guard that proves the `csv-enrich` endpoint is no longer mapped (the former `CsvUiParkingGuardTests` mapping assertion is flipped). The Slice 2 transport request-body cap and its `web.config` `requestLimits` were sourced from the deleted CSV options and are removed with the feature.
- Consequence: Reverses the F01 "park CSV in UI only" non-goal. Moots P05 Slices 3–7 (they hardened this feature); the P05 plan is superseded. DATA-D1's CSV-path "never row cell values" clause is now historical.

## TEST-D1 — F01 front-end slices are guarded by an automated browser test harness

- Status: Approved
- Date: 2026-07-27
- Authority: Repository owner
- Decision: The F01 front-end slices (headline rendering B2, follow-up chat UI C3, and any other browser-visible F01 change) are verified by an automated headless-browser test harness that drives the real page, not by hand-written manual smoke notes. The owner declined the manual-only option.
- Constraints: Introducing the harness is a code/dependency change and goes through the normal plan gate before any of it lands. It becomes its own F01 slice, sequenced before B2, and carries the standard red→green guard (a test that fails when the rendering it guards is broken) and passes `scripts/verify.ps1`. Wire it into the canonical verification so the browser checks run on every change, not on memory.
- Consequence: Front-end regressions are caught automatically rather than depending on someone remembering to look. B2 does not start until the harness slice is planned and landed.

## FONT-D1 — UI uses the Windows-installed Candara font; no web-font hosting

- Status: Approved
- Date: 2026-07-27
- Authority: Repository owner
- Decision: The conversational-query UI (F01) uses Candara, which ships with every supported Windows client, as its display font. No `@font-face`, CDN link, or self-hosted font file is introduced.
- Constraints: Candara is Windows-only; a non-Windows client falls back through the declared stack. Cross-platform or self-hosted fonts are out of scope for F01 and remain a later decision if a non-Windows client ever matters.
- Consequence: The approved mockup (`artifacts/mockups/qa-ui.html`) already satisfies this; porting its palette/typography into `css/styles.css` needs no font-delivery infrastructure.

## FOLLOWUP-D1 — Follow-up context is byte-capped by its own knob

- Status: Approved
- Date: 2026-07-27
- Authority: Repository owner
- Decision: The context slice a follow-up turn sends back to the model is capped by bytes, and that cap is its own configuration knob (`FollowUp:MaxContextBytes`), separate from the on-screen preview-row cap (`QueryDefaults:PreviewRowCount`).
- Constraints: The byte cap is enforced server-side as the authoritative minimal-leakage bound (DATA-D1); the client must not be trusted to have truncated. The knob's default is evidence-derived from the actual preview+plan-summary payload, not an assumed typical turn.
- Consequence: The preview cap governs display; the follow-up cap governs model exposure. Tuning one never silently moves the other.

## FOLLOWUP-D2 — Follow-up carries the last turn only

- Status: Approved
- Date: 2026-07-27
- Authority: Repository owner
- Decision: A follow-up turn sends the immediately preceding turn's material (its question, executed-plan summary, and DATA-D1 minimal value slice) and nothing earlier. No accumulated multi-turn transcript is sent to the model or retained server-side for follow-up purposes.
- Constraints: Each follow-up re-validates through the existing P04 security policy and plan validation exactly as a fresh query; it is not a privileged path.
- Consequence: Context stays bounded and stateless per turn; there is no growing conversation buffer to leak or manage.

## HEADLINE-D1 — Headline answer is derived from plan shape, not user-selected

- Status: Approved
- Date: 2026-07-27
- Authority: Repository owner
- Decision: The main-window headline answer is derived from the generated plan's shape — count/aggregation plans yield a number/grouped-count headline, single-record plans yield a record headline, multi-row plans yield a count-plus-table headline. The user does not pick the format from a selector.
- Constraints: The headline is presentation over data the async path already returns (`aggregation` on job status, `rows`/`totalRows` on preview); it introduces no new value exposure to the model and never replaces the authoritative server-side download.
- Consequence: Terse questions get a terse answer without the user reading a grid, while the full table and download remain available beneath.

## SYNC-D1 — Retire the unused synchronous `execute` endpoint

- Status: Approved
- Date: 2026-07-27
- Authority: Repository owner
- Decision: The synchronous `POST api/query/execute` endpoint (`QueryController.ExecuteQuery`) is retired. The shipped browser uses only the async path (`execute-async` + job polling); nothing calls the sync endpoint. Rather than fix its latent aggregation gap (it computes but never returns aggregation), remove it.
- Constraints: Confirm no in-repo or shipped caller invokes it before removal. Remove the action and any now-dead helpers unique to it; do not disturb the async path or shared helpers. This is a code change requiring the normal red→green guard and verification once F01 implementation begins (or as an independent cleanup slice).
- Consequence: Removes the former F01 "GATE-2." F01 Slice B targets only the async headline path; there is no sync `QueryResponse` aggregation gap to close.

## DATA-D1 — Relaxed data-minimization: bounded AD values may be sent to the model

- Status: Approved
- Date: 2026-07-24
- Authority: Repository owner
- Decision: The original strict no-CUI posture (send column *patterns*/format descriptions to the model, never actual attribute values — e.g. `QueryController.DetectColumnPatterns`, and the CSV path's "never row cell values" rule) was built before the AD data's classification was clarified. The models served through the Portkey/Bedrock route are cleared for information up to and including Confidential, and the AD data's classification falls within that clearance. Sending a minimal amount of real AD attribute values back to the model as steering context is therefore permitted.
- Constraints: Remain privacy-focused and minimal. Send the smallest slice that steers effectively — the on-screen preview slice (≤10 rows, `QueryDefaults:PreviewRowCount`) or, for aggregation queries, the group-by summary, never the full result set and never 10k rows. Full download results stay server-side and are never sent to the model.
- Consequence: Enables value-based follow-up/refinement (feeding the prior turn's question, plan, and preview values back as context) rather than pattern-only re-guessing. The value-minimization in the current code becomes a deliberate "keep minimal" bound rather than an absolute prohibition. A per-turn context cap (reuse the preview cap or a dedicated small knob) enforces the minimal-leakage bound in code.
- Amendment (2026-07-27, repository owner): the original "only the Portkey/Bedrock cleared route is covered" restriction is dropped. The model needs the context to answer reliably, and the model routes are being updated regardless; withholding context to satisfy a route-clearance rule would make the app less useful for no real benefit. Bounded AD values may be sent as steering context to whatever configured model route answers the query, primary or alternate. The remaining constraints (minimal slice, never the full set, downloads stay server-side) still hold. This removes the basis for the former F01 "GATE-1" and for treating the alternate (Azure OpenAI) retry route as a leakage risk. Note this scope is model-transmission only; on-disk logging of AD values on the application's own locked-down server is unrelated to data-minimization and is unrestricted — the app logs everything as normal.

## P05-D0 — No legacy-limit constraint; UI reflects enforced code limits

- Status: Approved
- Date: 2026-07-23
- Authority: Repository owner
- Decision: The application has never had a real user, so no existing user-facing CSV limit is a compatibility constraint. The stale "Maximum upload size: 10 MB" UI hint (`csharp/wwwroot/index.html`) is discarded, not preserved. The code enforces the proper evidence-derived CSV limits (the D1 caps), and the UI is updated to state exactly what the code enforces.
- Constraints: The UI's stated limits must match the enforced code limits, not aspirational or stale values. This resolves the flagged conflict between the shipped 10 MB hint and P05's 100,000-row / ~90.7 MB worst-case design target: the design target governs, the 10 MB hint is removed.
- Consequence: D1 cap selection is unconstrained by the old 10 MB label. Whatever body/row/column caps D1 settles become both the enforced values and the displayed guidance.

## P04-D1 — Fail CSV enrichment atomically

- Status: Approved
- Date: 2026-07-22
- Authority: Repository owner
- Decision: On the first non-cancellation Active Directory operational error during CSV enrichment, fail the entire enrichment, discard every accumulated row, and publish no result file, download identifier, preview, or cache entry.
- Constraints: A successful lookup with no result remains an ordinary “not found” outcome. Cancellation must propagate rather than becoming a lookup failure. Invalid plans must fail before directory access. Partial-result publication is out of scope unless a later owner decision defines its data, UI, retry, and warning contract.
- Consequence: Users must retry after a directory failure, but the application cannot present an incomplete dataset as a successful authoritative result.

## P03-D5 — Defer the real-server sign-in check

- Status: Approved
- Date: 2026-07-22
- Authority: Repository owner
- Decision: Do not require a separate non-production Windows server or make real company-account sign-in testing a release condition. The application may be released after its automated checks pass; test allowed, refused, and anonymous access on the real server when convenient.
- Constraints: A production installation remains a separately authorized action. Never record the deferred sign-in check as passed until it actually runs. If the later check fails, close access and remove or replace the failed installation rather than leaving known-bad authentication exposed.
- Consequence: The project accepts the risk that a Windows or company-directory integration problem may first appear on the real server. This is proportionate to the owner's stated context that the application currently has no users and has remained broken for months without reported impact.

## P03-D2 — Use the maintained server runtime

- Status: Approved
- Date: 2026-07-22
- Authority: Repository owner
- Decision: Keep the application framework-dependent on IIS. Install and maintain one patched .NET 10 runtime and Hosting Bundle on each Windows server instead of shipping a private .NET runtime inside every application release.
- Constraints: Publish a clear prerequisite checklist covering the required .NET 10 runtime, IIS hosting components, installation order, server architecture, authentication settings, and restart requirements. Deployment must stop before replacing application files when those prerequisites are missing or stale.
- Consequence: Application releases stay smaller and do not carry runtime files that become stale independently. Server maintenance owns .NET security updates, and deployment documentation must make that responsibility explicit.

## P02-D1 — Provider-capable sampling

- Status: Approved
- Date: 2026-07-22
- Authority: Repository owner
- Decision: Retain `temperature` as an explicit capability of a configured LLM route. Sampling is omitted unless one valid profile's exact integration-qualified model identifier equals the effective request model. A matching `Temperature` profile emits its finite `0.0..1.0` value; no match emits nothing. Exact configured equality is selection, not capability inference: never derive support from provider, gateway, class, endpoint, or model-name patterns.
- Constraints: The checked-in Vertex Claude route has no enabled sampling profile. Blank or duplicate profile identifiers, unknown modes, and missing or invalid opted-in values fail startup validation. A legacy global `Claude:Temperature` value is ignored with one warning and never enables sampling. One centralized request builder applies the same exact-profile policy to normal, CSV-enrichment, and health-related requests.
- Consequence: Current Claude Opus 4.8 requests stop failing on the deprecated parameter, while another configured provider can opt in without leaking that capability to the primary, alternate, or arbitrary unprofiled model routes.

## P01-D5 — Repository line endings

- Status: Approved implementation selection
- Date: 2026-07-22
- Authority: Repository owner delegated implementation through the approved P01 execution direction
- Decision: Store repository-owned text as LF through `.gitattributes` and `.editorconfig`, with CRLF reserved for Windows command scripts (`*.bat` and `*.cmd`). Use four-space indentation for C# and PowerShell and two-space indentation for JSON, YAML, and MSBuild XML.
- Constraints: Land policy before the isolated Slice 4 normalization; do not mix the resulting whole-repository whitespace rewrite with analyzer or behavior changes.
- Consequence: Git configuration such as `core.autocrlf` cannot silently select a different canonical representation, and the formatter gate has one deterministic cross-platform baseline.

## P01-D4 — Progressive analyzer enforcement

- Status: Approved implementation selection
- Date: 2026-07-22
- Authority: Repository owner delegated implementation through the approved P01 execution direction
- Decision: Enforce the .NET 10 SDK's default analyzer set and all compiler/analyzer warnings as errors in P01. Do not enable the full `10.0-recommended` set until its existing findings are fixed under their owning modernization plans.
- Evidence: A dry run at implementation base `5716462` with `AnalysisLevel=10.0-recommended` produced 290 errors spanning logging source generation, globalization, API naming, allocation, and semantic cleanup. The same solution passed with zero warnings under `AnalysisLevel=10.0` and warnings-as-errors.
- Constraints: Add no blanket or per-rule suppression baseline. New warnings in the enforced set fail immediately. Later plans must resolve applicable recommended diagnostics rather than suppress them and may raise the repository level only when the complete solution is clean.
- Consequence: P01 establishes a strict, green analyzer floor without absorbing behavior-sensitive work from P02–P21.

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
