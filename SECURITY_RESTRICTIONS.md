# Executive Summary
- Only Windows-authenticated users in `ANALOG\ADEXNLQ_Users` can access the API; unauthenticated callers never reach controllers.
- Every directory plan is vetted by `PlanValidator` and `DirectoryPlanExecutor`, which block unsupported operations, attributes, and complexity before LDAP execution.
- Result sizes, projection width, and attribute exposure are strictly constrained; per-user logging and sanitized export paths provide an audit trail with controlled data egress.
- Optional HMAC signing, secure LDAP bindings, and structured health checks round out defense-in-depth for execution plans and downstream dependencies.

# Detailed Guardrails

## Access Control
- `Controllers/QueryController.cs:21` enforces `[Authorize(Roles = "ANALOG\ADEXNLQ_Users")]`, limiting the API to Windows-authenticated callers in that group.
- `Program.cs:34-44` registers Negotiate authentication and sets a fallback authorization policy requiring the same role, ensuring every controller inherits the restriction.
- `Program.cs:65-82` keeps HTTPS, static file serving, and API routing behind the authentication/authorization middleware, preventing anonymous access.

## Plan Guardrails
- `Security/PlanValidator.cs:18-121` allow-lists plan operations (`search`, `expand_members`, `lookup`), approved filter operators, and per-object attribute sets; it also rejects duplicate step names, enforces step dependency ordering, and requires `lookup` steps to specify both `source` and `source_attribute`.
- `Controllers/QueryController.cs:118` normalizes plan filters before validation/execution, mapping license aliases to `extensionAttribute11` and translating relaxed operator phrases into the supported allow-list.
- `Security/PlanValidator.cs:37-123` caps plan complexity to 10 steps, five filters per step, 25 attributes per step, and 25 projection columns; violations are logged and cause validation failure.
- `Security/PlanValidator.cs:123-175` validates the optional projection-level filter so only allow-listed attributes/operators can be used to trim row results post-execution.
- `Services/DirectoryPlanExecutor.cs:230-309` evaluates projection filters server-side, ensuring exclusions expressed at projection scope (e.g., `extensionAttribute11 not_equals "F3"`) actually shape the final dataset.
- `Services/ActiveDirectoryService.cs:55-126` trims LDAP filter values and refuses to execute searches when a filter value is blank, avoiding malformed `(attribute=)` queries from hitting Active Directory.
- `Services/DirectoryPlanExecutor.cs:166-221` normalizes step filters before execution and halts downstream processing when a required group search returns no members, preventing broad fallbacks when a seed object is missing.
- `Services/ActiveDirectoryService.cs:239` translates allowed operators (including the new negation variants) into LDAP clauses so exclusions such as `extensionAttribute8 not_equals "2"` are enforced server-side.
- `Services/DirectoryPlanExecutor.cs:64-120` runs validation before any execution, confirms mandatory metadata (description, projection row step), enforces sequential step numbering, and prevents malformed plans from reaching LDAP.
- `Services/DirectoryPlanExecutor.cs:175-187` calls `ValidateComplexity`, giving `PlanSecurityResult` the final say before execution proceeds.

## Attribute Allow-Lists & Configuration
- `Security/PlanValidator.cs:193-241` loads per-object attribute allow-lists from the configuration text files under `Configuration/*.txt`, falling back to safe defaults if files are missing or empty.
- `appsettings.json:113-125` wires the attribute file locations and exposes `HmacSecretKey`, `EnableHmacValidation`, `MaxPlanComplexity`, and `MaxExecutionTimeSeconds` for environment-specific tuning.
- `Security/PlanValidator.cs:138-171` implements optional HMAC signing of plans, using the configured secret to compute a `SHA256` signature when enabled.
- `Controllers/QueryController.cs:766` accommodates a client-supplied signature field so the controller can later require HMAC validation once `EnableHmacValidation` is true.

## Execution & Output Safety
- `Controllers/QueryController.cs:603-621` parses natural-language result limits and caps them at 500 rows to prevent large data extractions.
- `Controllers/QueryController.cs:629-657` writes the enforced limit back into the plan (`result_limit`, `size_limit`) so both the executor and LDAP respect it.
- `Controllers/QueryController.cs:83-216` captures the raw Claude response and both the original and executed plans in each per-request log to simplify audits and debugging.
- `Services/ActiveDirectoryService.cs:56-126` honors those size limits during searches and uses secure LDAP bindings (`AuthenticationTypes.Secure | Sealing | Signing` at `Services/ActiveDirectoryService.cs:77,149,191`).
- `Controllers/QueryController.cs:27-51` restricts cached artifacts to a 30-minute lifetime and a known set of export formats, reducing exposure of sensitive results.
- `Controllers/QueryController.cs:261-307` enforces the same format allow-list during downloads; unauthorized or stale request IDs yield `404`/`400` responses.
- `Controllers/QueryController.cs:586-734` sanitizes per-user output paths, writes detailed log files for each request, and records download history to provide an auditable trail of data access.

## Operational Observability
- `Services/ClaudeService.cs:65-204` validates Claude responses, ensuring only well-formed JSON plans are accepted and logging malformed replies or API errors.
- `Controllers/QueryController.cs:300-353` exposes a health endpoint that aggregates Claude and orchestrator status; the underlying health checks confirm plan execution and validator readiness.
- `Services/ClaudeService.cs:204-259` provides a dedicated health probe that exercises `GenerateExecutionPlanAsync`, ensuring the upstream model remains reachable and parsable.

## Residual Considerations
- `appsettings.json:113-118` ships HMAC validation disabled (`EnableHmacValidation: false`); enabling it will require distributing the shared secret and wiring controller-level enforcement.
- `Security/PlanValidator.cs:175-187` respects the hard-coded complexity caps, but the `MaxPlanComplexity` setting is currently unused; aligning config-driven limits with the validator would improve flexibility.
- `Controllers/QueryController.cs:603-615` caps human-readable limits at 500, but other plan shapes could still request fewer rows; monitor execution logs to ensure limits remain appropriate.
- `Controllers/QueryController.cs:27-51` caches results in memory, so deployments should ensure the 30-minute retention aligns with organizational data-handling policies.
