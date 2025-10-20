using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AdQuery.Orchestrator.Models;
using AdQuery.Orchestrator.Security;
using Microsoft.Extensions.Logging;

namespace AdQuery.Orchestrator.Services;

/// <summary>
/// Executes directory query plans entirely in managed code.
/// </summary>
public class DirectoryPlanExecutor : IDirectoryPlanExecutor
{
    private readonly ILogger<DirectoryPlanExecutor> _logger;
    private readonly IPlanValidator _planValidator;
    private readonly IActiveDirectoryService _directoryService;

    public DirectoryPlanExecutor(
        ILogger<DirectoryPlanExecutor> logger,
        IPlanValidator planValidator,
        IActiveDirectoryService directoryService)
    {
        _logger = logger;
        _planValidator = planValidator;
        _directoryService = directoryService;
    }

    public Task<PlanExecutionResult> ExecutePlanAsync(DirectoryQueryPlan plan, CancellationToken cancellationToken = default)
    {
        // Use null progress for backward compatibility
        return ExecutePlanAsync(plan, new NullProgress(), cancellationToken);
    }

    public async Task<PlanExecutionResult> ExecutePlanAsync(DirectoryQueryPlan plan, IProgress<PlanProgressUpdate> progress, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new PlanExecutionResult();

        try
        {
            var validation = await ValidatePlanAsync(plan, cancellationToken);
            if (!validation.IsValid)
            {
                result.Success = false;
                result.Errors.AddRange(validation.Errors);
                result.Warnings.AddRange(validation.Warnings);
                return result;
            }

            var runtime = new DirectoryPlanRuntime(_directoryService, _logger, progress);
            var execution = await runtime.ExecuteAsync(plan, cancellationToken);

            result.Success = execution.Success;
            result.Errors.AddRange(execution.Errors);
            result.Warnings.AddRange(execution.Warnings);
            result.Data = execution.Data;
            result.StepsExecuted = execution.StepsExecuted;
            result.StepsSkipped = execution.StepsSkipped;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("Directory plan execution cancelled.");
            result.Success = false;
            result.Errors.Add("Query execution cancelled or timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error executing directory plan.");
            result.Success = false;
            result.Errors.Add("An unexpected error occurred while executing the directory plan.");
        }
        finally
        {
            stopwatch.Stop();
            result.ExecutionTimeMs = stopwatch.ElapsedMilliseconds;
        }

        return result;
    }

    private class NullProgress : IProgress<PlanProgressUpdate>
    {
        public void Report(PlanProgressUpdate value) { }
    }

    public async Task<PlanValidationResult> ValidatePlanAsync(DirectoryQueryPlan plan, CancellationToken cancellationToken = default)
    {
        var result = new PlanValidationResult();

        if (string.IsNullOrWhiteSpace(plan.Description))
        {
            result.Errors.Add("Plan description is required.");
        }

        if (!plan.Steps.Any())
        {
            result.Errors.Add("Plan must contain at least one step.");
        }

        if (string.IsNullOrWhiteSpace(plan.Projection.RowStep))
        {
            result.Errors.Add("Projection must specify a row_step.");
        }

        if (!ValidateStepNumbers(plan, result.Errors))
        {
            result.Errors.Add("Step numbers must be sequential starting at 1.");
        }

        foreach (var step in plan.Steps)
        {
            if (step.SizeLimit.HasValue && step.SizeLimit.Value <= 0)
            {
                result.Errors.Add($"Step {step.Step} size_limit must be greater than zero when provided.");
            }
        }

        if (plan.ResultLimit.HasValue && plan.ResultLimit.Value <= 0)
        {
            result.Errors.Add("result_limit must be greater than zero when provided.");
        }

        var security = await _planValidator.ValidateSecurityAsync(plan);
        result.Security = security;
        result.Errors.AddRange(security.SecurityErrors);

        if (!_planValidator.ValidateComplexity(plan))
        {
            security.ComplexityValid = false;
            result.Errors.Add("Plan exceeds complexity limits.");
        }

        result.IsValid = !result.Errors.Any();
        return result;
    }

    private static bool ValidateStepNumbers(DirectoryQueryPlan plan, List<string> errors)
    {
        var expected = 1;
        foreach (var step in plan.Steps.OrderBy(s => s.Step))
        {
            if (step.Step != expected)
            {
                errors.Add($"Step numbering must be sequential. Expected {expected}, found {step.Step}.");
                return false;
            }

            expected++;
        }

        return true;
    }
}

/// <summary>
/// Runtime helper that executes plan steps and assembles results.
/// </summary>
internal sealed class DirectoryPlanRuntime
{
    private readonly IActiveDirectoryService _directoryService;
    private readonly ILogger _logger;
    private readonly IProgress<PlanProgressUpdate>? _progress;

    private readonly Dictionary<string, StepRuntimeState> _stepStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();

    public DirectoryPlanRuntime(IActiveDirectoryService directoryService, ILogger logger, IProgress<PlanProgressUpdate>? progress = null)
    {
        _directoryService = directoryService;
        _logger = logger;
        _progress = progress;
    }

    public async Task<RuntimeResult> ExecuteAsync(DirectoryQueryPlan plan, CancellationToken cancellationToken)
    {
        var result = new RuntimeResult();

        foreach (var step in plan.Steps.OrderBy(s => s.Step))
        {
            cancellationToken.ThrowIfCancellationRequested();

            _logger.LogInformation("Executing directory plan step {Step}:{Name}", step.Step, step.Name);

            var records = await ExecuteStepAsync(step, cancellationToken);
            if (records is null)
            {
                result.StepsSkipped++;
                continue;
            }

            _stepStates[step.Name] = new StepRuntimeState(step, records);
            result.StepsExecuted++;

            if (records.Count == 0)
            {
                _warnings.Add($"Step {step.Step} returned no records.");

                var hasDependents = plan.Steps.Any(s =>
                    !string.IsNullOrWhiteSpace(s.Source) &&
                    s.Source.Equals(step.Name, StringComparison.OrdinalIgnoreCase));

                if (step.TargetType == DirectoryObjectType.Group &&
                    step.Operation.Equals("search", StringComparison.OrdinalIgnoreCase) &&
                    hasDependents)
                {
                    _warnings.Add($"Halting execution because step {step.Step} ('{step.Name}') produced no groups for downstream steps.");
                    break;
                }
            }
        }

        result.Data = Project(plan.Projection);

        // Compute aggregation if requested
        if (plan.Projection?.Aggregation != null && result.Data.Any())
        {
            _progress?.Report(new PlanProgressUpdate
            {
                NodesProcessed = result.Data.Count,
                CurrentDepth = 0,
                Phase = "aggregation"
            });

            result.Aggregation = ComputeAggregation(result.Data, plan.Projection.Aggregation);
        }

        if (plan.ResultLimit.HasValue && plan.ResultLimit.Value > 0 && result.Data.Count > plan.ResultLimit.Value)
        {
            result.Data = result.Data.Take(plan.ResultLimit.Value).ToList();
            _warnings.Add($"Result set truncated to {plan.ResultLimit.Value} rows.");
        }
        result.Errors.AddRange(_errors);
        result.Warnings.AddRange(_warnings);
        result.Success = !result.Errors.Any();

        return result;
    }

    private async Task<IReadOnlyList<DirectoryRecord>?> ExecuteStepAsync(DirectoryPlanStep step, CancellationToken cancellationToken)
    {
        return step.Operation.ToLowerInvariant() switch
        {
            "search" => await ExecuteSearchStep(step, cancellationToken),
            "expand_members" => await ExecuteExpandMembersStep(step, cancellationToken),
            "lookup" => await ExecuteLookupStep(step, cancellationToken),
            "expand_reports" => await ExecuteExpandReportsStep(step, cancellationToken),
            _ => throw new InvalidOperationException($"Unsupported operation '{step.Operation}'.")
        };
    }

    private async Task<IReadOnlyList<DirectoryRecord>> ExecuteSearchStep(DirectoryPlanStep step, CancellationToken cancellationToken)
    {
        if (!TryNormalizeFilters(step, out var normalizedFilters))
        {
            _warnings.Add($"Step {step.Step} skipped because a filter value was missing.");
            return Array.Empty<DirectoryRecord>();
        }

        var filtersToUse = normalizedFilters.Count > 0
            ? normalizedFilters
            : step.Filters ?? new List<DirectoryFilter>();

        if (normalizedFilters.Count > 0)
        {
            step.Filters = normalizedFilters;
        }

        if (TryEvaluateTemplateSearch(step, filtersToUse, out var templateRecords))
        {
            return templateRecords;
        }

        if (TryExpandTemplateFilters(step, filtersToUse, out var expandedFilterSets))
        {
            if (expandedFilterSets.Count == 0)
            {
                return Array.Empty<DirectoryRecord>();
            }

            var aggregated = new Dictionary<string, DirectoryRecord>(StringComparer.OrdinalIgnoreCase);
            foreach (var filterSet in expandedFilterSets)
            {
                var expandedRequest = new DirectorySearchRequest
                {
                    TargetType = step.TargetType,
                    Attributes = step.Attributes,
                    Filters = filterSet,
                    SizeLimit = step.SizeLimit
                };

                var expandedResults = await _directoryService.SearchAsync(expandedRequest, cancellationToken);
                foreach (var record in expandedResults)
                {
                    if (!string.IsNullOrWhiteSpace(record.DistinguishedName) &&
                        !aggregated.ContainsKey(record.DistinguishedName))
                    {
                        aggregated[record.DistinguishedName] = record;
                    }
                }
            }

            if (aggregated.Count > 0)
            {
                return aggregated.Values.ToList();
            }
        }

        var request = new DirectorySearchRequest
        {
            TargetType = step.TargetType,
            Attributes = step.Attributes,
            Filters = filtersToUse,
            SizeLimit = step.SizeLimit
        };

        var records = await _directoryService.SearchAsync(request, cancellationToken);

        if (records.Count == 0)
        {
            var fallback = await TryExecutePersonSearchFallbackAsync(step, filtersToUse, cancellationToken);
            if (fallback is { } result && result.Records.Count > 0)
            {
                _warnings.Add($"Step {step.Step} fallback search matched {result.Records.Count} records.");
                step.Filters = result.Filters;
                return result.Records;
            }
        }

        return records;
    }

    private async Task<IReadOnlyList<DirectoryRecord>> ExecuteExpandMembersStep(DirectoryPlanStep step, CancellationToken cancellationToken)
    {
        var source = GetSourceState(step);
        var distinguishedNames = source.Records.Select(r => r.DistinguishedName);
        return await _directoryService.ExpandGroupMembersAsync(distinguishedNames, step.Recursive, step.Attributes, cancellationToken);
    }

    private async Task<IReadOnlyList<DirectoryRecord>> ExecuteLookupStep(DirectoryPlanStep step, CancellationToken cancellationToken)
    {
        var source = GetSourceState(step);
        if (string.IsNullOrWhiteSpace(step.SourceAttribute))
        {
            _errors.Add($"Step {step.Step} requires source_attribute.");
            return Array.Empty<DirectoryRecord>();
        }

        var lookupValues = source.Records
            .SelectMany(r => r.GetStrings(step.SourceAttribute))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (lookupValues.Count == 0)
        {
            return Array.Empty<DirectoryRecord>();
        }

        return await _directoryService.LookupAsync(lookupValues, step.TargetType, step.Attributes, cancellationToken);
    }

    private async Task<IReadOnlyList<DirectoryRecord>> ExecuteExpandReportsStep(DirectoryPlanStep step, CancellationToken cancellationToken)
    {
        var maxDepth = step.MaxDepth ?? 10;
        var maxNodes = step.MaxNodes ?? 10000;
        var visitedDNs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var levelResults = new Dictionary<int, List<DirectoryRecord>>();
        var source = GetSourceState(step);

        // Get seed DNs from source step
        var seedDNs = source.Records
            .Select(r => r.DistinguishedName)
            .Where(dn => !string.IsNullOrWhiteSpace(dn))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!seedDNs.Any())
        {
            _warnings.Add($"Step {step.Step}: No seed records for org expansion");
            return Array.Empty<DirectoryRecord>();
        }

        levelResults[0] = source.Records.ToList();

        // Breadth-first traversal
        var currentLevelDNs = seedDNs;
        for (int depth = 1; depth <= maxDepth; depth++)
        {
            if (!currentLevelDNs.Any()) break;

            cancellationToken.ThrowIfCancellationRequested();

            // Report progress
            var totalProcessed = levelResults.Values.Sum(list => list.Count);
            _progress?.Report(new PlanProgressUpdate
            {
                NodesProcessed = totalProcessed,
                CurrentDepth = depth,
                EstimatedRemainingNodes = EstimateRemainingNodes(totalProcessed, depth),
                Phase = $"enumerating-level-{depth}"
            });

            // Mark current level as visited
            foreach (var dn in currentLevelDNs)
            {
                visitedDNs.Add(dn);
            }

            // Batch query: find all direct reports for this level
            var directReports = await _directoryService.GetDirectReportsBatch(
                currentLevelDNs,
                step.Attributes,
                cancellationToken);

            if (!directReports.Any())
            {
                _logger.LogDebug("Org expansion ended naturally at depth {Depth}", depth);
                break;
            }

            // Check node limit
            var newTotal = totalProcessed + directReports.Count;
            if (newTotal > maxNodes)
            {
                var remaining = maxNodes - totalProcessed;
                _warnings.Add($"Stopped at {maxNodes} nodes (limit reached, {directReports.Count - remaining} nodes truncated)");
                directReports = directReports.Take(remaining).ToList();
                levelResults[depth] = directReports.ToList();
                break;
            }

            levelResults[depth] = directReports.ToList();

            // Prepare next level DNs (excluding cycles)
            currentLevelDNs = directReports
                .Select(r => r.DistinguishedName)
                .Where(dn => !string.IsNullOrWhiteSpace(dn) && !visitedDNs.Contains(dn))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            // Hit max depth with more nodes remaining
            if (depth == maxDepth && currentLevelDNs.Any())
            {
                _warnings.Add($"Stopped at depth {maxDepth} (safety limit, {currentLevelDNs.Count} unexplored nodes)");
            }
        }

        // Final progress update
        var finalTotal = levelResults.Values.Sum(list => list.Count);
        _progress?.Report(new PlanProgressUpdate
        {
            NodesProcessed = finalTotal,
            CurrentDepth = levelResults.Keys.Any() ? levelResults.Keys.Max() : 0,
            EstimatedRemainingNodes = 0,
            Phase = "finalizing"
        });

        // Flatten all levels (exclude level 0 which is seed)
        var allRecords = levelResults
            .Where(kvp => kvp.Key > 0)
            .SelectMany(kvp => kvp.Value)
            .ToList();

        _logger.LogInformation(
            "Org expansion complete: {Levels} levels, {Nodes} total nodes, {Cycles} cycles detected",
            levelResults.Keys.Any() ? levelResults.Keys.Max() : 0,
            allRecords.Count,
            visitedDNs.Count - levelResults.Values.Sum(list => list.Count));

        return allRecords;
    }

    private int? EstimateRemainingNodes(int processed, int currentDepth)
    {
        if (currentDepth <= 1 || processed == 0) return null;

        var avgPerLevel = processed / currentDepth;
        var estimatedRemaining = avgPerLevel * 2;
        return processed + estimatedRemaining;
    }

    private Dictionary<string, object> ComputeAggregation(List<Dictionary<string, object?>> rows, AggregationDefinition aggregation)
    {
        var result = new Dictionary<string, object>();

        if (aggregation.Count && aggregation.GroupBy.Any())
        {
            var grouped = rows
                .GroupBy(row =>
                {
                    var keys = aggregation.GroupBy
                        .Select(field =>
                        {
                            row.TryGetValue(field, out var value);
                            return value?.ToString() ?? "(empty)";
                        })
                        .ToList();
                    return string.Join("|", keys);
                })
                .ToDictionary(g => g.Key, g => g.Count());

            result["grouped_counts"] = grouped;
        }

        // Note: level_metadata will be added when expand_reports stores it in step state
        // For now, aggregation only includes grouped_counts from the final projected data

        return result;
    }

    private StepRuntimeState GetSourceState(DirectoryPlanStep step)
    {
        if (string.IsNullOrWhiteSpace(step.Source))
        {
            throw new InvalidOperationException($"Step {step.Step} requires a source step.");
        }

        if (!_stepStates.TryGetValue(step.Source, out var state))
        {
            throw new InvalidOperationException($"Step {step.Step} references unknown source '{step.Source}'.");
        }

        return state;
    }

    private List<Dictionary<string, object?>> Project(ProjectionDefinition projection)
    {
        if (!_stepStates.TryGetValue(projection.RowStep, out var rowState))
        {
            throw new InvalidOperationException($"Projection references unknown step '{projection.RowStep}'.");
        }

        var rows = new List<Dictionary<string, object?>>();
        var projectionFilters = new List<DirectoryFilter>();

        if (projection.Filters is not null && projection.Filters.Count > 0)
        {
            projectionFilters.AddRange(projection.Filters);
        }

        if (projection.Filter is not null &&
            !projectionFilters.Contains(projection.Filter))
        {
            projectionFilters.Add(projection.Filter);
        }

        foreach (var record in rowState.Records)
        {
            if (projectionFilters.Any() &&
                projectionFilters.Any(filter => !RecordMatchesFilter(record, filter)))
            {
                continue;
            }

            var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);

            foreach (var column in projection.Columns)
            {
                var sourceStep = column.SourceStep ?? projection.RowStep;
                if (!_stepStates.TryGetValue(sourceStep, out var sourceState))
                {
                    throw new InvalidOperationException($"Projection references unknown step '{sourceStep}'.");
                }

                DirectoryRecord? sourceRecord = sourceStep.Equals(projection.RowStep, StringComparison.OrdinalIgnoreCase)
                    ? record
                    : ResolveProjectionSourceRecord(sourceState, record, column);

                object? value = sourceRecord?[column.Attribute];

                if (value is null && column.DefaultValue is not null)
                {
                    value = column.DefaultValue;
                }

                row[column.Name] = value;
            }

            rows.Add(row);
        }

        return rows;
    }

    private bool TryEvaluateTemplateSearch(DirectoryPlanStep step, List<DirectoryFilter> filters, out IReadOnlyList<DirectoryRecord> records)
    {
        records = Array.Empty<DirectoryRecord>();

        var templateMatches = filters.SelectMany(EnumerateTemplateMatches).ToList();
        if (templateMatches.Count == 0)
        {
            return false;
        }

        StepRuntimeState? referencedState = null;
        foreach (var match in templateMatches)
        {
            var referencedStepName = match.Groups["step"].Value;
            if (!_stepStates.TryGetValue(referencedStepName, out var state))
            {
                return false;
            }

            if (referencedState is null)
            {
                referencedState = state;
            }
            else if (!ReferenceEquals(referencedState, state))
            {
                // Multiple referenced steps not yet supported
                return false;
            }
        }

        if (referencedState is null)
        {
            return false;
        }

        var candidates = referencedState.Records;
        if (!candidates.Any())
        {
            return false;
        }

        var filtered = new List<DirectoryRecord>();
        foreach (var candidate in candidates)
        {
            var matches = true;

            foreach (var filter in filters)
            {
                if (FilterContainsTemplate(filter))
                {
                    if (!EvaluateTemplateFilter(filter, candidate, referencedState))
                    {
                        matches = false;
                        break;
                    }
                }
                else
                {
                    if (!RecordMatchesFilter(candidate, filter))
                    {
                        matches = false;
                        break;
                    }
                }
            }

            if (matches)
            {
                filtered.Add(candidate);
            }
        }

        if (filtered.Count == 0)
        {
            return false;
        }

        records = filtered;
        return true;
    }

    private async Task<(IReadOnlyList<DirectoryRecord> Records, List<DirectoryFilter> Filters)?> TryExecutePersonSearchFallbackAsync(
        DirectoryPlanStep step,
        List<DirectoryFilter> filters,
        CancellationToken cancellationToken)
    {
        if (step.TargetType != DirectoryObjectType.User)
        {
            return TryEvaluateTemplateSearch(step, filters, out var inMemoryRecords)
                ? (inMemoryRecords, filters)
                : null;
        }

        var displayNameFilters = filters.Where(f => f.Attribute.Equals("displayName", StringComparison.OrdinalIgnoreCase)).ToList();
        if (displayNameFilters.Count == 0)
        {
            var upnDerivedFilters = new List<DirectoryFilter>();
            var upnFilters = filters
                .Where(f => f.Attribute.Equals("userPrincipalName", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (upnFilters.Count > 0)
            {
                var augmentedFilters = new List<DirectoryFilter>(filters);
                var seenDisplayValues = new HashSet<string>(
                    filters
                        .Where(f => f.Attribute.Equals("displayName", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(f.Value))
                        .Select(f => f.Value!),
                    StringComparer.OrdinalIgnoreCase);

                foreach (var upnFilter in upnFilters)
                {
                    if (string.IsNullOrWhiteSpace(upnFilter.Value))
                    {
                        continue;
                    }

                    foreach (var candidate in EnumerateDisplayNameCandidatesFromUpn(upnFilter.Value))
                    {
                        if (seenDisplayValues.Add(candidate))
                        {
                            var generatedFilter = new DirectoryFilter
                            {
                                Attribute = "displayName",
                                Operator = "equals",
                                Value = candidate
                            };

                            upnDerivedFilters.Add(generatedFilter);
                            augmentedFilters.Add(generatedFilter);
                        }
                    }
                }

                if (upnDerivedFilters.Count > 0)
                {
                    filters = augmentedFilters;
                    displayNameFilters = upnDerivedFilters;
                }
            }

            if (displayNameFilters.Count == 0)
            {
                return TryEvaluateTemplateSearch(step, filters, out var inMemoryRecords)
                    ? (inMemoryRecords, filters)
                    : null;
            }
        }

        var candidateValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var filter in displayNameFilters)
        {
            if (!string.IsNullOrWhiteSpace(filter.Value))
            {
                foreach (var candidate in EnumerateDisplayNameCandidates(filter.Value))
                {
                    candidateValues.Add(candidate);
                }
            }
        }

        if (candidateValues.Count == 0)
        {
            return null;
        }

        foreach (var candidate in candidateValues)
        {
            var alternativeFilters = filters
                .Select(filter => filter.Attribute.Equals("displayName", StringComparison.OrdinalIgnoreCase)
                    ? new DirectoryFilter
                    {
                        Attribute = filter.Attribute,
                        Operator = "equals",
                        Value = candidate
                    }
                    : new DirectoryFilter
                    {
                        Attribute = filter.Attribute,
                        Operator = filter.Operator,
                        Value = filter.Value
                    })
                .ToList();

            var request = new DirectorySearchRequest
            {
                TargetType = step.TargetType,
                Attributes = step.Attributes,
                Filters = alternativeFilters,
                SizeLimit = step.SizeLimit
            };

            var result = await _directoryService.SearchAsync(request, cancellationToken);
            if (result.Count > 0)
            {
                return (result.ToList(), alternativeFilters);
            }

            if (TryParseName(candidate, out var first, out var last))
            {
                var containsFilters = new List<DirectoryFilter>
                {
                    new() { Attribute = "displayName", Operator = "contains", Value = last },
                    new() { Attribute = "displayName", Operator = "contains", Value = first }
                };

                var additionalFilters = filters
                    .Where(filter => !filter.Attribute.Equals("displayName", StringComparison.OrdinalIgnoreCase))
                    .Select(filter => new DirectoryFilter
                    {
                        Attribute = filter.Attribute,
                        Operator = filter.Operator,
                        Value = filter.Value
                    });

                containsFilters.AddRange(additionalFilters);

                request = new DirectorySearchRequest
                {
                    TargetType = step.TargetType,
                    Attributes = step.Attributes,
                    Filters = containsFilters,
                    SizeLimit = step.SizeLimit
                };

                result = await _directoryService.SearchAsync(request, cancellationToken);
                if (result.Count > 0)
                {
                    return (result.ToList(), containsFilters);
                }
            }
        }

        return TryEvaluateTemplateSearch(step, filters, out var templateRecords)
            ? (templateRecords, filters)
            : null;
    }

    private static IEnumerable<string> EnumerateDisplayNameCandidatesFromUpn(string upn)
    {
        if (string.IsNullOrWhiteSpace(upn))
        {
            yield break;
        }

        var atIndex = upn.IndexOf('@');
        var localPart = atIndex >= 0 ? upn[..atIndex] : upn;

        if (string.IsNullOrWhiteSpace(localPart))
        {
            yield break;
        }

        var nameTokens = localPart
            .Split(new[] { '.', '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => NormalizeNameComponent(token))
            .ToList();

        if (nameTokens.Count < 2)
        {
            yield break;
        }

        var first = nameTokens.First();
        var last = nameTokens.Last();

        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            $"{last}, {first}",
            $"{first} {last}"
        };

        if (!string.Equals(first, last, StringComparison.OrdinalIgnoreCase))
        {
            candidates.Add($"{first}, {last}");
            candidates.Add($"{last} {first}");
        }

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                yield return candidate;
            }
        }
    }

    private IEnumerable<string> EnumerateDisplayNameCandidates(string original)
    {
        if (string.IsNullOrWhiteSpace(original))
        {
            yield break;
        }

        var trimmed = original.Trim();
        yield return trimmed;

        if (trimmed.Contains(',', StringComparison.Ordinal))
        {
            var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 2)
            {
                var last = parts[0].Trim();
                var first = parts[1].Trim();

                var canonical = $"{NormalizeNameComponent(last)}, {NormalizeNameComponent(first)}";
                if (!string.Equals(trimmed, canonical, StringComparison.OrdinalIgnoreCase))
                {
                    yield return canonical;
                }

                yield return $"{NormalizeNameComponent(first)} {NormalizeNameComponent(last)}";
            }
            yield break;
        }

        var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length >= 2)
        {
            var first = tokens[0];
            var last = tokens[^1];

            yield return $"{NormalizeNameComponent(last)}, {NormalizeNameComponent(first)}";
            yield return $"{NormalizeNameComponent(first)} {NormalizeNameComponent(last)}";
        }
    }

    private static string NormalizeNameComponent(string value)
    {
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.ToLowerInvariant());
    }

    private static bool TryParseName(string value, out string first, out string last)
    {
        first = string.Empty;
        last = string.Empty;

        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var trimmed = value.Trim();
        if (trimmed.Contains(',', StringComparison.Ordinal))
        {
            var parts = trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2)
            {
                last = NormalizeNameComponent(parts[0].Trim());
                first = NormalizeNameComponent(parts[1].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty);
                return true;
            }
        }
        else
        {
            var tokens = trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length >= 2)
            {
                first = NormalizeNameComponent(tokens.First());
                last = NormalizeNameComponent(tokens.Last());
                return true;
            }
        }

        return false;
    }

    private bool EvaluateTemplateFilter(DirectoryFilter filter, DirectoryRecord record, StepRuntimeState referencedState)
    {
        if (filter is null)
        {
            return false;
        }

        var operatorValue = (filter.Operator ?? "equals").Trim().ToLowerInvariant();

        if (filter.Conditions is { Count: > 0 })
        {
            return operatorValue switch
            {
                "or" => filter.Conditions.Any(child => EvaluateTemplateFilter(child, record, referencedState)),
                "and" => filter.Conditions.All(child => EvaluateTemplateFilter(child, record, referencedState)),
                _ => filter.Conditions.All(child => EvaluateTemplateFilter(child, record, referencedState))
            };
        }

        var referencedValue = ResolveTemplateValue(filter.Value, referencedState, record);
        if (referencedValue is null)
        {
            return false;
        }

        var candidateValue = record.GetString(filter.Attribute);
        if (string.IsNullOrWhiteSpace(candidateValue) && filter.Attribute.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase))
        {
            candidateValue = record.DistinguishedName;
        }

        if (candidateValue is null)
        {
            return false;
        }

        var isNegated = operatorValue.StartsWith("not_");
        var baseOperator = isNegated ? operatorValue[4..] : operatorValue;
        var comparison = MatchesBaseOperator(candidateValue, referencedValue, baseOperator);
        return isNegated ? !comparison : comparison;
    }

    private bool FilterContainsTemplate(DirectoryFilter filter)
    {
        if (filter is null)
        {
            return false;
        }

        if (filter.Conditions is { Count: > 0 })
        {
            return filter.Conditions.Any(FilterContainsTemplate);
        }

        return TemplateRegex.IsMatch(filter.Value ?? string.Empty);
    }

    private IEnumerable<Match> EnumerateTemplateMatches(DirectoryFilter filter)
    {
        if (filter is null)
        {
            yield break;
        }

        if (filter.Conditions is { Count: > 0 })
        {
            foreach (var child in filter.Conditions)
            {
                foreach (var match in EnumerateTemplateMatches(child))
                {
                    yield return match;
                }
            }

            yield break;
        }

        if (string.IsNullOrWhiteSpace(filter.Value))
        {
            yield break;
        }

        foreach (Match match in TemplateRegex.Matches(filter.Value))
        {
            if (match.Success)
            {
                yield return match;
            }
        }
    }

    private string? ResolveTemplateValue(string? template, StepRuntimeState referencedState, DirectoryRecord record)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return template;
        }

        var matches = TemplateRegex.Matches(template);
        if (matches.Count == 0)
        {
            return template;
        }

        var result = new System.Text.StringBuilder();
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            if (!match.Success)
            {
                continue;
            }

            var referencedStepName = match.Groups["step"].Value;
            if (!string.IsNullOrWhiteSpace(referencedStepName) &&
                !string.Equals(referencedStepName, referencedState.StepName, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var attribute = match.Groups["attribute"].Value;
            var referencedValue = record.GetString(attribute);
            if (referencedValue is null)
            {
                return null;
            }

            result.Append(template, lastIndex, match.Index - lastIndex);
            result.Append(referencedValue);
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < template.Length)
        {
            result.Append(template, lastIndex, template.Length - lastIndex);
        }

        return result.ToString();
    }

    private bool TryExpandTemplateFilters(DirectoryPlanStep step, List<DirectoryFilter> filters, out List<List<DirectoryFilter>> expandedFilterSets)
    {
        expandedFilterSets = new List<List<DirectoryFilter>>();

        var references = CollectTemplateReferences(filters);
        if (references.Count == 0)
        {
            return false;
        }

        if (references.Any(reference => reference.Value.Records.Count == 0))
        {
            return true;
        }

        var combinations = BuildRecordCombinations(references);
        foreach (var combination in combinations)
        {
            var clonedFilters = CloneFilterSetWithTemplates(filters, combination);
            if (clonedFilters is not null && clonedFilters.Count > 0)
            {
                expandedFilterSets.Add(clonedFilters);
            }
        }

        return true;
    }

    private Dictionary<string, StepRuntimeState> CollectTemplateReferences(IEnumerable<DirectoryFilter> filters)
    {
        var result = new Dictionary<string, StepRuntimeState>(StringComparer.OrdinalIgnoreCase);

        foreach (var filter in filters)
        {
            CollectTemplateReferences(filter, result);
        }

        return result;
    }

    private void CollectTemplateReferences(DirectoryFilter? filter, IDictionary<string, StepRuntimeState> references)
    {
        if (filter is null)
        {
            return;
        }

        if (filter.Conditions is { Count: > 0 })
        {
            foreach (var child in filter.Conditions)
            {
                CollectTemplateReferences(child, references);
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(filter.Value))
        {
            return;
        }

        foreach (Match match in TemplateRegex.Matches(filter.Value))
        {
            if (!match.Success)
            {
                continue;
            }

            var stepName = match.Groups["step"].Value;
            if (string.IsNullOrWhiteSpace(stepName))
            {
                continue;
            }

            if (!references.ContainsKey(stepName) && _stepStates.TryGetValue(stepName, out var state))
            {
                references[stepName] = state;
            }
        }
    }

    private List<Dictionary<string, DirectoryRecord>> BuildRecordCombinations(Dictionary<string, StepRuntimeState> references)
    {
        var referenceList = references.ToList();
        var combinations = new List<Dictionary<string, DirectoryRecord>>();
        BuildRecordCombinationsRecursive(referenceList, 0, new Dictionary<string, DirectoryRecord>(StringComparer.OrdinalIgnoreCase), combinations);
        return combinations;
    }

    private void BuildRecordCombinationsRecursive(
        IReadOnlyList<KeyValuePair<string, StepRuntimeState>> references,
        int index,
        Dictionary<string, DirectoryRecord> current,
        List<Dictionary<string, DirectoryRecord>> combinations)
    {
        if (index >= references.Count)
        {
            combinations.Add(new Dictionary<string, DirectoryRecord>(current, StringComparer.OrdinalIgnoreCase));
            return;
        }

        var (stepName, state) = references[index];
        foreach (var record in state.Records)
        {
            current[stepName] = record;
            BuildRecordCombinationsRecursive(references, index + 1, current, combinations);
        }

        current.Remove(stepName);
    }

    private List<DirectoryFilter>? CloneFilterSetWithTemplates(IEnumerable<DirectoryFilter> filters, Dictionary<string, DirectoryRecord> recordMap)
    {
        var cloned = new List<DirectoryFilter>();
        foreach (var filter in filters)
        {
            var clone = CloneFilterWithTemplates(filter, recordMap);
            if (clone is null)
            {
                return null;
            }
            cloned.Add(clone);
        }

        return cloned;
    }

    private DirectoryFilter? CloneFilterWithTemplates(DirectoryFilter? filter, Dictionary<string, DirectoryRecord> recordMap)
    {
        if (filter is null)
        {
            return null;
        }

        var clone = new DirectoryFilter
        {
            Attribute = filter.Attribute,
            Operator = filter.Operator,
            Value = filter.Value,
            Conditions = filter.Conditions is { Count: > 0 } ? new List<DirectoryFilter>() : null
        };

        if (filter.Conditions is { Count: > 0 })
        {
            foreach (var child in filter.Conditions)
            {
                var clonedChild = CloneFilterWithTemplates(child, recordMap);
                if (clonedChild is null)
                {
                    return null;
                }
                clone.Conditions!.Add(clonedChild);
            }

            return clone;
        }

        var replacedValue = ReplaceTemplatePlaceholders(filter.Value, recordMap);
        if (replacedValue is null)
        {
            return null;
        }

        clone.Value = replacedValue;
        return clone;
    }

    private string? ReplaceTemplatePlaceholders(string? template, Dictionary<string, DirectoryRecord> recordMap)
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return template;
        }

        var matches = TemplateRegex.Matches(template);
        if (matches.Count == 0)
        {
            return template;
        }

        var builder = new System.Text.StringBuilder();
        var lastIndex = 0;

        foreach (Match match in matches)
        {
            if (!match.Success)
            {
                continue;
            }

            var stepName = match.Groups["step"].Value;
            if (!recordMap.TryGetValue(stepName, out var record))
            {
                return null;
            }

            var attribute = match.Groups["attribute"].Value;
            var value = record.GetString(attribute);
            if (value is null && attribute.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase))
            {
                value = record.DistinguishedName;
            }

            if (value is null)
            {
                return null;
            }

            builder.Append(template, lastIndex, match.Index - lastIndex);
            builder.Append(value);
            lastIndex = match.Index + match.Length;
        }

        if (lastIndex < template.Length)
        {
            builder.Append(template, lastIndex, template.Length - lastIndex);
        }

        return builder.ToString();
    }

    private bool TryNormalizeFilters(DirectoryPlanStep step, out List<DirectoryFilter> normalized)
    {
        normalized = new List<DirectoryFilter>();

        if (step.Filters is null || step.Filters.Count == 0)
        {
            return true;
        }

        foreach (var filter in step.Filters)
        {
            if (filter is null)
            {
                continue;
            }

            if (!TryNormalizeFilter(filter, out var normalizedFilter))
            {
                return false;
            }

            normalized.Add(normalizedFilter);
        }

        return true;
    }

    private bool TryNormalizeFilter(DirectoryFilter filter, out DirectoryFilter normalized)
    {
        var operatorValue = string.IsNullOrWhiteSpace(filter.Operator)
            ? (filter.Conditions is { Count: > 0 } ? "and" : "equals")
            : filter.Operator.Trim();

        if (filter.Conditions is { Count: > 0 })
        {
            var normalizedChildren = new List<DirectoryFilter>();
            foreach (var child in filter.Conditions)
            {
                if (child is null)
                {
                    continue;
                }

                if (!TryNormalizeFilter(child, out var normalizedChild))
                {
                    normalized = null!;
                    return false;
                }

                normalizedChildren.Add(normalizedChild);
            }

            normalized = new DirectoryFilter
            {
                Operator = operatorValue,
                Conditions = normalizedChildren
            };
            return true;
        }

        var trimmedAttribute = filter.Attribute?.Trim();
        var trimmedValue = filter.Value?.Trim();

        if (string.IsNullOrWhiteSpace(trimmedAttribute) ||
            (string.IsNullOrWhiteSpace(trimmedValue) && !AllowsEmptyFilterValue(trimmedAttribute, operatorValue)))
        {
            normalized = null!;
            return false;
        }

        normalized = new DirectoryFilter
        {
            Attribute = trimmedAttribute,
            Operator = operatorValue,
            Value = trimmedValue ?? string.Empty
        };

        return true;
    }

    private static bool AllowsEmptyFilterValue(string? attribute, string operatorValue)
    {
        if (string.IsNullOrWhiteSpace(attribute))
        {
            return false;
        }

        if (attribute.Equals("AccountExpirationDate", StringComparison.OrdinalIgnoreCase) &&
            (operatorValue.Equals("not_equals", StringComparison.OrdinalIgnoreCase) ||
             operatorValue.Equals("equals", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }

    private static bool RecordMatchesFilter(DirectoryRecord record, DirectoryFilter filter)
    {
        var operatorValue = (filter.Operator ?? "equals").Trim().ToLowerInvariant();

        if (filter.Conditions is { Count: > 0 })
        {
            return operatorValue switch
            {
                "or" => filter.Conditions.Any(child => RecordMatchesFilter(record, child)),
                "and" => filter.Conditions.All(child => RecordMatchesFilter(record, child)),
                _ => filter.Conditions.All(child => RecordMatchesFilter(record, child))
            };
        }

        var isNegated = operatorValue.StartsWith("not_");
        var baseOperator = isNegated ? operatorValue[4..] : operatorValue;
        var expected = filter.Value ?? string.Empty;

        var candidates = record.GetStrings(filter.Attribute)
            .Where(v => v is not null)
            .Select(v => v ?? string.Empty)
            .ToList();

        if (!candidates.Any())
        {
            var single = record.GetString(filter.Attribute);
            if (single is not null)
            {
                candidates.Add(single);
            }
        }

        if (!candidates.Any())
        {
            candidates.Add(string.Empty);
        }

        var match = candidates.Any(candidate => MatchesBaseOperator(candidate, expected, baseOperator));
        return isNegated ? !match : match;
    }

    private static bool MatchesBaseOperator(string candidate, string expected, string operatorValue)
    {
        candidate ??= string.Empty;
        expected ??= string.Empty;
        var comparison = StringComparison.OrdinalIgnoreCase;

        return operatorValue switch
        {
            "equals" => string.Equals(candidate, expected, comparison),
            "contains" => expected.Length == 0 || candidate.IndexOf(expected, comparison) >= 0,
            "starts_with" => expected.Length == 0 || candidate.StartsWith(expected, comparison),
            "ends_with" => expected.Length == 0 || candidate.EndsWith(expected, comparison),
            _ => false
        };
    }

    private static DirectoryRecord? ResolveProjectionSourceRecord(StepRuntimeState sourceState, DirectoryRecord rowRecord, ProjectionColumn column)
    {
        var matchOn = column.MatchOn ?? "distinguishedName";
        var matchValue = !string.IsNullOrWhiteSpace(column.MatchValueFrom)
            ? rowRecord.GetString(column.MatchValueFrom)
            : rowRecord.DistinguishedName;

        return sourceState.FindByAttribute(matchOn, matchValue);
    }

    private static readonly Regex TemplateRegex = new(@"\{\{\s*(?<step>[^.\s]+)\.(?<attribute>[^}\s]+)\s*\}\}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private sealed class StepRuntimeState
    {
        private readonly Dictionary<string, DirectoryRecord> _indexByDistinguishedName;
        private readonly IReadOnlyList<DirectoryRecord> _records;

        public StepRuntimeState(DirectoryPlanStep step, IReadOnlyList<DirectoryRecord> records)
        {
            _records = records;
            StepName = step.Name ?? string.Empty;
            _indexByDistinguishedName = records
                .Where(r => !string.IsNullOrWhiteSpace(r.DistinguishedName))
                .GroupBy(r => r.DistinguishedName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

        public string StepName { get; }

        public IReadOnlyList<DirectoryRecord> Records => _records;

        public DirectoryRecord? FindByAttribute(string attribute, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return null;
            }

            if (attribute.Equals("distinguishedName", StringComparison.OrdinalIgnoreCase))
            {
                return _indexByDistinguishedName.TryGetValue(value, out var record) ? record : null;
            }

            return _records.FirstOrDefault(r =>
            {
                var candidate = r.GetString(attribute);
                return candidate is not null && candidate.Equals(value, StringComparison.OrdinalIgnoreCase);
            });
        }
    }

    internal sealed class RuntimeResult
    {
        public bool Success { get; set; }

        public List<Dictionary<string, object?>> Data { get; set; } = new();

        public List<string> Errors { get; set; } = new();

        public List<string> Warnings { get; set; } = new();

        public int StepsExecuted { get; set; }

        public int StepsSkipped { get; set; }

        public Dictionary<string, object>? Aggregation { get; set; }
    }
}




