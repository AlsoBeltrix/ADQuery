# Implementation Summary

## Highlights

- Claude now produces **DirectoryQueryPlan** documents—operation/attribute driven JSON—rather than shell commands.
- An all-managed pipeline (`DirectoryPlanExecutor` + `ActiveDirectoryService`) executes those plans with `System.DirectoryServices`.
- Security guardrails enforce operation, attribute, and filter allow-lists before any LDAP request runs.
- Per-user audit trail: every query now produces its own log (`E:\WWWOutput\<SAMAccountName>\adquery_<SAMAccountName>_<timestamp>.log`) alongside a matching CSV export in the same directory.
- Natural-language limits (“first N”, “top N”) are turned into `result_limit`/`size_limit` in the plan and enforced again in the executor so we never cache more rows than requested.
- Front-end workflow shows a preview first, then lets the user choose CSV, Excel, HTML, or plain-text exports without rerunning the query.

## Component Map

| Area | Implementation |
|------|----------------|
| Models | `DirectoryQueryPlan`, `DirectoryPlanStep`, `ProjectionDefinition`, `DirectoryRecord` |
| Services | `ClaudeService`, `DirectoryPlanExecutor`, `ActiveDirectoryService`, `IActiveDirectoryService`, `IDirectoryPlanExecutor` |
| Security | `PlanValidator` returning `PlanSecurityResult` |
| Controllers | `QueryController` wired to the new executor, download endpoint, and per-user logging |
| Health | `OrchestratorHealthCheck` validates directory plans, `ClaudeHealthCheck` unchanged |
| Configuration | Removed JEA section; new `ActiveDirectory` settings for optional LDAP root override |
| Dependency Injection | Registers `IActiveDirectoryService`, `IDirectoryPlanExecutor`, `IPlanValidator`, `IClaudeService`, and memory cache |

## Execution Flow

1. User submits a natural-language question via `/api/query/execute`.
2. `ClaudeService` builds a directory plan JSON document with operations (`search`, `expand_members`, `lookup`) and a projection map.
3. `PlanValidator` enforces allow-lists (operations, attributes, filter operators, projection width, plan length).
4. `DirectoryPlanExecutor` runs each step in order, delegating to `ActiveDirectoryService` for LDAP queries.
5. Results are combined according to the projection; the API responds with the first 10 preview rows plus metadata, while the full dataset is cached for download.

## Security & Compliance

- **Operation allow-list**: only `search`, `expand_members`, `lookup`.
- **Attribute allow-list**: tailored for users, groups, computers, and OUs.
- **Filter operators**: `equals`, `not_equals`, `contains`, `not_contains`, `starts_with`, `not_starts_with`, `ends_with`, `not_ends_with`.
- **Filter normalization**: LDAP predicates are trimmed and empty values are rejected before execution, preventing malformed `(attribute=)` searches from reaching AD.
- **Plan complexity**: capped at ten steps, five filters per step, twenty-five projection columns.
- **Projection filters**: optional row-step filters are validated and evaluated after execution so JSON plans can express exclusions without custom joins.
- **Windows group enforcement**: only members of `ANALOG\ADEXNLQ_Users` can reach the controllers (Negotiate/Windows auth).
- **Optional HMAC**: retained via `Security:HmacSecretKey`.
- **Per-user logging**: each request (success, failure, cancellation) writes a dedicated timestamped log file with download history and the saved CSV artifact.

## Configuration Notes

- `ActiveDirectory.RootPath` defaults to the forest's `RootDSE` when left blank (the IIS app pool identity is always used for binding).
- Claude configuration continues to support both Portkey and direct Anthropic endpoints.

## Health & Diagnostics

- `/health` (directory plan): validates a tiny plan to ensure the executor and validator remain healthy.
- `/health` (claude): calls `GenerateExecutionPlanAsync` with a static prompt to confirm model reachability and JSON parsing.
- `logs/adquery-orchestrator-.txt`: rolling Serilog output for API-level diagnostics; per-user folders hold the audit trail and exported files.

## Front-end Impact

- The UI now previews the first 10 rows, then presents format buttons (CSV, Excel, HTML, Plain Text) so users can download without rerunning the query.
- Theme refreshed to the Dracula palette to match the legacy look-and-feel.
- Banner still shows the authenticated Windows identity fetched from `/api/user/info`.

## Next Steps

- Add integration tests that feed known directory plans through the validator and executor.
- Extend the allow-lists as additional directory attributes or operations are required.
- Consider caching/streaming strategies for very large result sets.
