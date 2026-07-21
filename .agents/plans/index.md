# Optimization and Modernization Plans

This index is the canonical inventory of implementation plans derived from the 2026-07-21 codebase review. Each plan is self-contained, is committed separately, and remains implementation-blocked until its owner decisions and plan status say otherwise.

Plan reviews are advisory reviews of plan quality, performed headlessly with the machine's configured Claude harness at maximum effort. They are not `codereview` implementation verdicts because no code fix or red/green guard proof exists yet. Each plan receives at most three review rounds.

## Status

- `Queued`: plan not yet drafted.
- `In review`: draft exists and review is active.
- `Reviewed`: review comments are resolved or explicitly retained as open decisions.
- `Approved`: owner approved the plan for implementation.

## Plans

| ID | Plan | Status | Review rounds |
|---|---|---|---:|
| P01 | Verification foundation and CI | Reviewed | 2 |
| P02 | LLM provider request compatibility | Reviewed | 2 |
| P03 | Dependency security and .NET runtime modernization | Reviewed | 2 |
| P04 | CSV enrichment authorization and failure integrity | Reviewed | 2 |
| P05 | CSV enrichment scale and request limits | Reviewed | 2 |
| P06 | End-to-end query work budgets | Reviewed | 3 |
| P07 | Streaming results, exports, and artifact caching | Reviewed | 3 |
| P08 | Template expansion and LDAP filter complexity | Queued | 0 |
| P09 | Bounded and timeout-aware LDAP execution | Queued | 0 |
| P10 | Cycle-safe and bounded directory traversal | Reviewed | 3 |
| P11 | Indexed projection and single-pass aggregation | Reviewed | 2 |
| P12 | Authoritative semantic plan validation | Reviewed | 2 |
| P13 | End-to-end cancellation and error contracts | Reviewed | 2 |
| P14 | Atomic, bounded query-job orchestration | Queued | 0 |
| P15 | Safe, checked, and recoverable IIS deployment | Queued | 0 |
| P16 | Portable configuration, storage, and logging | Queued | 0 |
| P17 | Versioned feedback storage and analyzer contract | Queued | 0 |
| P18 | Standards-compliant CSV ingestion | Queued | 0 |
| P19 | Single-flight browser job polling | Queued | 0 |
| P20 | Separated liveness, readiness, and diagnostics | Queued | 0 |
| P21 | Behavior-preserving component decomposition | Queued | 0 |
