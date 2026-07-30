using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using AdQuery.Orchestrator.Controllers;

namespace AdQuery.Orchestrator.Tests.Unit;

/// <summary>
/// Static reachability over the application assembly's IL, shared by the invariant guards that
/// need to prove a claim about everything a method can reach rather than about one execution.
/// <para>
/// Extracted from <c>ExportIsModelFreeTests</c> when F04 Slice 7 needed the same walk to prove
/// no completed-result reader is still on the results cache. Both claims are "no input can",
/// not "this input did not", which a stub-driven test cannot establish.
/// </para>
/// </summary>
internal static class AssemblyCallGraph
{
    internal static readonly Assembly AppAssembly = typeof(QueryController).Assembly;

    /// <summary>
    /// Every method in the application assembly transitively reachable from <paramref name="root"/>.
    /// Calls out of the assembly (BCL, ClosedXML) are recorded as callees by
    /// <see cref="CalledMembers"/> but not descended into.
    /// <para>
    /// A call to an interface or abstract method descends into **every** application-assembly
    /// implementation of it (slice4-or-2). Ordinary DI dispatch would otherwise stop the walk at
    /// an empty interface body, so routing work through an injected service would silently hide
    /// whatever that service calls. Over-approximating — walking implementations the runtime
    /// might never select — is the safe direction for an invariant a guard must not miss.
    /// </para>
    /// </summary>
    internal static IReadOnlyCollection<MethodBase> ReachableMethods(MethodBase root)
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
                if (callee is not MethodBase method || method.DeclaringType?.Assembly != AppAssembly)
                {
                    continue;
                }

                foreach (var target in Enumerable.Repeat(method, 1).Concat(ImplementationsOf(method)))
                {
                    if (seen.Add(target))
                    {
                        queue.Enqueue(target);
                    }
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// Application-assembly overrides and interface implementations of a virtual, abstract, or
    /// interface method. Empty for an ordinary concrete call.
    /// </summary>
    private static IEnumerable<MethodBase> ImplementationsOf(MethodBase method)
    {
        var declaring = method.DeclaringType;
        if (declaring == null || method is not MethodInfo declared ||
            !(declaring.IsInterface || declared.IsAbstract || declared.IsVirtual))
        {
            yield break;
        }

        foreach (var type in AppAssembly.GetTypes())
        {
            if (type.IsInterface || type.IsAbstract || !declaring.IsAssignableFrom(type))
            {
                continue;
            }

            var target = declaring.IsInterface
                ? MapInterfaceMethod(type, declaring, declared)
                : type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                    .FirstOrDefault(m => m.GetBaseDefinition() == declared.GetBaseDefinition());

            if (target != null && target != declared && target.DeclaringType?.Assembly == AppAssembly)
            {
                yield return target;
            }
        }
    }

    private static MethodInfo? MapInterfaceMethod(Type implementation, Type iface, MethodInfo declared)
    {
        if (implementation.IsGenericTypeDefinition)
        {
            // An open generic has no runtime interface map; its methods are unreachable as
            // written and any concrete instantiation is reached through its own type.
            return null;
        }

        var map = implementation.GetInterfaceMap(iface);
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            if (map.InterfaceMethods[i] == declared)
            {
                return map.TargetMethods[i];
            }
        }

        return null;
    }

    internal static IEnumerable<MemberInfo> CalledMembers(MethodBase method) =>
        ResolveTokens(method, static op =>
            op.OperandType == OperandType.InlineMethod || op.OperandType == OperandType.InlineTok)
            .OfType<MethodBase>();

    internal static IEnumerable<FieldInfo> LoadedFields(MethodBase method) =>
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
                // this reflection context; skipping them cannot hide an ordinary call to a
                // non-generic interface member, which is what every guarded claim is about.
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
