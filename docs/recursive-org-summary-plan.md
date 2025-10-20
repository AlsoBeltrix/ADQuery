## Feature Plan: Recursive Org Summaries with Aggregation

### 1. Clarify Requirements
- Confirm desired depth cap and acceptable latency for reporting-tree expansion.
- Decide what summarisation data is required (totals only vs. level-by-level detail).
- Agree on behaviour when limits are hit (partial results, warnings, truncation messages).

### 2. Extend Plan Schema & Prompt
- Add new plan operations (e.g. `expand_reports`) that describe recursive org expansion with depth/result limits.
- Introduce aggregation directives in the projection contract (`group_by`, `count`, optional per-level metadata).
- Update Claude prompt/examples so recursive queries request the new operations instead of multi-step loops.

### 3. Validator Updates
- Enforce recursion/aggregation constraints (max depth, max nodes, allowed aggregates).
- Detect cycles or unbounded expansions, provide clear validation errors.
- Expand allow-list logic/tests to cover the new schema elements.

### 4. Executor Enhancements
- Implement breadth-first recursive expansion with visited tracking and cancellation support.
- Add an aggregation pass that groups results by `employeeType` (and optionally level) according to the new projection schema.
- Emit warnings when recursion or aggregation truncates due to limits.

### 5. ActiveDirectoryService Adjustments
- Optimise manager lookups (batch LDAP requests, caching identifiers we already fetched).
- Ensure required attributes (manager, employeeType, etc.) are automatically requested and remain allow-listed.

### 6. Front-End & API Work
- Surface new warnings/errors (e.g. recursion depth exceeded) in the UI.
- Extend results rendering/downloading to handle aggregated datasets.
- Update on-page guidance/tips to describe the new query capabilities and limitations.

### 7. Testing & Deployment
- Unit/integration tests covering validator changes, recursive executor behaviour, aggregation correctness, and guard-rail enforcement.
- Load/perf tests with representative org structures to tune default limits.
- Update documentation/README and deploy via existing IIS publishing process.
