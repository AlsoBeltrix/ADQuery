# Recursive Org Summary - Implementation Workflow

## Overview

Add `expand_reports` operation to enable recursive organizational hierarchy traversal with aggregation. When users ask "show everyone under [person]", the tool will traverse the complete reporting structure and optionally return aggregated summaries.

**Key Principle**: Claude generates a single step with recursion metadata → Executor performs breadth-first traversal with batched LDAP queries.

---

## Implementation Phases

### Phase 1: Schema & Model Updates
**Duration**: 1-2 days
**Files**: `csharp/Models/DirectoryQueryPlan.cs`

#### 1.1 Add New Fields to DirectoryPlanStep
```csharp
/// <summary>
/// Maximum recursion depth (1-10). Required for expand_reports operation.
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
    var maxDepth = step.MaxDepth ?? 5;
    var maxNodes = step.MaxNodes ?? 5000;
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

### Phase 5: Validator Updates
**Duration**: 1-2 days
**Files**: `csharp/Security/PlanValidator.cs`, `appsettings.json`

#### 5.1 Add Configuration Limits

```json
// appsettings.json → Security section
"MaxRecursionDepth": 10,
"MaxNodesPerRecursion": 5000
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

#### 5.3 Add Validation for expand_reports

```csharp
private PlanSecurityResult ValidateExpandReports(DirectoryPlanStep step)
{
    var errors = new List<string>();

    // Require max_depth
    if (!step.MaxDepth.HasValue || step.MaxDepth.Value < 1)
    {
        errors.Add($"Step '{step.Name}': expand_reports requires max_depth >= 1");
    }

    // Enforce max_depth limit
    if (step.MaxDepth > _config.MaxRecursionDepth)
    {
        errors.Add($"Step '{step.Name}': max_depth {step.MaxDepth} exceeds limit of {_config.MaxRecursionDepth}");
    }

    // Enforce max_nodes limit if specified
    if (step.MaxNodes.HasValue && step.MaxNodes.Value > _config.MaxNodesPerRecursion)
    {
        errors.Add($"Step '{step.Name}': max_nodes {step.MaxNodes} exceeds limit of {_config.MaxNodesPerRecursion}");
    }

    // Require source step
    if (string.IsNullOrEmpty(step.Source))
    {
        errors.Add($"Step '{step.Name}': expand_reports requires source step");
    }

    // Only allow User target_type for org recursion
    if (step.TargetType != DirectoryObjectType.User)
    {
        errors.Add($"Step '{step.Name}': expand_reports only supports target_type 'User'");
    }

    return new PlanSecurityResult
    {
        IsValid = !errors.Any(),
        SecurityErrors = errors
    };
}
```

#### 5.4 Validate Aggregation Fields

```csharp
private PlanSecurityResult ValidateAggregation(ProjectionDefinition projection)
{
    if (projection.Aggregation == null) return PlanSecurityResult.Success();

    var errors = new List<string>();

    // Validate group_by fields are allowed
    foreach (var field in projection.Aggregation.GroupBy)
    {
        if (!IsAttributeAllowed(field, DirectoryObjectType.User))
        {
            errors.Add($"Aggregation field '{field}' is not allowed");
        }
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
- max_depth > 10 → validation error
- expand_reports with target_type=Group → validation error
- Aggregation with disallowed field → validation error
- Valid recursive plan → passes validation

---

### Phase 6: Claude Prompt Updates
**Duration**: 1 day
**Files**: `csharp/Services/ClaudeService.cs`

#### 6.1 Update BuildExecutionPlanPrompt

Add to the PLAN REQUIREMENTS section (around line 218):

```csharp
promptBuilder.AppendLine("- Supported operations: search, expand_members, lookup, expand_reports.");
promptBuilder.AppendLine("- expand_reports: recursively traverse organizational hierarchy (manager→reports direction). Requires: source (step with manager records), max_depth (1-10), attributes. Optional: max_nodes (default 5000).");
promptBuilder.AppendLine("- When users ask for 'everyone under [person]' or 'all reports', use expand_reports instead of chaining multiple search steps.");
promptBuilder.AppendLine("- Set reasonable max_depth: use 3-5 for typical queries, 10 only when explicitly requested.");
promptBuilder.AppendLine("- For count/summary queries, add aggregation to projection with group_by fields (e.g., employeeType) and count: true.");
```

#### 6.2 Add expand_reports Example

Add after the existing example (around line 289):

```csharp
promptBuilder.AppendLine();
promptBuilder.AppendLine("EXAMPLE 2 - Recursive org query with aggregation:");
promptBuilder.AppendLine("```json");
promptBuilder.AppendLine("{");
promptBuilder.AppendLine(" \"description\": \"Count all employees under Jane Doe by type\",");
promptBuilder.AppendLine(" \"schema_version\": 2,");
promptBuilder.AppendLine(" \"steps\": [");
promptBuilder.AppendLine("  {");
promptBuilder.AppendLine("   \"step\": 1,");
promptBuilder.AppendLine("   \"name\": \"find_jane\",");
promptBuilder.AppendLine("   \"operation\": \"search\",");
promptBuilder.AppendLine("   \"target_type\": \"User\",");
promptBuilder.AppendLine("   \"filters\": [");
promptBuilder.AppendLine("    { \"attribute\": \"displayName\", \"operator\": \"equals\", \"value\": \"Doe, Jane\" }");
promptBuilder.AppendLine("   ],");
promptBuilder.AppendLine("   \"attributes\": [\"distinguishedName\", \"displayName\"]");
promptBuilder.AppendLine("  },");
promptBuilder.AppendLine("  {");
promptBuilder.AppendLine("   \"step\": 2,");
promptBuilder.AppendLine("   \"name\": \"all_reports\",");
promptBuilder.AppendLine("   \"operation\": \"expand_reports\",");
promptBuilder.AppendLine("   \"source\": \"find_jane\",");
promptBuilder.AppendLine("   \"target_type\": \"User\",");
promptBuilder.AppendLine("   \"max_depth\": 5,");
promptBuilder.AppendLine("   \"attributes\": [\"distinguishedName\", \"displayName\", \"manager\", \"employeeType\"]");
promptBuilder.AppendLine("  }");
promptBuilder.AppendLine(" ],");
promptBuilder.AppendLine(" \"projection\": {");
promptBuilder.AppendLine("  \"row_step\": \"all_reports\",");
promptBuilder.AppendLine("  \"columns\": [");
promptBuilder.AppendLine("   { \"name\": \"Employee\", \"attribute\": \"displayName\" },");
promptBuilder.AppendLine("   { \"name\": \"Type\", \"attribute\": \"employeeType\" }");
promptBuilder.AppendLine("  ],");
promptBuilder.AppendLine("  \"aggregation\": {");
promptBuilder.AppendLine("   \"group_by\": [\"employeeType\"],");
promptBuilder.AppendLine("   \"count\": true,");
promptBuilder.AppendLine("   \"include_level_metadata\": true");
promptBuilder.AppendLine("  }");
promptBuilder.AppendLine(" }");
promptBuilder.AppendLine("}");
promptBuilder.AppendLine("```");
```

**Acceptance**:
- Test prompt with "show everyone under Jane Doe" → generates expand_reports
- Test prompt with "show Jane Doe's direct reports" → generates search (1 level)
- Test prompt with "count employees by type under IT" → includes aggregation
- Claude sets reasonable max_depth (3-5 by default)

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

#### 7.6 Update Execute Handler

```javascript
// app.js - in executeQuery function
async function executeQuery(query) {
    // ... existing code ...

    const response = await fetch('/api/query/execute', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ query })
    });

    const result = await response.json();

    displayWarnings(result.warnings);
    displayAggregation(result.aggregation);
    displayResults(result.previewRows);
}
```

**Acceptance**:
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

**Acceptance**:
- All unit tests pass
- Integration tests with mock data pass
- Performance benchmarks meet targets
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

- [ ] Users can query "everyone under [person]" and get complete org tree
- [ ] Depth limit (10) prevents runaway recursion
- [ ] Node limit (5000) prevents memory issues
- [ ] Aggregation by employeeType works correctly
- [ ] Warnings display when limits are hit
- [ ] Performance: 200 nodes in <3 seconds
- [ ] Backward compatibility: existing queries unchanged
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
