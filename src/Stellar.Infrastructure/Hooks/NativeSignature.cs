using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
namespace Stellar.Infrastructure.Hooks;

/// <summary>An interop method's exact signature as text — what a native detour checks before it goes live (a detour's
/// delegate IS the native ABI, so a game patch that changes one parameter type, count or the return type must turn the
/// detour off, never call the original with the wrong arguments). Type text: the full name, generic arguments in
/// brackets, by-ref as a trailing <c>&amp;</c> (e.g. <c>ECSModel.ExternalBlobPtr`1[ECSModel.AnimationClipBlob]</c>,
/// <c>Unity.Mathematics.float2&amp;</c>). Pure.</summary>
internal readonly record struct NativeSignature(string Return, IReadOnlyList<string> Parameters)
{
    internal static string TypeText(Type t) =>
        t.IsByRef ? TypeText(t.GetElementType()!) + "&"
        : t.IsGenericType ? $"{t.GetGenericTypeDefinition().FullName}[{string.Join(",", t.GetGenericArguments().Select(TypeText))}]"
        : t.FullName ?? t.Name;

    internal static NativeSignature Of(MethodInfo m) =>
        new(TypeText(m.ReturnType), m.GetParameters().Select(p => TypeText(p.ParameterType)).ToArray());

    /// <summary>True when <paramref name="m"/> has exactly this return type and parameter-type list.</summary>
    internal bool Matches(MethodInfo m)
    {
        var actual = Of(m);
        return actual.Return == Return && actual.Parameters.SequenceEqual(Parameters);
    }

    public override string ToString() => $"{Return} ({string.Join(", ", Parameters)})";
}
