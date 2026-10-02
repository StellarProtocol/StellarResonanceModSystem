using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Infrastructure.Hooks;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>The typed HarmonyX prefixes of the freeze's animation-request gate (<see cref="AnimRequestGate"/>; perf review
/// 2026-10-03 major 1 / qa I-3). The shared prefix trampoline paid an <c>object[] __args</c> array plus a box per argument on
/// EVERY call of the patched entry points, all session long, frozen or not; these take the game's arguments by position
/// (<c>__0</c>…), so a call costs a counter increment and one field check until a freeze is armed, and the argument copy
/// for the replay is built only for a call that is actually deferred.
/// <para><b>Only the internal twins are gated</b> (release_3.7 ISIL of <c>Panda.ZGame.ECSAnimController</c>):
/// <c>PlayBaseState</c> / <c>PlayUpperState</c> / <c>PlayAdditiveState</c> / <c>PlayManualClip</c> each end in a direct call
/// to <c>playBaseState</c> / <c>playUpperState</c> / <c>playAdditiveState</c> / <c>playManualClip</c> with their own
/// arguments unchanged (after a clip-list clear, or an early return when nothing plays); the sequence entry points
/// (<c>PlayBaseStateSeq</c> / <c>PlayUpperStateSeq</c> → <c>playBaseState</c> / <c>playUpperState</c>,
/// <c>PlayBaseClipsSeq</c> / <c>PlayUpperClipsSeq</c> → <c>playManualClip</c>) and the state-end callbacks reach the same
/// four. So the four see every request, and a sequence's own first request replaces a stale kept one for its layer (qa M-5
/// needs no prefix of its own).</para>
/// <para>A prefix goes live only on an interop method with exactly the signature in <see cref="Expected"/>: a by-position
/// prefix on a changed signature would read the wrong arguments. The game enums (<c>EAnimBase</c> / <c>EAnimUpper</c> /
/// <c>EAnimAddi</c> / <c>EAnimLayer</c>, all <c>int</c>-based) arrive as <c>int</c> and are re-typed only for a kept call.
/// Every signature is one <see cref="Il2CppPatchSafety"/> allows (enums, floats, bools, an int, a string and a BY-VALUE
/// blittable 8-byte <c>Vector2</c>, which Il2CppInterop passes as the struct itself — byte-checked on TEST, qa M-11).</para>
/// <para><b>Static state — the HarmonyX exception</b> (as <see cref="TimeScalePatch"/>): a patch method is called statically,
/// so the gate, the main thread, the replay targets and the entry counter are reached through these fields, written on the
/// main thread (install, each freeze press).</para></summary>
internal static class AnimGatePatch
{
    /// <summary>The gated entry points: name, prefix, the layer (null = the call's own <c>EAnimLayer</c> argument) and the
    /// exact interop signature (return, then parameters) a prefix needs.</summary>
    internal static readonly (string Name, string Prefix, AnimRequestGate.Layer? Layer, NativeSignature Expected)[] Entries =
    {
        ("playBaseState", nameof(BaseState), AnimRequestGate.Layer.Base, StateSignature("Panda.ZAnim.EAnimBase")),
        ("playUpperState", nameof(UpperState), AnimRequestGate.Layer.Upper, StateSignature("Panda.ZAnim.EAnimUpper")),
        ("playAdditiveState", nameof(AdditiveState), AnimRequestGate.Layer.Additive,
            new NativeSignature("System.Void", new[] { "Panda.ZAnim.EAnimAddi", "System.Single", "UnityEngine.Vector2", "System.Single" })),
        ("playManualClip", nameof(ManualClip), null, new NativeSignature("System.Void", new[]
        {
            "System.String", "Panda.ZAnim.EAnimLayer", "System.Single", "System.Single", "System.Single", "System.Boolean",
            "System.Boolean", "System.Int32", "System.Single", "System.Boolean",
        })),
    };

    private static AnimRequestGate? s_gate;
    private static int s_mainThread;
    private static readonly MethodInfo?[] s_methods = new MethodInfo?[4];
    private static readonly Type?[] s_enums = new Type?[4];
    private static long s_calls;

    /// <summary>Calls of the gated entry points since install, frozen or not (always counted — the freeze summary's real-rate
    /// figure).</summary>
    internal static long Calls => s_calls;

    /// <summary>The thread the freeze was pressed on (Unity's main thread); calls from any other thread always run.</summary>
    internal static int MainThread { set => s_mainThread = value; }

    private static NativeSignature StateSignature(string state) => new("System.Void", new[]
    {
        state, "System.Single", "UnityEngine.Vector2", "System.Single", "System.Single", "System.Boolean", "System.Boolean",
    });

    /// <summary>Patches each entry point of <paramref name="controller"/> whose signature matches; the others are warned and
    /// left ungated. Returns how many are live.</summary>
    internal static int Install(HarmonyGameMethodHooker hooker, Type controller, AnimRequestGate gate, Action<string> warn)
    {
        s_gate = gate;
        var live = 0;
        for (var i = 0; i < Entries.Length; i++)
        {
            var (name, prefix, _, expected) = Entries[i];
            var m = Find(controller, name, expected);
            if (m is null) { warn($"animation gate: {name} {expected} not found — that request is not held while frozen"); continue; }
            s_methods[i] = m;
            s_enums[i] = m.GetParameters()[i == 3 ? 1 : 0].ParameterType;
            var p = typeof(AnimGatePatch).GetMethod(prefix, BindingFlags.Static | BindingFlags.NonPublic);
            if (p is not null && hooker.PrefixWith(m, p)) live++;
            else s_methods[i] = null;
        }
        return live;
    }

    private static MethodInfo? Find(Type controller, string name, NativeSignature expected)
    {
        foreach (var m in controller.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            if (m.Name == name && expected.Matches(m)) return m;
        return null;
    }

    // ---- the prefixes: true = the game's call runs now. Each never throws into the native caller. ----

#pragma warning disable STELLAR0003 // HarmonyX patch methods: the parameter list IS the game method's (by-position __N injection); no parameter object is possible.
    private static bool BaseState(Il2CppObjectBase __instance, int __0, float __1, Vector2 __2, float __3, float __4, bool __5, bool __6)
    {
        s_calls++;
        if (s_gate is not { Armed: true } g || !Defers(g, __instance, AnimRequestGate.Layer.Base, out var ctl)) return true;
        return Keep(0, ctl, __instance, AnimRequestGate.Layer.Base, new object?[] { Enum(0, __0), __1, __2, __3, __4, __5, __6 });
    }

    private static bool UpperState(Il2CppObjectBase __instance, int __0, float __1, Vector2 __2, float __3, float __4, bool __5, bool __6)
    {
        s_calls++;
        if (s_gate is not { Armed: true } g || !Defers(g, __instance, AnimRequestGate.Layer.Upper, out var ctl)) return true;
        return Keep(1, ctl, __instance, AnimRequestGate.Layer.Upper, new object?[] { Enum(1, __0), __1, __2, __3, __4, __5, __6 });
    }

    private static bool AdditiveState(Il2CppObjectBase __instance, int __0, float __1, Vector2 __2, float __3)
    {
        s_calls++;
        if (s_gate is not { Armed: true } g || !Defers(g, __instance, AnimRequestGate.Layer.Additive, out var ctl)) return true;
        return Keep(2, ctl, __instance, AnimRequestGate.Layer.Additive, new object?[] { Enum(2, __0), __1, __2, __3 });
    }

    private static bool ManualClip(Il2CppObjectBase __instance, string __0, int __1, float __2, float __3, float __4, bool __5,
        bool __6, int __7, float __8, bool __9)
    {
        s_calls++;
        if (s_gate is not { Armed: true } g) return true;
        var layer = (AnimRequestGate.Layer)Math.Clamp(__1, 0, 2);   // the call's own EAnimLayer, read as its int
        if (!Defers(g, __instance, layer, out var ctl)) return true;
        return Keep(3, ctl, __instance, layer, new object?[] { __0, Enum(3, __1), __2, __3, __4, __5, __6, __7, __8, __9 });
    }
#pragma warning restore STELLAR0003

    private static bool Defers(AnimRequestGate g, Il2CppObjectBase? instance, AnimRequestGate.Layer layer, out nint controller)
    {
        controller = instance?.Pointer ?? IntPtr.Zero;
        var thread = Environment.CurrentManagedThreadId;
        return g.Defers(controller, layer, thread == 0 || thread != s_mainThread);
    }

    // Only reached for a call that is deferred: its caller builds the argument copy after Defers said yes, never on the
    // pass-through path (no lambda — a closure over the prefix's parameters would be allocated at method entry, every call).
    // A failure lets the game's call run.
    private static bool Keep(int entry, nint controller, Il2CppObjectBase instance, AnimRequestGate.Layer layer, object?[] args)
    {
        try
        {
            if (s_methods[entry] is not { } method || s_gate is not { } g) return true;
            g.Keep(controller, instance, layer, method, args);
            return false;
        }
        catch { return true; }
    }

    private static object Enum(int entry, int value) => s_enums[entry] is { IsEnum: true } t ? System.Enum.ToObject(t, value) : value;
}
