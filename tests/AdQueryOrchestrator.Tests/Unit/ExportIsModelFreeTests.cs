using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using AdQuery.Orchestrator.Controllers;
using AdQuery.Orchestrator.Services;
using Xunit;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// F04 Slice 4 invariant lock: <see cref="QueryController.DownloadAsync"/> serializes the
/// settled result artifact and nothing else. It must never reach a model or re-execute a
/// plan — the owner's binding constraint is that exporting can never risk producing a
/// different result than the answer the user already read.
///
/// The guard walks the whole call graph reachable from <c>DownloadAsync</c> through the
/// application assembly and asserts that no method in it calls <see cref="IClaudeService"/>
/// or <see cref="IDirectoryPlanExecutor"/>, and that the controller's model and executor
/// fields are never even loaded. A "fail the test if the model is invoked" stub cannot be
/// used here: <c>DownloadAsync</c> writes its audit copy under
/// <see cref="QueryLogHelper.OutputRoot"/> (a hard-coded <c>E:\</c> path), which does not
/// exist on a build agent, so the method cannot be driven end to end portably. Reading the
/// call graph proves the stronger claim anyway — not merely that this input made no model
/// call, but that no input can.
/// </summary>
public sealed class ExportIsModelFreeTests
{
    private static readonly Assembly AppAssembly = typeof(QueryController).Assembly;

    private static readonly HashSet<Type> ForbiddenTypes =
    [
        typeof(IClaudeService),
        typeof(IDirectoryPlanExecutor),
    ];

    private static readonly string[] ForbiddenControllerFields =
    [
        "_claudeService",
        "_planExecutor",
    ];

    [Fact]
    public void DownloadAsync_CallGraph_NeverReachesTheModelOrThePlanExecutor()
    {
        var offenders = new List<string>();

        foreach (var method in ReachableMethods(DownloadAsyncMethod()))
        {
            foreach (var callee in CalledMembers(method))
            {
                var declaring = callee.DeclaringType;
                if (declaring == null)
                {
                    continue;
                }

                var forbidden = ForbiddenTypes.Any(t =>
                    t == declaring || (t.IsAssignableFrom(declaring) && declaring != typeof(object)));
                if (forbidden)
                {
                    offenders.Add($"{method.DeclaringType?.Name}.{method.Name} → {declaring.Name}.{callee.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Export must serialize the settled artifact, never re-derive it. Model/executor calls "
            + "reachable from DownloadAsync: " + string.Join("; ", offenders));
    }

    [Fact]
    public void DownloadAsync_CallGraph_NeverLoadsTheModelOrExecutorFields()
    {
        // Belt and braces for the call-graph assertion above: a model call routed through a
        // local, a delegate, or a helper that receives the service as an argument would still
        // have to read one of these fields somewhere in the graph.
        var offenders = new List<string>();

        foreach (var method in ReachableMethods(DownloadAsyncMethod()))
        {
            foreach (var field in LoadedFields(method))
            {
                if (field.DeclaringType == typeof(QueryController) &&
                    ForbiddenControllerFields.Contains(field.Name))
                {
                    offenders.Add($"{method.DeclaringType?.Name}.{method.Name} → {field.Name}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "DownloadAsync must not read the model or plan-executor services. Loads: "
            + string.Join("; ", offenders));
    }

    [Fact]
    public void TheGuardWalksARealCallGraph()
    {
        // Over-removal sentinel: if the walker silently resolved nothing, the two assertions
        // above would pass vacuously. DownloadAsync demonstrably reaches the serializer.
        var reachable = ReachableMethods(DownloadAsyncMethod())
            .Select(m => $"{m.DeclaringType?.Name}.{m.Name}")
            .ToHashSet(StringComparer.Ordinal);

        Assert.Contains("QueryController.GenerateFileContent", reachable);
        Assert.Contains("QueryController.BuildGroupedDistributionExport", reachable);
        Assert.Contains("QueryLogHelper.GetUserDirectory", reachable);
        Assert.True(reachable.Count > 10, $"walked only {reachable.Count} methods");
    }

    private static MethodInfo DownloadAsyncMethod() =>
        typeof(QueryController).GetMethod(nameof(QueryController.DownloadAsync))
        ?? throw new InvalidOperationException("QueryController.DownloadAsync was not found.");

    /// <summary>
    /// Every method in the application assembly transitively reachable from <paramref name="root"/>.
    /// Calls out of the assembly (BCL, ClosedXML) are recorded as callees by
    /// <see cref="CalledMembers"/> but not descended into.
    /// </summary>
    private static IReadOnlyCollection<MethodBase> ReachableMethods(MethodBase root)
    {
        var seen = new HashSet<MethodBase>();
        var queue = new Queue<MethodBase>();
        seen.Add(root);
        queue.Enqueue(root);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            foreach (var callee in CalledMembers(current))
            {
                if (callee is not MethodBase method ||
                    method.DeclaringType?.Assembly != AppAssembly ||
                    !seen.Add(method))
                {
                    continue;
                }

                queue.Enqueue(method);
            }
        }

        return seen;
    }

    private static IEnumerable<MemberInfo> CalledMembers(MethodBase method) =>
        ResolveTokens(method, static op =>
            op.OperandType == OperandType.InlineMethod || op.OperandType == OperandType.InlineTok)
            .OfType<MethodBase>();

    private static IEnumerable<FieldInfo> LoadedFields(MethodBase method) =>
        ResolveTokens(method, static op => op.OperandType == OperandType.InlineField)
            .OfType<FieldInfo>();

    private static IEnumerable<MemberInfo> ResolveTokens(MethodBase method, Func<OpCode, bool> wanted)
    {
        var il = method.GetMethodBody()?.GetILAsByteArray();
        if (il == null)
        {
            yield break;
        }

        var module = method.Module;
        var typeArgs = method.DeclaringType?.IsGenericType == true
            ? method.DeclaringType.GetGenericArguments()
            : null;
        var methodArgs = method.IsGenericMethodDefinition ? method.GetGenericArguments() : null;

        foreach (var (op, operandOffset) in ReadInstructions(il))
        {
            if (!wanted(op))
            {
                continue;
            }

            var token = BitConverter.ToInt32(il, operandOffset);
            MemberInfo? member;
            try
            {
                member = module.ResolveMember(token, typeArgs, methodArgs);
            }
            catch (ArgumentException)
            {
                // Tokens for constructed generics and vararg sites can fail to resolve in
                // this reflection context; skipping them cannot hide a model call, which is
                // an ordinary call to a non-generic interface member.
                continue;
            }

            if (member != null)
            {
                yield return member;
            }
        }
    }

    /// <summary>
    /// Minimal IL walker: yields each opcode with the offset of its inline operand, stepping
    /// by the operand width so that operand bytes are never misread as opcodes.
    /// </summary>
    private static IEnumerable<(OpCode Op, int OperandOffset)> ReadInstructions(byte[] il)
    {
        var offset = 0;
        while (offset < il.Length)
        {
            OpCode op;
            if (il[offset] == 0xFE && offset + 1 < il.Length)
            {
                op = TwoByteOpCodes[il[offset + 1]];
                offset += 2;
            }
            else
            {
                op = SingleByteOpCodes[il[offset]];
                offset += 1;
            }

            var operandOffset = offset;
            offset += op.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI
                    or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineBrTarget or OperandType.InlineField or OperandType.InlineI
                    or OperandType.InlineMethod or OperandType.InlineSig or OperandType.InlineString
                    or OperandType.InlineTok or OperandType.InlineType
                    or OperandType.ShortInlineR => 4,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, operandOffset)),
                _ => throw new NotSupportedException($"Unhandled operand type {op.OperandType}."),
            };

            yield return (op, operandOffset);
        }
    }

    private static readonly OpCode[] SingleByteOpCodes = BuildOpCodeTable(twoByte: false);
    private static readonly OpCode[] TwoByteOpCodes = BuildOpCodeTable(twoByte: true);

    private static OpCode[] BuildOpCodeTable(bool twoByte)
    {
        var table = new OpCode[256];
        for (var i = 0; i < table.Length; i++)
        {
            // Unused slots decode as a no-operand opcode so the walker steps by one byte
            // rather than throwing on padding it will never be asked about.
            table[i] = OpCodes.Nop;
        }

        foreach (var field in typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static))
        {
            if (field.GetValue(null) is not OpCode op)
            {
                continue;
            }

            var value = (ushort)op.Value;
            var isTwoByte = value > 0xFF;
            if (isTwoByte == twoByte)
            {
                table[value & 0xFF] = op;
            }
        }

        return table;
    }
}
