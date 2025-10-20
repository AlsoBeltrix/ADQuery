## Answers: Recursive Org Summary PRD Questions

### Current Behavior Understanding
**Q1.** Yes. Claude emits a two-step plan: find the manager, then search users where `manager` equals that DN. It returns one level of direct reports and currently works.  
**Q2.** When users ask for “everyone under …”, Claude chains multiple lookup steps. The executor lacks recursion so it stops at the first level (or returns incomplete data). Claude understands the intent but plans can’t deliver it.  
**Q3.** The `recursive` boolean on `expand_members` is already used for nested group expansion and works. Org recursion needs its own logic but we can follow the same pattern.

### Solution Approach
**Q4.** Adopt Option A: add a new operation (e.g. `expand_reports`) dedicated to org-chart recursion.  
**Q5.** Claude should generate a single step with recursion metadata; the executor performs traversal and cycle detection.  
**Q6.** ANALOG AD primarily sets `manager`. `directReports` is unreliable, so traversal must be “for each DN, search where manager = DN”.

### Aggregation Requirements
**Q7.** Only summarize when the user explicitly requests counts/rollups. Otherwise return detailed rows.  
**Q8.** Return both a summary (counts by `employeeType`) and the detailed list so the UI can display totals and still allow drilling into records.  
**Q9.** Per-level metadata (levels 0…N) should be surfaced alongside the summary so users understand the hierarchy depth covered.

### Performance & Limits
**Q10.** Depth cap of 10 prevents runaway expansions; most org charts are shallower.  
**Q11.** 5,000 node limit constrains memory usage and LDAP load; tune after perf testing.  
**Q12.** Return partial results with a prominent warning (e.g. “Stopped at depth 10” or “Reached 5,000 nodes”) rather than failing outright.

### Technical Implementation
**Q13.** Maintain a visited-`DN` set in-memory within the executor for each request.  
**Q14.** Breadth-first traversal is preferred to fetch level-by-level and batch lookups efficiently.  
**Q15.** Batch each level via an OR-filter (`(|(manager=DN1)(manager=DN2)… )`). Expect an order-of-magnitude reduction in LDAP calls.  
**Q16.** Cache DN → record mappings for the duration of the traversal to avoid re-fetching the same entries.

### Schema Changes
**Q17.** Version the schema (e.g. `schema_version: 2`) and retain backward compatibility with v1 plans.  
**Q18.** New fields on the recursive step: operation `expand_reports`, `max_depth`, `max_nodes`. Aggregation stays in the projection block.  
**Q19.** Prompt updates must teach Claude the new operation syntax, safe default limits, and when to switch from single-level to recursion.

### Validator & Security
**Q20.** Validator should enforce depth/node limits, allow recursion only on `User` target, and ensure aggregation references allow-listed attributes.  
**Q21.** Cycle detection is handled at runtime (visited set). The validator won’t catch cycles statically.

### Frontend & User Experience
**Q22.** Show warning banners combining all limit messages. Downloads remain available unless execution aborts.  
**Q23.** Display aggregation in a summary panel above the table (and include it in downloads).  
**Q24.** If a user requests “first 10”, return the first 10 encountered but compute aggregates across the full traversed set. Document the truncation.

### Testing Strategy
**Q25.** Use controlled test data (mock or dedicated OU) for deterministic org structures.  
**Q26.** Introduce artificial cycles in test data to confirm detection.  
**Q27.** Acceptable performance: typical rollups (≤200 users, depth ≤5) complete within a few seconds.

### Scope Boundaries
**Q28.** Out of scope: upward traversals, historical data, non-count aggregates, new export formats.  
**Q29.** Existing queries must remain unchanged; consider feature flag or config toggle for rollout.  
**Q30.** Design for future extensibility (additional aggregations, alternative traversals).

### Meta
**Q31.** Key open items are UX confirmation for summaries/warnings and aligning with product expectations before implementation.
