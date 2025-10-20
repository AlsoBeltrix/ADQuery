# Recursive Org Summary - Implementation Workflow

## Overview

Add `expand_reports` operation to enable recursive organizational hierarchy traversal with aggregation. When users ask "show everyone under [person]", the tool will traverse the complete reporting structure and optionally return aggregated summaries.

**Key Principle**: Claude generates a single step with recursion metadata → Executor performs breadth-first traversal with batched LDAP queries.

---

## Architecture Decisions

### Synchronous vs Async Execution

**Decision**: Stay synchronous for v1, with async job pattern as future enhancement.

**Rationale**:
- Synchronous is simpler to implement and debug
- Most queries complete in seconds (< 10K nodes)
- Large queries (40K nodes) will take 2-5 minutes - acceptable with progress indication

**Requirements for synchronous approach**:
- ✅ Progress indicator in UI ("Processing... may take a few minutes")
- ✅ Conservative defaults (10K node limit) - Claude raises explicitly for full org
- ⚠️ **MUST stress-test with 40K nodes before production deployment**
- ⚠️ HTTP timeout configured to 10+ minutes in IIS
- ⚠️ Cancellation token support to kill long-running queries

**Future enhancement (post-v1)**:
If synchronous proves brittle under load, migrate to async job pattern:
- POST /api/query/execute → 202 Accepted with jobId
- GET /api/query/jobs/{jobId} → poll for status/progress
- Download results when complete

### Limits Strategy

**Configured Maximums** (what system CAN handle):
- MaxRecursionDepth: 100
- MaxNodesPerRecursion: 50,000

**Executor Defaults** (what queries GET unless explicit):
- Default depth: 10
- Default nodes: 10,000

**Claude Prompt Guidance** (when to raise limits):
- Team/department queries: Use defaults
- "Entire org" queries: Explicitly set max_depth=50, max_nodes=50000

---

## Implementation Phases

### Phase 1: Schema & Model Updates
**Duration**: 1-2 days
**Files**: `csharp/Models/DirectoryQueryPlan.cs`

#### 1.1 Add New Fields to DirectoryPlanStep
```csharp
/// <summary>
/// Maximum recursion depth (1-100). Safety valve against infinite cycles.
/// Primary stop condition is natural end of org tree or max_nodes limit.
/// </summary>
[JsonPropertyName("max_depth")]
public int? MaxDepth { get; set; }

/// <summary>
/// Maximum total nodes to retrieve during recursive expansion.
/// </summary>
[JsonPropertyName("max_nodes")]
public int? MaxNodes { get; set; }
```

#### 1.2 Add AggregationDefinition to ProjectionDefinition
```csharp
/// <summary>
/// Optional aggregation rules for summary data.
/// </summary>
[JsonPropertyName("aggregation")]
public AggregationDefinition? Aggregation { get; set; }
```

#### 1.3 Create AggregationDefinition Class
```csharp
public class AggregationDefinition
{
    /// <summary>
    /// Attributes to group by (e.g. ["employeeType"]).
    /// </summary>
    [JsonPropertyName("group_by")]
    public List<string> GroupBy { get; set; } = new();

    /// <summary>
    /// Whether to include record counts per group.
    /// </summary>
    [JsonPropertyName("count")]
    public bool Count { get; set; }

    /// <summary>
    /// Include per-level metadata (nodes per depth level).
    /// </summary>
    [JsonPropertyName("include_level_metadata")]
    public bool IncludeLevelMetadata { get; set; }
}
```

#### 1.4 Add Schema Version
```csharp
// In DirectoryQueryPlan class
[JsonPropertyName("schema_version")]
public int SchemaVersion { get; set; } = 2;
```

**Acceptance**:
- Models compile without errors
- JSON serialization/deserialization works with new fields
- Backward compat: v1 plans (without new fields) still deserialize

---

### Phase 2: Executor - Recursive Traversal
**Duration**: 3-4 days
**Files**: `csharp/Services/DirectoryPlanExecutor.cs`, `csharp/Services/ActiveDirectoryService.cs`

#### 2.1 Add ExecuteExpandReports Method

```csharp
private async Task<StepRuntimeState> ExecuteExpandReports(
    DirectoryPlanStep step,
    DirectoryPlanRuntime runtime,
    CancellationToken cancellationToken)
{
    var maxDepth = step.MaxDepth ?? 10; // Conservative default; Claude raises explicitly
    var maxNodes = step.MaxNodes ?? 10000; // Conservative; handles most queries
    var visitedDNs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    var levelResults = new Dictionary<int, List<DirectoryRecord>>();
    var warnings = new List<string>();

    // Get seed DNs from source step
    var sourceState = runtime.StepStates[step.Source];
    var seedDNs = sourceState.Records
        .Select(r => r.GetAttributeValue("distinguishedName"))
        .Where(dn => !string.IsNullOrEmpty(dn))
        .ToList();

    levelResults[0] = sourceState.Records;

    // Breadth-first traversal
    var currentLevelDNs = seedDNs;
    for (int depth = 1; depth <= maxDepth; depth++)
    {
        if (!currentLevelDNs.Any()) break;

        // Mark as visited
        foreach (var dn in currentLevelDNs)
        {
            visitedDNs.Add(dn);
        }

        // Batch query: find all direct reports for this level
        var directReports = await _adService.GetDirectReportsBatch(
            currentLevelDNs,
            step.Attributes,
            cancellationToken
        );

        if (!directReports.Any()) break;

        // Check node limit
        var totalNodes = levelResults.Values.Sum(list => list.Count) + directReports.Count;
        if (totalNodes > maxNodes)
        {
            warnings.Add($"Stopped at {maxNodes} nodes (limit reached)");

            // Take what we can fit
            var remaining = maxNodes - levelResults.Values.Sum(list => list.Count);
            directReports = directReports.Take(remaining).ToList();
            levelResults[depth] = directReports;
            break;
        }

        levelResults[depth] = directReports;

        // Prepare next level DNs (excluding cycles)
        currentLevelDNs = directReports
            .Select(r => r.GetAttributeValue("distinguishedName"))
            .Where(dn => !string.IsNullOrEmpty(dn) && !visitedDNs.Contains(dn))
            .ToList();

        // Check if we hit max depth
        if (depth == maxDepth && currentLevelDNs.Any())
        {
            warnings.Add($"Stopped at depth {maxDepth} (limit reached)");
        }
    }

    // Flatten all levels (excluding seed level 0)
    var allRecords = levelResults
        .Where(kvp => kvp.Key > 0)
        .SelectMany(kvp => kvp.Value)
        .ToList();

    return new StepRuntimeState
    {
        Records = allRecords,
        LevelMetadata = levelResults.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.Count
        ),
        Warnings = warnings
    };
}
```

#### 2.2 Update ExecuteStep to Handle expand_reports
```csharp
private async Task<StepRuntimeState> ExecuteStep(
    DirectoryPlanStep step,
    DirectoryPlanRuntime runtime,
    CancellationToken cancellationToken)
{
    return step.Operation switch
    {
        "search" => await ExecuteSearch(step, runtime, cancellationToken),
        "expand_members" => await ExecuteExpandMembers(step, runtime, cancellationToken),
        "lookup" => await ExecuteLookup(step, runtime, cancellationToken),
        "expand_reports" => await ExecuteExpandReports(step, runtime, cancellationToken),
        _ => throw new InvalidOperationException($"Unknown operation: {step.Operation}")
    };
}
```

#### 2.3 Add StepRuntimeState.LevelMetadata Property
```csharp
public class StepRuntimeState
{
    public List<DirectoryRecord> Records { get; set; } = new();
    public List<string> Warnings { get; set; } = new();

    // NEW
    public Dictionary<int, int>? LevelMetadata { get; set; }
}
```

**Acceptance**:
- Breadth-first traversal works correctly
- Visited tracking prevents infinite loops
- Depth and node limits enforced with warnings
- Level metadata captured

---

### Phase 3: ActiveDirectory - Batch Queries
**Duration**: 1-2 days
**Files**: `csharp/Services/ActiveDirectoryService.cs`

#### 3.1 Add GetDirectReportsBatch Method

```csharp
public async Task<List<DirectoryRecord>> GetDirectReportsBatch(
    List<string> managerDNs,
    List<string> attributes,
    CancellationToken cancellationToken)
{
    if (!managerDNs.Any()) return new List<DirectoryRecord>();

    // Build OR filter: (|(manager=DN1)(manager=DN2)...)
    var filterParts = managerDNs
        .Select(dn => $"(manager={LdapEscape(dn)})")
        .ToList();

    var filter = filterParts.Count == 1
        ? filterParts[0]
        : $"(|{string.Join("", filterParts)})";

    // Combine with objectClass filter
    var combinedFilter = $"(&(objectClass=user){filter})";

    _logger.LogDebug("Batch direct reports query for {Count} managers", managerDNs.Count);

    return await ExecuteSearch(new DirectorySearchRequest
    {
        Filter = combinedFilter,
        Attributes = EnsureRequiredAttributes(attributes),
        ObjectClass = "user"
    }, cancellationToken);
}

private List<string> EnsureRequiredAttributes(List<string> requested)
{
    var attributes = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
    attributes.Add("distinguishedName"); // Always needed for recursion
    attributes.Add("manager"); // Needed for next level
    return attributes.ToList();
}

private string LdapEscape(string value)
{
    // Escape special LDAP characters
    return value
        .Replace("\\", "\\5c")
        .Replace("*", "\\2a")
        .Replace("(", "\\28")
        .Replace(")", "\\29")
        .Replace("\0", "\\00");
}
```

**Acceptance**:
- Single LDAP query per level (not N queries)
- Correctly handles 1 DN, 10 DNs, 100 DNs
- Performance test: 50 managers → 1 query, not 50

---

### Phase 4: Aggregation Engine
**Duration**: 1-2 days
**Files**: `csharp/Services/DirectoryPlanExecutor.cs`

#### 4.1 Add Aggregation Logic to ApplyProjection

```csharp
private RuntimeResult ApplyProjection(
    DirectoryQueryPlan plan,
    DirectoryPlanRuntime runtime)
{
    var projection = plan.Projection;
    var rowState = runtime.StepStates[projection.RowStep];

    // Existing projection logic
    var rows = BuildProjectionRows(projection, runtime);

    // NEW: Compute aggregation if requested
    Dictionary<string, object>? aggregation = null;
    if (projection.Aggregation != null)
    {
        aggregation = new Dictionary<string, object>();

        if (projection.Aggregation.Count && projection.Aggregation.GroupBy.Any())
        {
            var grouped = rowState.Records
                .GroupBy(r =>
                {
                    var keys = projection.Aggregation.GroupBy
                        .Select(field => r.GetAttributeValue(field) ?? "(empty)")
                        .ToList();
                    return string.Join("|", keys);
                })
                .ToDictionary(
                    g => g.Key,
                    g => g.Count()
                );

            aggregation["grouped_counts"] = grouped;
        }

        if (projection.Aggregation.IncludeLevelMetadata && rowState.LevelMetadata != null)
        {
            aggregation["level_metadata"] = rowState.LevelMetadata;
        }
    }

    // Collect warnings from all steps
    var allWarnings = runtime.StepStates.Values
        .SelectMany(s => s.Warnings)
        .ToList();

    return new RuntimeResult
    {
        Rows = rows,
        Aggregation = aggregation,
        Warnings = allWarnings
    };
}
```

#### 4.2 Update RuntimeResult Class
```csharp
public class RuntimeResult
{
    public List<Dictionary<string, string>> Rows { get; set; } = new();

    // NEW
    public Dictionary<string, object>? Aggregation { get; set; }
    public List<string> Warnings { get; set; } = new();
}
```

**Acceptance**:
- Aggregation by single field works
- Aggregation by multiple fields works
- Level metadata included when requested
- Empty groups handled correctly

---

### Phase 4.5: API/Response Wiring for Aggregation
**Duration**: 1 day
**Files**: `csharp/Controllers/QueryController.cs`, `csharp/Models/QueryResponses.cs`

#### 4.5.1 Define Complete Response Models

```csharp
// New response model in QueryController.cs or separate Models file
public class QueryExecuteResponse
{
    [JsonPropertyName("previewRows")]
    public List<Dictionary<string, string>> PreviewRows { get; set; } = new();

    [JsonPropertyName("totalRows")]
    public int TotalRows { get; set; }

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    // NEW: Aggregation summary
    [JsonPropertyName("aggregation")]
    public AggregationSummary? Aggregation { get; set; }

    // NEW: Warnings from execution
    [JsonPropertyName("warnings")]
    public List<string>? Warnings { get; set; }
}

public class AggregationSummary
{
    /// <summary>
    /// Grouped counts: key is concatenated group_by values, value is count
    /// Example: { "FTE": 45, "Contractor": 12 }
    /// </summary>
    [JsonPropertyName("grouped_counts")]
    public Dictionary<string, int> GroupedCounts { get; set; } = new();

    /// <summary>
    /// Per-level node counts (level → count)
    /// Example: { "0": 1, "1": 8, "2": 23, "3": 45 }
    /// </summary>
    [JsonPropertyName("level_metadata")]
    public Dictionary<int, int>? LevelMetadata { get; set; }

    /// <summary>
    /// Fields used for grouping (for display purposes)
    /// Example: ["employeeType"]
    /// </summary>
    [JsonPropertyName("group_by_fields")]
    public List<string> GroupByFields { get; set; } = new();
}
```

**Example JSON Response Payload**:
```json
{
  "previewRows": [
    {"Name": "Smith, John", "Type": "FTE"},
    {"Name": "Doe, Jane", "Type": "Contractor"},
    ...
  ],
  "totalRows": 1247,
  "requestId": "abc-123",
  "aggregation": {
    "grouped_counts": {
      "FTE": 1045,
      "Contractor": 187,
      "Intern": 15
    },
    "level_metadata": {
      "0": 1,
      "1": 12,
      "2": 87,
      "3": 342,
      "4": 805
    },
    "group_by_fields": ["employeeType"]
  },
  "warnings": [
    "Stopped at depth 10 (5 nodes remaining)"
  ]
}
```

#### 4.5.2 Update Execute Endpoint in QueryController

```csharp
[HttpPost("execute")]
public async Task<IActionResult> Execute([FromBody] QueryExecuteRequest request)
{
    try
    {
        // Existing validation...
        var plan = await _claudeService.GenerateExecutionPlanAsync(request.Query);
        var validationResult = _validator.Validate(plan.Plan);

        if (!validationResult.IsValid)
        {
            return BadRequest(new { errors = validationResult.SecurityErrors });
        }

        // Execute plan
        var result = await _executor.ExecuteAsync(plan.Plan, cancellationToken);

        // NEW: Build aggregation summary if present
        AggregationSummary? aggregationSummary = null;
        if (result.Aggregation != null)
        {
            aggregationSummary = new AggregationSummary();

            if (result.Aggregation.ContainsKey("grouped_counts"))
            {
                aggregationSummary.GroupedCounts =
                    (Dictionary<string, int>)result.Aggregation["grouped_counts"];
            }

            if (result.Aggregation.ContainsKey("level_metadata"))
            {
                aggregationSummary.LevelMetadata =
                    (Dictionary<int, int>)result.Aggregation["level_metadata"];
            }

            // Store group_by fields for UI display
            if (plan.Plan.Projection?.Aggregation != null)
            {
                aggregationSummary.GroupByFields =
                    plan.Plan.Projection.Aggregation.GroupBy;
            }
        }

        // Cache full results for download
        var requestId = Guid.NewGuid().ToString();
        _cache.Set($"query_result_{requestId}", result, TimeSpan.FromMinutes(30));

        return Ok(new QueryExecuteResponse
        {
            PreviewRows = result.Rows.Take(10).ToList(),
            TotalRows = result.Rows.Count,
            RequestId = requestId,
            Aggregation = aggregationSummary,
            Warnings = result.Warnings?.Any() == true ? result.Warnings : null
        });
    }
    catch (Exception ex)
    {
        _logger.LogError(ex, "Error executing query");
        return StatusCode(500, new { error = "Query execution failed" });
    }
}
```

#### 4.5.3 Update Download Endpoints to Include Aggregation

```csharp
[HttpGet("download/csv/{requestId}")]
public IActionResult DownloadCsv(string requestId)
{
    if (!_cache.TryGetValue($"query_result_{requestId}", out RuntimeResult result))
    {
        return NotFound(new { error = "Results expired or not found" });
    }

    var csv = new StringBuilder();

    // Header row
    if (result.Rows.Any())
    {
        csv.AppendLine(string.Join(",", result.Rows[0].Keys.Select(EscapeCsv)));
    }

    // Data rows
    foreach (var row in result.Rows)
    {
        csv.AppendLine(string.Join(",", row.Values.Select(EscapeCsv)));
    }

    // NEW: Append aggregation summary as comments
    if (result.Aggregation != null)
    {
        csv.AppendLine();
        csv.AppendLine("# SUMMARY");

        if (result.Aggregation.ContainsKey("grouped_counts"))
        {
            var counts = (Dictionary<string, int>)result.Aggregation["grouped_counts"];
            csv.AppendLine("# Category,Count");
            foreach (var (key, count) in counts)
            {
                csv.AppendLine($"# {EscapeCsv(key)},{count}");
            }
        }

        if (result.Aggregation.ContainsKey("level_metadata"))
        {
            csv.AppendLine("# ");
            csv.AppendLine("# HIERARCHY DEPTH");
            var levels = (Dictionary<int, int>)result.Aggregation["level_metadata"];
            csv.AppendLine("# Level,Count");
            foreach (var (level, count) in levels.OrderBy(kvp => kvp.Key))
            {
                csv.AppendLine($"# Level {level},{count}");
            }
        }
    }

    // NEW: Append warnings as comments
    if (result.Warnings?.Any() == true)
    {
        csv.AppendLine();
        csv.AppendLine("# WARNINGS");
        foreach (var warning in result.Warnings)
        {
            csv.AppendLine($"# {warning}");
        }
    }

    return File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv", $"adquery_{requestId}.csv");
}
```

**Complete Data Flow**:
```
DirectoryPlanExecutor.ExecuteAsync()
  ↓ returns RuntimeResult { Rows, Aggregation (dict), Warnings }
  ↓
QueryController.Execute()
  ↓ transforms to AggregationSummary { GroupedCounts, LevelMetadata, GroupByFields }
  ↓ builds QueryExecuteResponse
  ↓
Frontend (app.js)
  ↓ receives JSON: { previewRows, totalRows, aggregation: {...}, warnings: [...] }
  ↓ calls displayAggregation(response.aggregation)
  ↓ renders into DOM element #aggregation-summary
```

**Acceptance**:
- Execute endpoint returns aggregation in response.aggregation field
- Download CSV includes aggregation as # comments at bottom
- Null aggregation handled gracefully (field omitted or null)
- Warnings flow through to response.warnings array

---

### Phase 5: Validator Updates
**Duration**: 1-2 days
**Files**: `csharp/Security/PlanValidator.cs`, `appsettings.json`

#### 5.1 Add Configuration Limits and Feature Flag

```json
// appsettings.json → Security section
"Security": {
  "MaxRecursionDepth": 100,              // Maximum allowed (safety valve for cycles)
  "MaxNodesPerRecursion": 50000,         // Maximum allowed (above total user count)
  "DefaultRecursionDepth": 10,           // Conservative default for executor
  "DefaultMaxNodes": 10000,              // Conservative default for executor
  "EnableRecursiveQueries": true,        // Feature flag for gradual rollout
  // ... existing security settings
}
```

#### 5.2 Add expand_reports to Allowed Operations

```csharp
private static readonly HashSet<string> AllowedOperations = new()
{
    "search",
    "expand_members",
    "lookup",
    "expand_reports" // NEW
};
```

#### 5.3 Comprehensive expand_reports Validation (Edge Cases Explicit)

```csharp
private PlanSecurityResult ValidateExpandReports(DirectoryPlanStep step)
{
    var errors = new List<string>();

    // EDGE CASE 1: Feature disabled
    if (!_config.GetValue<bool>("Security:EnableRecursiveQueries"))
    {
        errors.Add($"Step '{step.Name}': recursive queries are currently disabled");
        return new PlanSecurityResult { IsValid = false, SecurityErrors = errors };
    }

    // EDGE CASE 2: Missing max_depth
    if (!step.MaxDepth.HasValue)
    {
        errors.Add($"Step '{step.Name}': expand_reports requires max_depth (missing)");
    }

    // EDGE CASE 3: max_depth out of range
    if (step.MaxDepth.HasValue)
    {
        if (step.MaxDepth.Value < 1)
        {
            errors.Add($"Step '{step.Name}': max_depth must be >= 1 (got {step.MaxDepth.Value})");
        }

        if (step.MaxDepth.Value > _config.GetValue<int>("Security:MaxRecursionDepth"))
        {
            errors.Add($"Step '{step.Name}': max_depth {step.MaxDepth.Value} exceeds limit of {_config.GetValue<int>("Security:MaxRecursionDepth")}");
        }
    }

    // EDGE CASE 4: max_nodes out of range (if specified)
    if (step.MaxNodes.HasValue)
    {
        var maxNodesLimit = _config.GetValue<int>("Security:MaxNodesPerRecursion");

        if (step.MaxNodes.Value < 1)
        {
            errors.Add($"Step '{step.Name}': max_nodes must be >= 1 (got {step.MaxNodes.Value})");
        }

        if (step.MaxNodes.Value > maxNodesLimit)
        {
            errors.Add($"Step '{step.Name}': max_nodes {step.MaxNodes.Value} exceeds limit of {maxNodesLimit}");
        }
    }

    // EDGE CASE 5: Missing source step
    if (string.IsNullOrEmpty(step.Source))
    {
        errors.Add($"Step '{step.Name}': expand_reports requires 'source' field referencing a prior step");
    }

    // EDGE CASE 6: Invalid target_type (MUST be User)
    if (step.TargetType != DirectoryObjectType.User)
    {
        errors.Add($"Step '{step.Name}': expand_reports only supports target_type 'User' (got '{step.TargetType}')");
    }

    // EDGE CASE 7: Source step doesn't exist (check runtime)
    // Note: This is validated in main Validate() method when checking step references

    // EDGE CASE 8: Attributes list empty
    if (step.Attributes == null || !step.Attributes.Any())
    {
        errors.Add($"Step '{step.Name}': expand_reports requires at least one attribute");
    }

    return new PlanSecurityResult
    {
        IsValid = !errors.Any(),
        SecurityErrors = errors
    };
}
```

#### 5.4 Comprehensive Aggregation Validation (Edge Cases Explicit)

```csharp
private PlanSecurityResult ValidateAggregation(ProjectionDefinition projection)
{
    if (projection.Aggregation == null) return PlanSecurityResult.Success();

    var errors = new List<string>();
    var agg = projection.Aggregation;

    // EDGE CASE 1: No grouping fields and count=false
    if (!agg.GroupBy.Any() && !agg.Count)
    {
        errors.Add("Aggregation requires at least 'group_by' fields or 'count: true'");
    }

    // EDGE CASE 2: Empty group_by fields
    if (agg.GroupBy.Any(string.IsNullOrWhiteSpace))
    {
        errors.Add("Aggregation group_by contains empty or null field names");
    }

    // EDGE CASE 3: Validate all group_by fields are in allow-list
    foreach (var field in agg.GroupBy.Where(f => !string.IsNullOrWhiteSpace(f)))
    {
        if (!IsAttributeAllowed(field, DirectoryObjectType.User))
        {
            errors.Add($"Aggregation field '{field}' is not in the attribute allow-list");
        }
    }

    // EDGE CASE 4: Too many group_by fields
    if (agg.GroupBy.Count > 5)
    {
        errors.Add($"Aggregation group_by has {agg.GroupBy.Count} fields; maximum is 5");
    }

    // EDGE CASE 5: Duplicate group_by fields
    var duplicates = agg.GroupBy
        .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1)
        .Select(g => g.Key)
        .ToList();

    if (duplicates.Any())
    {
        errors.Add($"Aggregation group_by contains duplicate fields: {string.Join(", ", duplicates)}");
    }

    return new PlanSecurityResult
    {
        IsValid = !errors.Any(),
        SecurityErrors = errors
    };
}
```

#### 5.5 Integrate into Main Validation

```csharp
public PlanSecurityResult Validate(DirectoryQueryPlan plan)
{
    var results = new List<PlanSecurityResult>();

    // Existing validations
    results.Add(ValidateComplexity(plan));
    results.Add(ValidateOperations(plan));

    // Validate each step
    foreach (var step in plan.Steps)
    {
        if (step.Operation == "expand_reports")
        {
            results.Add(ValidateExpandReports(step));
        }

        // Other step validations...
    }

    // NEW: Validate aggregation
    if (plan.Projection?.Aggregation != null)
    {
        results.Add(ValidateAggregation(plan.Projection));
    }

    return CombineResults(results);
}
```

**Acceptance**:
- expand_reports without max_depth → validation error
- max_depth > 100 → validation error
- max_depth < 1 → validation error
- expand_reports with target_type=Group → validation error
- Aggregation with disallowed field → validation error
- Valid recursive plan (max_depth=50, max_nodes=50000) → passes validation

---

### Phase 6: Claude Prompt Contract
**Duration**: 1 day
**Files**: `csharp/Services/ClaudeService.cs`

#### 6.1 Complete expand_reports Documentation in Prompt

Add to the PLAN REQUIREMENTS section (after line 218, before filter operators):

```csharp
promptBuilder.AppendLine();
promptBuilder.AppendLine("NEW OPERATION: expand_reports");
promptBuilder.AppendLine("- Use expand_reports to recursively traverse organizational reporting structures.");
promptBuilder.AppendLine("- Syntax: { \"operation\": \"expand_reports\", \"source\": \"<prior_step>\", \"target_type\": \"User\", \"max_depth\": <1-100>, \"attributes\": [...] }");
promptBuilder.AppendLine("- REQUIRED fields:");
promptBuilder.AppendLine("  • operation: must be \"expand_reports\"");
promptBuilder.AppendLine("  • source: name of prior step containing manager(s)");
promptBuilder.AppendLine("  • target_type: must be \"User\" (org hierarchy only works for users)");
promptBuilder.AppendLine("  • max_depth: integer 1-100 (safety valve for cycles; primary stop is natural end of tree)");
promptBuilder.AppendLine("  • attributes: list of user attributes to retrieve (must include 'manager' for next level)");
promptBuilder.AppendLine("- OPTIONAL fields:");
promptBuilder.AppendLine("  • max_nodes: maximum total employees to retrieve (default 50000, exceeds total org size)");
promptBuilder.AppendLine();
promptBuilder.AppendLine("WHEN TO USE expand_reports:");
promptBuilder.AppendLine("- User asks: 'everyone under [person]', 'all reports for [person]', 'org chart for [department]'");
promptBuilder.AppendLine("- User asks: 'count employees', 'how many people report to', 'team size'");
promptBuilder.AppendLine("- DO NOT use for single-level direct reports - use regular search with manager filter instead");
promptBuilder.AppendLine();
promptBuilder.AppendLine("SETTING max_depth AND max_nodes:");
promptBuilder.AppendLine("- DEFAULT (omit max_depth/max_nodes): depth=10, nodes=10000 (handles most queries)");
promptBuilder.AppendLine("- SPECIFIC TEAM/DEPARTMENT: max_depth=20, max_nodes=10000");
promptBuilder.AppendLine("- ENTIRE ORG (user says 'everyone under CEO' or 'complete org'): max_depth=50, max_nodes=50000");
promptBuilder.AppendLine("- For 'direct reports only', use regular search instead of expand_reports");
promptBuilder.AppendLine("- Primary stop condition: no more employees found (natural end of tree)");
promptBuilder.AppendLine("- Secondary stop condition: max_nodes limit (with warning)");
promptBuilder.AppendLine();
promptBuilder.AppendLine("AGGREGATION WITH expand_reports:");
promptBuilder.AppendLine("- When user asks for counts/summaries, add aggregation block to projection:");
promptBuilder.AppendLine("  \"aggregation\": {");
promptBuilder.AppendLine("    \"group_by\": [\"employeeType\"],  // or [\"department\"], [\"title\"], etc.");
promptBuilder.AppendLine("    \"count\": true,");
promptBuilder.AppendLine("    \"include_level_metadata\": true  // shows nodes per hierarchy level");
promptBuilder.AppendLine("  }");
promptBuilder.AppendLine("- Still include normal columns so user can see detail records");
```

#### 6.2 Add Multiple expand_reports Examples

Replace the single example with comprehensive examples (after line 289):

```csharp
promptBuilder.AppendLine();
promptBuilder.AppendLine("=== EXPAND_REPORTS EXAMPLES ===");
promptBuilder.AppendLine();

// Example 1: Typical team query (use defaults)
promptBuilder.AppendLine("EXAMPLE 2a - Query: \"Show everyone who reports to Jane Doe\"");
promptBuilder.AppendLine("NOTE: Typical team query - OMIT max_depth/max_nodes to use defaults (10/10000)");
promptBuilder.AppendLine("```json");
promptBuilder.AppendLine("{");
promptBuilder.AppendLine("  \"description\": \"All employees reporting to Jane Doe\",");
promptBuilder.AppendLine("  \"schema_version\": 2,");
promptBuilder.AppendLine("  \"steps\": [");
promptBuilder.AppendLine("    {");
promptBuilder.AppendLine("      \"step\": 1,");
promptBuilder.AppendLine("      \"name\": \"find_jane\",");
promptBuilder.AppendLine("      \"operation\": \"search\",");
promptBuilder.AppendLine("      \"target_type\": \"User\",");
promptBuilder.AppendLine("      \"filters\": [{ \"attribute\": \"displayName\", \"operator\": \"equals\", \"value\": \"Doe, Jane\" }],");
promptBuilder.AppendLine("      \"attributes\": [\"distinguishedName\", \"displayName\"]");
promptBuilder.AppendLine("    },");
promptBuilder.AppendLine("    {");
promptBuilder.AppendLine("      \"step\": 2,");
promptBuilder.AppendLine("      \"name\": \"all_reports\",");
promptBuilder.AppendLine("      \"operation\": \"expand_reports\",");
promptBuilder.AppendLine("      \"source\": \"find_jane\",");
promptBuilder.AppendLine("      \"target_type\": \"User\",");
promptBuilder.AppendLine("      // OMIT max_depth - defaults to 10 (sufficient for most teams)");
promptBuilder.AppendLine("      // OMIT max_nodes - defaults to 10000 (sufficient for most teams)");
promptBuilder.AppendLine("      \"attributes\": [\"distinguishedName\", \"displayName\", \"manager\", \"mail\", \"title\"]");
promptBuilder.AppendLine("    }");
promptBuilder.AppendLine("  ],");
promptBuilder.AppendLine("  \"projection\": {");
promptBuilder.AppendLine("    \"row_step\": \"all_reports\",");
promptBuilder.AppendLine("    \"columns\": [");
promptBuilder.AppendLine("      { \"name\": \"Name\", \"attribute\": \"displayName\" },");
promptBuilder.AppendLine("      { \"name\": \"Title\", \"attribute\": \"title\" },");
promptBuilder.AppendLine("      { \"name\": \"Email\", \"attribute\": \"mail\" }");
promptBuilder.AppendLine("    ]");
promptBuilder.AppendLine("  }");
promptBuilder.AppendLine("}");
promptBuilder.AppendLine("```");
promptBuilder.AppendLine();

// Example 2: Recursive with aggregation
promptBuilder.AppendLine("EXAMPLE 2b - Query: \"Count employees by type under Jane Doe\"");
promptBuilder.AppendLine("```json");
promptBuilder.AppendLine("{");
promptBuilder.AppendLine("  \"description\": \"Employee count by type under Jane Doe\",");
promptBuilder.AppendLine("  \"schema_version\": 2,");
promptBuilder.AppendLine("  \"steps\": [");
promptBuilder.AppendLine("    {");
promptBuilder.AppendLine("      \"step\": 1,");
promptBuilder.AppendLine("      \"name\": \"find_jane\",");
promptBuilder.AppendLine("      \"operation\": \"search\",");
promptBuilder.AppendLine("      \"target_type\": \"User\",");
promptBuilder.AppendLine("      \"filters\": [{ \"attribute\": \"displayName\", \"operator\": \"equals\", \"value\": \"Doe, Jane\" }],");
promptBuilder.AppendLine("      \"attributes\": [\"distinguishedName\", \"displayName\"]");
promptBuilder.AppendLine("    },");
promptBuilder.AppendLine("    {");
promptBuilder.AppendLine("      \"step\": 2,");
promptBuilder.AppendLine("      \"name\": \"all_reports\",");
promptBuilder.AppendLine("      \"operation\": \"expand_reports\",");
promptBuilder.AppendLine("      \"source\": \"find_jane\",");
promptBuilder.AppendLine("      \"target_type\": \"User\",");
promptBuilder.AppendLine("      \"max_depth\": 50,");
promptBuilder.AppendLine("      \"attributes\": [\"distinguishedName\", \"displayName\", \"manager\", \"employeeType\"]");
promptBuilder.AppendLine("    }");
promptBuilder.AppendLine("  ],");
promptBuilder.AppendLine("  \"projection\": {");
promptBuilder.AppendLine("    \"row_step\": \"all_reports\",");
promptBuilder.AppendLine("    \"columns\": [");
promptBuilder.AppendLine("      { \"name\": \"Name\", \"attribute\": \"displayName\" },");
promptBuilder.AppendLine("      { \"name\": \"Type\", \"attribute\": \"employeeType\" }");
promptBuilder.AppendLine("    ],");
promptBuilder.AppendLine("    \"aggregation\": {");
promptBuilder.AppendLine("      \"group_by\": [\"employeeType\"],");
promptBuilder.AppendLine("      \"count\": true,");
promptBuilder.AppendLine("      \"include_level_metadata\": true");
promptBuilder.AppendLine("    }");
promptBuilder.AppendLine("  }");
promptBuilder.AppendLine("}");
promptBuilder.AppendLine("```");
promptBuilder.AppendLine();

// Example 3: Entire org query (explicit high limits)
promptBuilder.AppendLine("EXAMPLE 2c - Query: \"Show everyone under the CEO\"");
promptBuilder.AppendLine("NOTE: User says 'entire org' or 'everyone under CEO' - SET EXPLICIT HIGH LIMITS");
promptBuilder.AppendLine("```json");
promptBuilder.AppendLine("{");
promptBuilder.AppendLine("  \"description\": \"Complete organizational hierarchy under CEO\",");
promptBuilder.AppendLine("  \"schema_version\": 2,");
promptBuilder.AppendLine("  \"steps\": [");
promptBuilder.AppendLine("    {");
promptBuilder.AppendLine("      \"step\": 1,");
promptBuilder.AppendLine("      \"name\": \"find_ceo\",");
promptBuilder.AppendLine("      \"operation\": \"search\",");
promptBuilder.AppendLine("      \"target_type\": \"User\",");
promptBuilder.AppendLine("      \"filters\": [{ \"attribute\": \"title\", \"operator\": \"contains\", \"value\": \"Chief Executive\" }],");
promptBuilder.AppendLine("      \"attributes\": [\"distinguishedName\", \"displayName\"]");
promptBuilder.AppendLine("    },");
promptBuilder.AppendLine("    {");
promptBuilder.AppendLine("      \"step\": 2,");
promptBuilder.AppendLine("      \"name\": \"entire_org\",");
promptBuilder.AppendLine("      \"operation\": \"expand_reports\",");
promptBuilder.AppendLine("      \"source\": \"find_ceo\",");
promptBuilder.AppendLine("      \"target_type\": \"User\",");
promptBuilder.AppendLine("      \"max_depth\": 50,     // EXPLICIT: entire org needs high depth");
promptBuilder.AppendLine("      \"max_nodes\": 50000, // EXPLICIT: org has ~40K users");
promptBuilder.AppendLine("      \"attributes\": [\"distinguishedName\", \"displayName\", \"manager\", \"employeeType\", \"department\"]");
promptBuilder.AppendLine("    }");
promptBuilder.AppendLine("  ],");
promptBuilder.AppendLine("  \"projection\": {");
promptBuilder.AppendLine("    \"row_step\": \"entire_org\",");
promptBuilder.AppendLine("    \"columns\": [");
promptBuilder.AppendLine("      { \"name\": \"Name\", \"attribute\": \"displayName\" },");
promptBuilder.AppendLine("      { \"name\": \"Department\", \"attribute\": \"department\" },");
promptBuilder.AppendLine("      { \"name\": \"Type\", \"attribute\": \"employeeType\" }");
promptBuilder.AppendLine("    ],");
promptBuilder.AppendLine("    \"aggregation\": {");
promptBuilder.AppendLine("      \"group_by\": [\"department\", \"employeeType\"],");
promptBuilder.AppendLine("      \"count\": true,");
promptBuilder.AppendLine("      \"include_level_metadata\": true");
promptBuilder.AppendLine("    }");
promptBuilder.AppendLine("  }");
promptBuilder.AppendLine("}");
promptBuilder.AppendLine("```");
promptBuilder.AppendLine();

// Example 4: Counter-example (what NOT to do)
promptBuilder.AppendLine("COUNTER-EXAMPLE - Query: \"Show Jane Doe's direct reports\"");
promptBuilder.AppendLine("WRONG (don't use expand_reports for single level):");
promptBuilder.AppendLine("  { \"operation\": \"expand_reports\", \"max_depth\": 1, ... }");
promptBuilder.AppendLine();
promptBuilder.AppendLine("CORRECT (use regular search):");
promptBuilder.AppendLine("```json");
promptBuilder.AppendLine("{");
promptBuilder.AppendLine("  \"steps\": [");
promptBuilder.AppendLine("    { \"step\": 1, \"name\": \"find_jane\", \"operation\": \"search\", ... },");
promptBuilder.AppendLine("    {");
promptBuilder.AppendLine("      \"step\": 2,");
promptBuilder.AppendLine("      \"name\": \"direct_reports\",");
promptBuilder.AppendLine("      \"operation\": \"search\",");
promptBuilder.AppendLine("      \"target_type\": \"User\",");
promptBuilder.AppendLine("      \"filters\": [{ \"attribute\": \"manager\", \"operator\": \"equals\", \"value\": \"{{find_jane.distinguishedName}}\" }],");
promptBuilder.AppendLine("      \"attributes\": [\"displayName\", \"mail\", \"title\"]");
promptBuilder.AppendLine("    }");
promptBuilder.AppendLine("  ]");
promptBuilder.AppendLine("}");
promptBuilder.AppendLine("```");
promptBuilder.AppendLine("=== END EXAMPLES ===");
```

**Acceptance Criteria**:
Test these queries manually against ClaudeService and verify the generated JSON plan:

1. **Query**: "show everyone under Jane Doe" (typical team)
   - **Expected**: Plan contains `expand_reports` WITHOUT explicit max_depth/max_nodes
   - **Effect**: Uses defaults (depth=10, nodes=10000)

2. **Query**: "show Jane Doe's direct reports" (single level)
   - **Expected**: Plan uses `search` operation with manager filter (NOT expand_reports)

3. **Query**: "count employees by type under IT department" (with aggregation)
   - **Expected**: Plan contains `aggregation` block with `group_by: ["employeeType"]` and `count: true`

4. **Query**: "show the entire org under the CEO" (explicit full org)
   - **Expected**: Plan contains `expand_reports` WITH explicit `max_depth: 50, max_nodes: 50000`
   - **Effect**: Can traverse full 40K org
   - **Claude recognizes**: "entire org", "everyone under CEO" triggers high limits

5. **Query**: "how many people work for John Smith"
   - **Expected**: Plan contains both `expand_reports` AND `aggregation` (count intent detected)
   - **Limits**: Uses defaults unless query implies large scope

---

### Phase 7: Frontend Updates
**Duration**: 2 days
**Files**: `csharp/wwwroot/js/app.js`, `csharp/wwwroot/css/styles.css`, `csharp/wwwroot/index.html`

#### 7.1 Update QueryExecuteResponse Model

```csharp
// In QueryController.cs
public class QueryExecuteResponse
{
    // Existing fields
    public List<Dictionary<string, string>> PreviewRows { get; set; }
    public int TotalRows { get; set; }
    public string RequestId { get; set; }

    // NEW
    public Dictionary<string, object>? Aggregation { get; set; }
    public List<string>? Warnings { get; set; }
}
```

#### 7.2 Add Warning Display (JavaScript)

```javascript
// app.js
function displayWarnings(warnings) {
    const container = document.getElementById('warnings');
    if (!warnings || warnings.length === 0) {
        container.style.display = 'none';
        return;
    }

    container.style.display = 'block';
    container.innerHTML = warnings
        .map(w => `<div class="warning-item">⚠️ ${escapeHtml(w)}</div>`)
        .join('');
}
```

#### 7.3 Add Aggregation Display (JavaScript)

```javascript
// app.js
function displayAggregation(aggregation) {
    const container = document.getElementById('aggregation-summary');
    if (!aggregation || !aggregation.grouped_counts) {
        container.style.display = 'none';
        return;
    }

    const counts = aggregation.grouped_counts;
    const total = Object.values(counts).reduce((a, b) => a + b, 0);

    let html = '<h3>Summary</h3><table class="agg-table">';
    html += '<tr><th>Category</th><th>Count</th><th>%</th></tr>';

    for (const [key, count] of Object.entries(counts)) {
        const pct = ((count / total) * 100).toFixed(1);
        html += `<tr><td>${escapeHtml(key)}</td><td>${count}</td><td>${pct}%</td></tr>`;
    }

    html += `<tr class="total"><td><strong>Total</strong></td><td><strong>${total}</strong></td><td><strong>100%</strong></td></tr>`;
    html += '</table>';

    // Level metadata if present
    if (aggregation.level_metadata) {
        html += '<h4>By Level</h4><table class="level-table">';
        html += '<tr><th>Level</th><th>Count</th></tr>';
        for (const [level, count] of Object.entries(aggregation.level_metadata)) {
            html += `<tr><td>Level ${level}</td><td>${count}</td></tr>`;
        }
        html += '</table>';
    }

    container.innerHTML = html;
    container.style.display = 'block';
}
```

#### 7.4 Update HTML Structure

```html
<!-- index.html - add after query input, before results -->
<div id="warnings" style="display:none;"></div>
<div id="aggregation-summary" style="display:none;"></div>
<div id="results"></div>
```

#### 7.5 Update CSS

```css
/* styles.css */
#warnings {
    background: #8b4513;
    border: 1px solid #ff8c00;
    border-radius: 4px;
    padding: 12px;
    margin: 12px 0;
}

.warning-item {
    color: #ffd700;
    margin: 4px 0;
}

#aggregation-summary {
    background: #2b2b3d;
    border: 1px solid #6272a4;
    border-radius: 4px;
    padding: 16px;
    margin: 12px 0;
}

.agg-table, .level-table {
    width: 100%;
    border-collapse: collapse;
    margin-top: 8px;
}

.agg-table th, .level-table th {
    background: #44475a;
    text-align: left;
    padding: 8px;
}

.agg-table td, .level-table td {
    padding: 6px 8px;
    border-bottom: 1px solid #44475a;
}

.agg-table tr.total {
    background: #44475a;
}
```

#### 7.6 Add Progress Indication for Large Queries

```javascript
// app.js - add progress indicator
function showProgress(message) {
    const container = document.getElementById('progress-indicator');
    container.innerHTML = `
        <div class="progress-message">
            <span class="spinner">⏳</span>
            <span>${message}</span>
        </div>
    `;
    container.style.display = 'block';
}

function hideProgress() {
    document.getElementById('progress-indicator').style.display = 'none';
}
```

```css
/* styles.css - add progress indicator styling */
#progress-indicator {
    background: #44475a;
    border: 1px solid #6272a4;
    border-radius: 4px;
    padding: 16px;
    margin: 12px 0;
    text-align: center;
}

.progress-message {
    color: #f8f8f2;
    font-size: 14px;
}

.spinner {
    display: inline-block;
    margin-right: 8px;
    animation: spin 2s linear infinite;
}

@keyframes spin {
    0% { transform: rotate(0deg); }
    100% { transform: rotate(360deg); }
}
```

```html
<!-- index.html - add progress container -->
<div id="progress-indicator" style="display:none;"></div>
<div id="warnings" style="display:none;"></div>
<div id="aggregation-summary" style="display:none;"></div>
```

#### 7.7 Update Execute Handler with Progress

```javascript
// app.js - in executeQuery function
async function executeQuery(query) {
    // Show progress for queries that might take time
    if (query.toLowerCase().includes('everyone') ||
        query.toLowerCase().includes('entire org') ||
        query.toLowerCase().includes('all')) {
        showProgress('Processing large query... this may take a few minutes');
    }

    try {
        const response = await fetch('/api/query/execute', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ query })
        });

        const result = await response.json();

        hideProgress();
        displayWarnings(result.warnings);
        displayAggregation(result.aggregation);
        displayResults(result.previewRows);
    } catch (error) {
        hideProgress();
        showError('Query failed: ' + error.message);
    }
}
```

**Acceptance**:
- Progress indicator shows for queries with keywords: "everyone", "entire org", "all"
- Progress message: "Processing large query... this may take a few minutes"
- Progress hides when results arrive or on error
- Warnings display in banner above results
- Aggregation summary shows counts and percentages
- Level metadata displays when present
- UI gracefully handles missing aggregation/warnings
- Downloads include aggregation in comments

---

### Phase 8: Testing
**Duration**: 2-3 days
**Files**: New test files in `csharp/Tests/`

#### 8.1 Unit Tests

**DirectoryPlanExecutorTests.cs**:
```csharp
[Fact]
public async Task ExpandReports_SingleLevel_ReturnsDirectReports()
{
    var mockAD = CreateMockADService();
    mockAD.Setup(m => m.GetDirectReportsBatch(
        It.IsAny<List<string>>(),
        It.IsAny<List<string>>(),
        It.IsAny<CancellationToken>()))
        .ReturnsAsync(CreateMockUsers(5));

    var executor = new DirectoryPlanExecutor(mockAD.Object, ...);
    var plan = CreateExpandReportsPlan(maxDepth: 1);

    var result = await executor.ExecuteAsync(plan);

    Assert.Equal(5, result.Rows.Count);
    Assert.NotNull(result.LevelMetadata);
}

[Fact]
public async Task ExpandReports_WithCycle_DetectsCycle()
{
    // A→B, B→C, C→A (cycle)
    var mockAD = CreateMockADWithCycle();
    var executor = new DirectoryPlanExecutor(mockAD.Object, ...);
    var plan = CreateExpandReportsPlan(maxDepth: 10);

    var result = await executor.ExecuteAsync(plan);

    // Should complete without hanging
    Assert.True(result.Rows.Count > 0);
}

[Fact]
public async Task ExpandReports_ExceedsNodeLimit_TruncatesAndWarns()
{
    var mockAD = CreateMockADWithLargeOrg(nodes: 10000);
    var executor = new DirectoryPlanExecutor(mockAD.Object, ...);
    var plan = CreateExpandReportsPlan(maxDepth: 10, maxNodes: 500);

    var result = await executor.ExecuteAsync(plan);

    Assert.Equal(500, result.Rows.Count);
    Assert.Contains(result.Warnings, w => w.Contains("500 nodes"));
}
```

**PlanValidatorTests.cs**:
```csharp
[Fact]
public void ExpandReports_WithoutMaxDepth_Fails()
{
    var plan = CreatePlan(new DirectoryPlanStep
    {
        Operation = "expand_reports",
        MaxDepth = null
    });

    var result = _validator.Validate(plan);

    Assert.False(result.IsValid);
    Assert.Contains("requires max_depth", result.SecurityErrors);
}

[Fact]
public void ExpandReports_WithGroupTargetType_Fails()
{
    var plan = CreatePlan(new DirectoryPlanStep
    {
        Operation = "expand_reports",
        MaxDepth = 5,
        TargetType = DirectoryObjectType.Group
    });

    var result = _validator.Validate(plan);

    Assert.False(result.IsValid);
    Assert.Contains("only supports target_type 'User'", result.SecurityErrors);
}
```

**AggregationTests.cs**:
```csharp
[Fact]
public void Aggregation_ByEmployeeType_ReturnsCorrectCounts()
{
    var records = CreateMockRecords(new[]
    {
        ("FTE", 10),
        ("Contractor", 5),
        ("Intern", 2)
    });

    var projection = new ProjectionDefinition
    {
        Aggregation = new AggregationDefinition
        {
            GroupBy = new List<string> { "employeeType" },
            Count = true
        }
    };

    var result = ApplyAggregation(records, projection);

    Assert.Equal(10, result["FTE"]);
    Assert.Equal(5, result["Contractor"]);
    Assert.Equal(2, result["Intern"]);
}
```

#### 8.2 Integration Tests

Create test org structure in mock or test OU:
```
Manager (Level 0)
├─ Report1 (Level 1)
│  ├─ Report1.1 (Level 2)
│  └─ Report1.2 (Level 2)
├─ Report2 (Level 1)
│  └─ Report2.1 (Level 2)
│     └─ Report2.1.1 (Level 3)
└─ Report3 (Level 1)
```

Test cases:
- max_depth=1 → returns 3 records (Report1, Report2, Report3)
- max_depth=2 → returns 6 records (adds Report1.1, Report1.2, Report2.1)
- max_depth=3 → returns 7 records (adds Report2.1.1)
- Aggregation by employeeType → correct counts
- Truncation warnings appear when limits hit

#### 8.3 Performance Benchmarks

Create performance test with realistic org structure:
```csharp
[Theory]
[InlineData(3, 100, 2000)] // depth 3, 100 total nodes, < 2s
[InlineData(5, 500, 5000)] // depth 5, 500 total nodes, < 5s
public async Task PerformanceTest(int depth, int nodeCount, int maxMs)
{
    var testOrg = CreateTestOrg(depth, nodeCount);
    var plan = CreateExpandReportsPlan(maxDepth: depth);

    var sw = Stopwatch.StartNew();
    var result = await _executor.ExecuteAsync(plan);
    sw.Stop();

    Assert.True(sw.ElapsedMilliseconds < maxMs,
        $"Took {sw.ElapsedMilliseconds}ms, expected <{maxMs}ms");
}
```

#### 8.4 Stress Testing (REQUIRED before production)

**⚠️ CRITICAL**: Must complete stress testing with full-scale org before production deployment.

Create realistic test environment:
```csharp
[Fact]
public async Task StressTest_FullOrg_40KNodes()
{
    // Use real AD test OU or mock with realistic structure
    // Depth: ~7-10 levels
    // Width: Varied (CEO→10 VPs→100 Directors→1000 Managers→38K ICs)
    var plan = CreateExpandReportsPlan(maxDepth: 50, maxNodes: 50000);

    var sw = Stopwatch.StartNew();
    var result = await _executor.ExecuteAsync(plan);
    sw.Stop();

    // Assertions
    Assert.True(result.Rows.Count <= 50000, "Exceeded max nodes");
    Assert.True(sw.Elapsed.TotalMinutes < 10, $"Took {sw.Elapsed.TotalMinutes} minutes");

    _logger.LogInformation(
        "Full org query: {Nodes} nodes in {Duration}s, {MemoryMB}MB peak",
        result.Rows.Count,
        sw.Elapsed.TotalSeconds,
        GC.GetTotalMemory(false) / 1024 / 1024
    );
}

[Fact]
public async Task StressTest_ConcurrentQueries()
{
    // Simulate 5 users running large queries simultaneously
    var tasks = Enumerable.Range(0, 5)
        .Select(_ => ExecuteLargeQuery(10000))
        .ToArray();

    await Task.WhenAll(tasks);

    // Verify no timeouts, no OOM, acceptable performance degradation
}
```

**Stress Test Acceptance Criteria**:
- [ ] 40K node query completes in < 5 minutes
- [ ] Memory usage stays under 4GB for single query
- [ ] 5 concurrent 10K queries complete without errors
- [ ] LDAP server load acceptable (monitor AD performance counters)
- [ ] IIS doesn't timeout (verify timeout settings)
- [ ] Cancellation tokens work (can kill long-running query)

**If stress tests fail**:
1. Reduce default max_nodes to 5K
2. Document current limits clearly to users
3. Plan migration to async job pattern

**Acceptance**:
- All unit tests pass
- Integration tests with mock data pass
- Performance benchmarks meet targets
- **Stress tests with 40K nodes pass (CRITICAL)**
- No memory leaks detected
- Test coverage >85%

---

## Deployment Checklist

### Pre-Deployment
- [ ] All tests passing
- [ ] Code review completed
- [ ] Documentation updated (README, IMPLEMENTATION_SUMMARY)
- [ ] Configuration reviewed (limits appropriate?)
- [ ] Backward compatibility verified (v1 plans still work)

### Deployment
```powershell
cd D:\source\adquery\csharp
dotnet test --configuration Release
.\deploy.ps1 -Force
```

### Post-Deployment
- [ ] Health check returns 200 OK
- [ ] Test query: "show everyone under [test person]" completes
- [ ] Aggregation displays in UI
- [ ] Warnings display correctly
- [ ] CSV export includes aggregation
- [ ] Monitor logs for errors (first hour)

### Rollback Plan
If issues arise:
```powershell
Stop-WebAppPool adquery_pool
Remove-Item D:\inetpub\adquery -Recurse -Force
Copy-Item D:\inetpub\adquery.backup D:\inetpub\adquery -Recurse
Start-WebAppPool adquery_pool
```

---

## Timeline Summary

| Phase | Duration | Dependencies |
|-------|----------|--------------|
| 1. Schema Updates | 1-2 days | None |
| 2. Executor Traversal | 3-4 days | Phase 1 |
| 3. AD Batching | 1-2 days | None (parallel with 2) |
| 4. Aggregation | 1-2 days | Phase 2 |
| 5. Validator | 1-2 days | Phase 1 |
| 6. Claude Prompt | 1 day | Phase 1 |
| 7. Frontend | 2 days | Phase 4 |
| 8. Testing | 2-3 days | All phases |

**Total**: ~12-18 days with 1 senior backend engineer

**Critical Path**: Phase 1 → 2 → 4 → 7 → 8
**Parallel Work**: Phases 3, 5, 6 can overlap with Phase 2

---

## Success Criteria

- [ ] Users can query "everyone under [person]" with conservative 10K default (Claude raises explicitly for full org)
- [ ] When Claude sets max_nodes=50000, full 40K org query completes successfully
- [ ] Depth limit (100 max) acts as safety valve; queries naturally stop at tree end
- [ ] Conservative defaults (depth=10, nodes=10K) handle most queries without hitting limits
- [ ] Aggregation by employeeType works correctly (tested up to 40K nodes)
- [ ] Warnings display when limits hit (with clear explanation)
- [ ] Performance: 10K queries in <30s, 40K queries in <5 minutes (stress-tested)
- [ ] UI shows "Processing... may take a few minutes" for large queries
- [ ] Backward compatibility: existing queries unchanged
- [ ] Stress tests pass: 40K nodes completes, 5 concurrent 10K queries succeed
- [ ] Zero production errors in first 48 hours

---

## Key Design Decisions

1. **Why new operation type?**: Clean separation of concerns, explicit recursion intent in plan
2. **Why breadth-first?**: Enables level-by-level batching, better LDAP performance
3. **Why OR-filter batching?**: Reduces O(n) queries to O(depth) queries
4. **Why visited tracking?**: Prevents cycles without relying on AD data integrity
5. **Why warnings not errors?**: Partial results are useful, truncation is expected
6. **Why both rows + aggregation?**: Users want summaries AND details for drilling in
7. **Why schema_version=2?**: Backward compatibility, gradual migration path
