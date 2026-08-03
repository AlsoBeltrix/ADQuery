# Optimization and Modernization Plans

This index is the canonical inventory of implementation plans derived from the 2026-07-21 codebase review. Each plan is self-contained, is committed separately, and remains implementation-blocked until its owner decisions and plan status say otherwise.

Plan reviews are advisory reviews of plan quality, performed headlessly with the machine's configured Claude harness at maximum effort. They are not `codereview` implementation verdicts because no code fix or red/green guard proof exists yet. Each plan receives at most three review rounds.

## Status

- `Queued`: plan not yet drafted.
- `In review`: draft exists and review is active.
- `Reviewed`: review comments are resolved or explicitly retained as open decisions.
- `Approved`: owner approved the plan for implementation.
- `Evidence pending`: checked-in implementation is complete, but required external acceptance evidence has not yet been collected.
- `Complete`: implementation and all required acceptance evidence are complete.

## Feature plans

Feature plans (the `F` namespace) capture net-new product direction, not findings from the 2026-07-21 review. They follow the same status vocabulary and self-contained/committed-separately rules.

| ID | Plan | Status |
|---|---|---|
| F01 | Conversational query experience (headline answer, follow-up chat, park CSV in UI) | Approved |
| F02 | Main window matches the approved mockup | Done |
| F03 | Deploy can never wipe the live API key | Done |
| F04 | Genuine conversational answers (translator/narrator model; whole-conversation re-planning; delete guess-transform) | Draft |
| F05 | A bare "how many" question answers with a number (Translate prompt emits a pure count, not a group-by on a filtered attribute) | Complete |
| F06 | An empty result tells the truth, and rooms come from Exchange (honest-empty answers; allow-list synonyms; EXO read-only room source) | Slices 1-2 Complete; 3-4 Deferred (F06-D1) |
| F07 | The assist path cannot write, and a guard proves it (read-only credential + call-graph reachability boundary for LLM-in-EAW) | Deferred (F06-D1) |

## Plans

| ID | Plan | Status | Review rounds |
|---|---|---|---:|
| P01 | Verification foundation and CI | Complete | 2 |
| P02 | LLM provider request compatibility | Complete | 2 |
| P03 | Dependency security and .NET runtime modernization | Complete | 2 |
| P04 | CSV enrichment authorization and failure integrity | Complete | 2 |
| P05 | CSV enrichment scale and request limits | Reviewed | 2 |
| P06 | End-to-end query work budgets | Reviewed | 3 |
| P07 | Streaming results, exports, and artifact caching | Reviewed | 3 |
| P08 | Template expansion and LDAP filter complexity | Reviewed | 2 |
| P09 | Bounded and timeout-aware LDAP execution | Reviewed | 3 |
| P10 | Cycle-safe and bounded directory traversal | Reviewed | 3 |
| P11 | Indexed projection and single-pass aggregation | Reviewed | 2 |
| P12 | Authoritative semantic plan validation | Reviewed | 2 |
| P13 | End-to-end cancellation and error contracts | Reviewed | 3 |
| P14 | Atomic, bounded query-job orchestration | Reviewed | 3 |
| P15 | Safe, checked, and recoverable IIS deployment | Reviewed | 3 |
| P16 | Portable configuration, storage, and logging | Reviewed | 3 |
| P17 | Versioned feedback storage and analyzer contract | Reviewed | 3 |
| P18 | Standards-compliant CSV ingestion | Reviewed | 3 |
| P19 | Single-flight browser job polling | Reviewed | 3 |
| P20 | Separated liveness, readiness, and diagnostics | Reviewed | 3 |
| P21 | Behavior-preserving component decomposition | Reviewed | 3 |
