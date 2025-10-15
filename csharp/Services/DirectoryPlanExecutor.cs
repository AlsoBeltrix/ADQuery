using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
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

    public async Task<PlanExecutionResult> ExecutePlanAsync(DirectoryQueryPlan plan, CancellationToken cancellationToken = default)
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

            var runtime = new DirectoryPlanRuntime(_directoryService, _logger);
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

    private readonly Dictionary<string, StepRuntimeState> _stepStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();

    public DirectoryPlanRuntime(IActiveDirectoryService directoryService, ILogger logger)
    {
        _directoryService = directoryService;
        _logger = logger;
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
            }
        }

        result.Data = Project(plan.Projection);

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
            _ => throw new InvalidOperationException($"Unsupported operation '{step.Operation}'.")
        };
    }

    private Task<IReadOnlyList<DirectoryRecord>> ExecuteSearchStep(DirectoryPlanStep step, CancellationToken cancellationToken)
    {
        var request = new DirectorySearchRequest
        {
            TargetType = step.TargetType,
            Attributes = step.Attributes,
            Filters = step.Filters,
            SizeLimit = step.SizeLimit
        };

        return _directoryService.SearchAsync(request, cancellationToken);
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

        foreach (var record in rowState.Records)
        {
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

    private static DirectoryRecord? ResolveProjectionSourceRecord(StepRuntimeState sourceState, DirectoryRecord rowRecord, ProjectionColumn column)
    {
        var matchOn = column.MatchOn ?? "distinguishedName";
        var matchValue = !string.IsNullOrWhiteSpace(column.MatchValueFrom)
            ? rowRecord.GetString(column.MatchValueFrom)
            : rowRecord.DistinguishedName;

        return sourceState.FindByAttribute(matchOn, matchValue);
    }

    private sealed class StepRuntimeState
    {
        private readonly Dictionary<string, DirectoryRecord> _indexByDistinguishedName;
        private readonly IReadOnlyList<DirectoryRecord> _records;

        public StepRuntimeState(DirectoryPlanStep step, IReadOnlyList<DirectoryRecord> records)
        {
            _records = records;
            _indexByDistinguishedName = records
                .Where(r => !string.IsNullOrWhiteSpace(r.DistinguishedName))
                .GroupBy(r => r.DistinguishedName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        }

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
    }
}
