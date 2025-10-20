# Questions for PRD Author: Recursive Org Summaries

## Current Behavior Understanding

### Q1: Current Single-Level Behavior
When a user asks "show me everyone who reports to Jane Doe", what does the tool do today?
- Does Claude generate a plan with: (1) find Jane, (2) search users where manager=Jane's DN?
- Does this return exactly 1 level of direct reports?
- Is the current behavior correct for this query, or does it fail in some way?

### Q2: Current Multi-Level Attempts
When a user asks "show me EVERYONE under Jane Doe" (implying all levels), what happens today?
- Does Claude try to generate multiple chained lookups?
- Does it fail with an error?
- Does it return only 1 level and the user doesn't get what they asked for?
- Does Claude understand the difference between "direct reports" and "all reports recursively"?

### Q3: expand_members Recursive Flag
The DirectoryPlanStep already has a `recursive` boolean property (line 75 of DirectoryQueryPlan.cs).
- Is this currently used by expand_members for nested group expansion?
- Does it actually work recursively today, or is it ignored?
- Can we reuse this same pattern for org hierarchy recursion?

## Solution Approach

### Q4: New Operation vs Enhanced Logic
The PRD mentions "new plan operations (e.g. `expand_reports`)". Which approach is intended:

**Option A: New Operation Type**
- Add "expand_reports" as a 4th operation type (alongside search, expand_members, lookup)
- This operation specifically handles manager→reports recursion
- Executor implements breadth-first traversal when it sees this operation

**Option B: Enhance Existing Operations**
- Keep only 3 operation types
- Enhance the executor to detect recursive patterns in existing lookup/search operations
- Add metadata/flags to indicate recursive intent

**Option C: Something else**
- Please describe if neither A nor B is correct

### Q5: Where Does Recursion Logic Live?
- Does Claude generate a single step that says "recurse here" and the executor handles the traversal?
- Or does Claude generate a multi-step plan with explicit recursion markers?
- Who is responsible for cycle detection - Claude's plan or the executor?

### Q6: Manager vs DirectReports Attribute
- Does ANALOG AD have a `directReports` attribute populated?
- Or do we ONLY have the `manager` attribute (child→parent direction)?
- If we only have `manager`, recursion means: for each person, search where manager=theirDN, repeat for results

## Aggregation Requirements

### Q7: What Triggers Aggregation?
When should the tool return aggregated summaries vs full record lists?
- Always when recursion depth > X?
- Only when user explicitly asks for counts/summaries?
- Both (return both full data AND aggregation)?

### Q8: Aggregation Output Format
When a user asks "count employees by type under Jane Doe":
- Do we return ONLY the aggregation (`{"FTE": 45, "Contractor": 12}`)?
- Or aggregation + full record list?
- Where does aggregation appear in the API response?

### Q9: Level-by-Level Metadata
"optional per-level metadata" - what does this mean specifically?
- Level 0 (Jane): 1 person
- Level 1 (direct reports): 8 people
- Level 2 (reports' reports): 23 people
- Is this only for debugging, or does the user see this?

## Performance & Limits

### Q10: Why Max Depth 10?
What's the reasoning behind the depth limit?
- Typical org structure depth in ANALOG AD?
- Performance boundary (LDAP query count/latency)?
- User experience (results too large)?

### Q11: Why Max 5000 Nodes?
What drives the 5000 node limit?
- Memory constraints in the API?
- Download size limits?
- LDAP query performance?

### Q12: What Happens at Limits?
When limits are hit, what should the user experience be?
- Partial results returned (first 5000 people found)?
- Error message (query too large, narrow your search)?
- Warning banner + partial results?

## Technical Implementation

### Q13: Visited Tracking Mechanism
For cycle detection, where is the visited set maintained?
- In-memory during executor traversal?
- Per-step state in DirectoryPlanRuntime?
- As a new property on DirectoryPlanStep?

### Q14: Breadth-First vs Depth-First
PRD specifies "breadth-first recursive expansion". Why breadth-first?
- Does it matter for correctness?
- Performance characteristic (LDAP query batching)?
- Result ordering preference?

### Q15: Batch LDAP Optimization
"Optimize manager lookups (batch LDAP requests)" - specifically:
- For level N, collect all DNs, issue single OR filter: `(|(manager=DN1)(manager=DN2)...)`?
- Or something else?
- What's the expected query reduction (10x? 100x?)?

### Q16: Caching Strategy
"caching identifiers we already fetched" - what specifically:
- Cache DN→record mappings for the duration of a single query execution?
- Across multiple queries (per-user session cache)?
- How long are cached entries valid?

## Schema Changes

### Q17: DirectoryQueryPlan Schema Version
Should we add `schema_version` field for backward compatibility?
- How do we handle v1 plans (today's format) in executor?
- Can v1 and v2 coexist?

### Q18: Required New Fields
Which new fields are REQUIRED on DirectoryPlanStep for recursive operations?
- `max_depth`? (required or optional with default?)
- `visited_tracking`? (always true, or user-controllable?)
- `traversal_direction`? (or always downward for org queries?)
- `aggregation`? (on projection or step level?)

### Q19: Claude Prompt Changes
What must Claude learn to generate recursive plans correctly?
- New operation types and their syntax?
- When to use recursion vs multi-step manual plans?
- How to set appropriate depth limits based on query intent?

## Validator & Security

### Q20: New Validation Rules
What new security constraints are needed?
- `max_depth` must be between 1-10?
- New operation types allowed by default?
- Recursion only allowed for specific target_types?
- Aggregation fields must be in allow-list?

### Q21: Cycle Detection at Validation Time
Can we detect potential cycles during plan validation?
- Static analysis of step dependencies?
- Or only at runtime during execution?

## Frontend & User Experience

### Q22: Warning Display Priority
If multiple warnings occur (depth limit + node limit), how are they shown?
- Single banner with all warnings?
- Dismissible warnings?
- Affect download/export behavior?

### Q23: Aggregation Display
When aggregation is returned, where does it appear in the UI?
- Above the result table?
- Separate tab?
- Expandable section?
- Always shown if present?

### Q24: Result Limit Interaction
If user asks "show first 10 employees under Jane" AND there are 100 total recursively:
- Return first 10 encountered during traversal?
- Traverse all, then return 10?
- Aggregation still shows all 100?

## Testing Strategy

### Q25: Test Org Structure
For testing, do we:
- Create mock AD test data with known org structure?
- Use a test OU in actual ANALOG AD?
- Mock the ActiveDirectoryService entirely?

### Q26: Cycle Testing
How do we test cycle detection?
- Create test data with A→B, B→A manager relationships?
- Or assume this can't happen in real AD and skip?

### Q27: Performance Baseline
What's the acceptance criteria for "good enough" performance?
- Query types and expected latency?
- Specific org structure sizes and max duration?

## Scope Boundaries

### Q28: What's NOT in Scope?
Which features are explicitly NOT part of this implementation?
- Upward traversal (employee → manager chain)?
- Cross-department aggregations?
- Historical org structure changes?
- Export format changes?

### Q29: Backward Compatibility
Must existing queries continue to work identically?
- Existing plans execute with same results?
- No breaking changes to API responses?
- Phased rollout possible (feature flag)?

### Q30: Future Extensions
What's designed to be extensible later?
- Additional aggregation functions (sum, avg, etc.)?
- More complex graph traversals?
- Custom recursion callbacks?

---

## Meta Question

**Q31: What am I still fundamentally misunderstanding about this tool or the PRD?**

If I'm asking the wrong questions or approaching this from the wrong angle, please explain what I should be focusing on instead.
