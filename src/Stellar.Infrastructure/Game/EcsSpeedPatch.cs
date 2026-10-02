using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>The interception behind <see cref="EcsSpeedGate"/>: the ECS animator's four speed writers on
/// <c>ECSModel.ECSModelResourceManager</c> (release_3.7 interop + ISIL; native caller counts in brackets) —
/// <c>SetAnimatorLayerData(uint uid, int layer, float speed, float weight)</c> [9], <c>PlayState(uid, ushort layer,
/// uint hash, ref float2, float normalizedTime, float fade, float speed, float weight, …)</c> [11],
/// <c>PlayClip(uid, ushort layer, clip, float fade, float normalizedTime, float speed, float weight, …)</c> [3] and
/// <c>PlayDynamicState(uid, ushort layer, state, float2, float normalizedTime, float fade, float speed, float weight, …)</c>
/// [1]. Every one is static and takes the ECS uid first. Never skips the original (the play still happens, frozen at its
/// first frame). A method that does not resolve or install is skipped and logged; the others still install. Never
/// <c>tryCalculateAnimSpeed</c> (R7-6).
/// <para><b>Two interception paths (freeze-crash root cause 2026-10-02, framework e4f925b).</b> Only
/// <c>SetAnimatorLayerData</c> — primitives only — is a HarmonyX prefix (a dedicated static method taking the uid, the
/// layer, <c>ref</c> the speed and the weight by position: no <c>__args</c>, no boxing). The three PLAY writers take an
/// IL2CPP struct (<c>ExternalBlobPtr&lt;T&gt;</c>, 8 bytes, by value; <c>PlayState</c>'s <c>in float2</c>) that
/// Il2CppInterop's HarmonyX trampoline mis-marshals: it reads a by-value struct argument as a POINTER to the struct, while
/// the Win64 ABI passes an 8-byte struct by value in a register — so the game's original <c>PlayClip</c> received the first
/// 8 bytes of the clip blob as its clip pointer and faulted (<c>c0000005</c> inside <c>PlayClip</c>+0xB67, called through
/// the trampoline; the owner's MAIN client died ~1 s after the freeze press). They are native detours with the exact
/// native signature instead (<see cref="EcsPlayDetours"/>); <see cref="Il2CppPatchSafety"/> makes the hooker refuse any
/// such HarmonyX patch.</para>
/// <para><b>Static state — the HarmonyX exception</b> (as <see cref="DrawnSpeedPatch"/>): <c>s_gate</c>, written once at
/// install on the main thread, read-only afterwards, holding no game object.</para></summary>
internal static class EcsSpeedPatch
{
    internal const string ManagerType = "ECSModel.ECSModelResourceManager";

    private static EcsSpeedGate? s_gate;

    private static readonly (string Method, Type Layer, int SpeedAt)[] Targets =
    {
        ("SetAnimatorLayerData", typeof(int), 2),
        ("PlayState", typeof(ushort), 6),
        ("PlayClip", typeof(ushort), 5),
        ("PlayDynamicState", typeof(ushort), 6),
    };

    /// <summary>The writers patched through HarmonyX: only the primitive-only <c>SetAnimatorLayerData</c>.</summary>
    internal static readonly string[] HarmonyTargets = { "SetAnimatorLayerData" };

    /// <summary>The writers taking an IL2CPP struct argument: native detours, NEVER HarmonyX (freeze-crash 2026-10-02).</summary>
    internal static readonly string[] NativeTargets = { "PlayState", "PlayClip", "PlayDynamicState" };

    internal static List<string> Install(HarmonyGameMethodHooker hooker, Type manager, EcsSpeedGate gate, Action<string> warn)
    {
        s_gate = gate;
        var patched = new List<string>();
        var prefix = typeof(EcsSpeedPatch).GetMethod(nameof(LayerData), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is not null && Resolve(manager, HarmonyTargets[0]) is { } layerData && hooker.PrefixWith(layerData, prefix))
            patched.Add(HarmonyTargets[0]);
        foreach (var name in NativeTargets)
            if (Resolve(manager, name) is { } play && EcsPlayDetours.Install(name, play, warn)) patched.Add(name);
        return patched;
    }

    private static MethodInfo? Resolve(Type manager, string name) =>
        manager.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(m => FitsTarget(name, m));

    internal static bool FitsTarget(string name, MethodInfo m)
    {
        if (m.Name != name) return false;
        foreach (var t in Targets)
            if (t.Method == name) return Fits(m, t.Layer, t.SpeedAt);
        return false;
    }

    private static bool Fits(MethodInfo m, Type layer, int speedAt)
    {
        var ps = m.GetParameters();
        return ps.Length > speedAt + 1 && ps[0].ParameterType == typeof(uint) && ps[1].ParameterType == layer &&
               ps[speedAt].ParameterType == typeof(float) && ps[speedAt + 1].ParameterType == typeof(float);
    }

    // ---- the HarmonyX prefix (static, by position; never throws into the native caller) ----

    private static void LayerData(uint __0, int __1, ref float __2, float __3) => Ask(EcsSpeedGate.Writer.LayerData, __0, __1, ref __2, __3);

    /// <summary>The gate's question for one writer call — the HarmonyX prefix and the native play detours both ask it.
    /// Unarmed: a bare early-out (two field reads). Armed: the diagnostics-only call counter, then the gate.</summary>
    internal static void Ask(EcsSpeedGate.Writer writer, uint uid, int layer, ref float speed, float weight)
    {
        var gate = s_gate;
        if (gate is null || !gate.Armed) return;
        try
        {
            gate.CountCall(writer);
            gate.TrySubstitute(uid, layer, ref speed, weight, Environment.CurrentManagedThreadId);
        }
        catch { /* trust boundary */ }
    }
}
