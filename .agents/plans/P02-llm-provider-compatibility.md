# P02: LLM Provider Request Compatibility

**Status**: Draft — implementation is not authorized
**Review**: Pending; maximum 3 advisory rounds
**Dependencies**: P01 provides the preferred test harness, but this production-blocking compatibility fix may establish only the focused C# test project if P01 has not landed.

## Problem

The configured Vertex-hosted Claude model rejects requests that contain `temperature`. Both application request paths always serialize that property, including when configuration is absent because the code falls back to zero.

Evidence:

- `csharp/appsettings.json:40-50` configures the provider, model, and `Claude:Temperature` value at line 46.
- `csharp/Services/ClaudeService.cs:99-111` parses a default value of zero and places `temperature` in every normal plan request.
- `csharp/Services/ClaudeService.cs:259-265` independently repeats the same behavior for CSV-enrichment requests.
- `csharp/Services/ClaudeService.cs:202-214` implements the provider health check through the normal generation path, so the same incompatibility also marks provider health unhealthy.

Observable failure: every plan-generation, CSV-enrichment, and provider-health request using the current model can fail with HTTP 400 before inference. Removing the JSON configuration key alone does not help because both code paths still serialize the fallback value.

## Goals

- Omit unsupported sampling parameters by default.
- Use one typed request builder for normal, CSV, and health-related message requests.
- Allow a sampling parameter only through explicit configuration that is independent of model-name heuristics.
- Preserve the existing message endpoint, authentication headers, prompt content, model override behavior, response parsing, and public service interfaces.
- Return a useful, sanitized provider error without leaking credentials or full prompt content.

## Non-goals

- Changing the configured provider, integration, or model.
- Redesigning prompts or plan schemas.
- Adding retries or provider failover.
- Redesigning health semantics; P20 replaces the expensive generation-based health check.
- General cancellation cleanup; P13 owns the end-to-end cancellation contract.

## Proposed design

### Typed options and request contract

Introduce a validated `ClaudeOptions` options type bound from the `Claude` section through `AddOptions<ClaudeOptions>().Bind(...).ValidateOnStart()` in `Program.cs`. Keep existing names for connection, endpoint, token budget, model, and prompt-template settings so environment/IIS overrides remain compatible. Preserve the typed client's configuration-driven `BaseAddress` behavior.

Represent sampling as an explicit opt-in rather than inferring capabilities from a provider name or model slug:

- `SamplingMode` defaults to `Omit`.
- When mode is `Omit`, ignore any legacy `Temperature` value and emit one startup warning; this remains true even when that ignored value is non-finite or out of range because it will not enter a request.
- When mode is `Temperature`, require a value and validate it as finite and within the inclusive `0.0..1.0` application range.
- Reject an unknown sampling mode, a missing opted-in value, or an invalid opted-in value during startup validation.

The messages request DTO has explicit `JsonPropertyName` attributes for `model`, `max_tokens`, `temperature`, `system`, and `messages`; the nested message DTO similarly pins `role` and `content`. Its nullable `Temperature` property uses `JsonIgnoreCondition.WhenWritingNull`. The default builder assigns `null`; therefore the serialized payload contains no `temperature` key. Do not use anonymous objects or a naming policy as an implicit wire-contract definition.

### One request builder

Extract a single internal builder or small provider-client component that accepts the effective model, system guidance, user content, and token limit and returns the typed messages request. Both `GenerateExecutionPlanAsync` and `GenerateCsvEnrichmentPlanAsync` must use it. The health path continues to call the shared path until P20 replaces it.

The builder is the only location allowed to translate sampling configuration into a request property. No call site may append optional provider parameters independently.

### Configuration migration

Change the checked-in default configuration from `Claude:Temperature: 0` to the explicit omit mode and remove the comment that recommends temperature for deterministic JSON. Keep a documented opt-in example without enabling it. Environment-specific configuration that still supplies only `Claude:Temperature` must not silently enable the field. A custom `IValidateOptions<ClaudeOptions>` logs the ignored legacy key through its injected logger while returning success for `Omit`; options caching makes this an initialization-time observation. The same validator returns a failure for invalid opted-in configuration. Tests resolve `IOptions<ClaudeOptions>.Value` with a capturing logger and assert the warning and validation outcomes.

The existing `int.Parse` handling for malformed `MaxTokens` is a separate typed-configuration defect owned by P16. P02 must preserve valid token-limit behavior and must not broaden into the full configuration migration.

### Provider error handling

Parse known Anthropic/Portkey error envelopes enough to retain HTTP status, provider type, error code when present, and a bounded sanitized message. Log the endpoint host, effective model identifier, status, and request correlation information, but never log authorization headers, API keys, or full prompts. Preserve the existing user-facing failure shape for this plan; P13 may later standardize error categories.

## Implementation slices

Each slice is one commit and must pass the repository verification command before the next begins.

1. Add focused request-serialization tests using a fake `HttpMessageHandler`. If P01 has not established `tests/AdQueryOrchestrator.Tests`, create only the minimum project and solution wiring needed by these tests, following P01's intended layout.
2. Add and register the typed options, typed messages DTO, validation, and centralized request builder without changing the active call sites.
3. Route normal plan generation and CSV-enrichment generation through the builder; remove both anonymous request bodies and duplicate temperature parsing.
4. Change checked-in sampling defaults to omit, add legacy-key warning behavior, and update configuration documentation.
5. Add bounded provider-error parsing and logging tests without changing public response contracts.

Do not combine the later health redesign, retry policy, or cancellation refactor into these commits.

## Verification and guard proof

Automated tests must cover:

- The default normal request JSON does not contain a `temperature` property.
- The default CSV request JSON does not contain a `temperature` property.
- Both payloads retain the exact required wire names `model`, `max_tokens`, `system`, and `messages`, and every message retains `role` and `content`; assert their JSON value kinds and representative values as well as their presence.
- Removing all sampling configuration still omits the property.
- Explicit `SamplingMode=Temperature` plus a valid value includes the property in both request types.
- Any Temperature value with `SamplingMode=Omit` is omitted and produces the expected startup warning, including a non-finite or out-of-range ignored value.
- Unknown mode, missing temperature in opted-in mode, and a non-finite or out-of-range opted-in value fail startup options validation.
- Base model, configured alternate model, and per-request model override are preserved and omit temperature unless their exact request configuration opts in.
- The health call reaches the same compatible request builder while P20 is pending.
- A representative Portkey/Vertex 400 response is converted to a bounded sanitized error and does not expose configured secrets.

Guard proof for the regression test:

1. With the fix present, capture the outgoing default payload and assert that `JsonDocument.RootElement.TryGetProperty("temperature", out _)` is false for both request paths.
2. Temporarily restore either unconditional `temperature` assignment.
3. Confirm the corresponding test fails because the key is present.
4. Restore the fix and confirm the focused tests and the repository verification command pass.

Manual verification, when credentials are available:

1. Start the application with the current configured integration and no sampling opt-in.
2. Submit one normal directory-plan request and one CSV-plan request containing no sensitive data.
3. Confirm neither receives the deprecated-parameter 400 and confirm provider logs contain no credential or prompt payload.

## Acceptance criteria

- No default application request serializes `temperature`.
- Normal and CSV request serialization are implemented by one builder and one DTO.
- The typed DTO preserves every required wire property and nested message property exactly.
- Sampling opt-in is explicit, validated, and covered by tests.
- Existing model override, endpoint, authentication, prompt, and response contracts remain compatible.
- The reported Vertex 400 is absent in a credentialed smoke test, or that smoke test is explicitly recorded as not run when credentials are unavailable.
- The red/green guard proof and the repository verification command both pass.

## Rollback and risks

The code rollback is the set of P02 commits. Configuration rollback is not sufficient because the original code serializes a fallback value. Omitting temperature may allow provider defaults to vary across integrations; structured-output correctness must continue to be enforced by schema validation rather than sampling configuration. The optional mode prevents a future provider that supports temperature from requiring another code fork.

## Open owner decision

**P02-D1 — Sampling support**: retain a disabled, explicit temperature opt-in, or remove temperature support entirely. Recommendation: retain the opt-in because it costs little once centralized and avoids model-name conditionals, while the safe checked-in and missing-configuration behavior remains unconditional omission.

## Review history

### Round 1 — 2026-07-21T19:43:16Z

**Reviewer**: Headless Claude Code 2.1.216 / configured model / max effort
**Verdict**: Revisions required

- Added full required-field and nested-message wire-shape guards so the anonymous-to-typed DTO migration cannot silently rename `max_tokens` or other provider fields.
- Defined legacy and invalid sampling configuration deterministically: omit-and-warn whenever mode is `Omit`; fail startup validation only for unknown mode or invalid/missing values when explicitly opted in.
- Added explicit options registration, alternate-model coverage, corrected configuration evidence, and assigned malformed `MaxTokens` handling to P16.

### Round 2 — 2026-07-21T19:49:00Z

**Reviewer**: Headless Claude Code 2.1.216 / configured model / max effort
**Verdict**: Accepted

- The reviewer confirmed both round-one defects were resolved and found no remaining implementation blocker.
- Applied the optional precision improvements without another review round: fixed the opted-in numeric range at `0.0..1.0` and defined the legacy-warning observation point through the custom options validator and capturing-logger test.
