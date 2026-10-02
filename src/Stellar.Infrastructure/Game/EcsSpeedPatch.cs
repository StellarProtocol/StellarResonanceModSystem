using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>The HarmonyX prefixes behind <see cref="EcsSpeedGate"/>: the ECS animator's four speed writers on
/// <c>ECSModel.ECSModelResourceManager</c> (release_3.7 interop + ISIL; native caller counts in brackets) —
/// <c>SetAnimatorLayerData(uint uid, int layer, float speed, float weight)</c> [9], <c>PlayState(uid, ushort layer,
/// uint hash, ref float2, float normalizedTime, float fade, float speed, float weight, …)</c> [11],
/// <c>PlayClip(uid, ushort layer, clip, float fade, float normalizedTime, float speed, float weight, …)</c> [3] and
/// <c>PlayDynamicState(uid, ushort layer, state, float2, float normalizedTime, float fade, float speed, float weight, …)</c>
/// [1]. Every one is static and takes the ECS uid first. Each prefix is a dedicated static method taking only the uid,
/// the layer, <c>ref</c> the speed and the weight by position — no <c>__args</c> array, no boxing; an unarmed gate returns
/// at once. Never skips the original (the play still happens, frozen at its first frame). A method that does not resolve
/// or patch is skipped and logged; the others still install. Never <c>tryCalculateAnimSpeed</c> (R7-6).
/// <para><b>Static state — the HarmonyX exception</b> (as <see cref="DrawnSpeedPatch"/>): <c>s_gate</c>, written once at
/// install on the main thread, read-only afterwards, holding no game object.</para></summary>
internal static class EcsSpeedPatch
{
    internal const string ManagerType = "ECSModel.ECSModelResourceManager";

    private static EcsSpeedGate? s_gate;

    /// <summary>(method, prefix, the exact layer type, speed index, writer): the layer is <c>int</c> on
    /// <c>SetAnimatorLayerData</c> and <c>ushort</c> on the three plays; the speed is at <c>__2</c> / <c>__6</c> / <c>__5</c> /
    /// <c>__6</c>, the weight right after it.</summary>
    private static readonly (string Method, string Prefix, Type Layer, int SpeedAt)[] Targets =
    {
        ("SetAnimatorLayerData", nameof(LayerData), typeof(int), 2),
        ("PlayState", nameof(PlayState), typeof(ushort), 6),
        ("PlayClip", nameof(PlayClip), typeof(ushort), 5),
        ("PlayDynamicState", nameof(PlayDynamic), typeof(ushort), 6),
    };

    /// <summary>Patches every target that resolves; returns the names patched.</summary>
    internal static List<string> Install(HarmonyGameMethodHooker hooker, Type manager, EcsSpeedGate gate)
    {
        s_gate = gate;
        var patched = new List<string>();
        foreach (var (name, prefixName, _, _) in Targets)
        {
            var prefix = typeof(EcsSpeedPatch).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic);
            var method = manager.GetMethods(BindingFlags.Static | BindingFlags.Public).FirstOrDefault(m => FitsTarget(name, m));
            if (prefix is not null && method is not null && hooker.PrefixWith(method, prefix)) patched.Add(name);
        }
        return patched;
    }

    /// <summary>True when <paramref name="m"/> is the target <paramref name="name"/> with exactly the shape its prefix binds
    /// by position: <c>uint</c> uid first, that target's own layer type second (review: <c>int</c> vs <c>ushort</c> is per
    /// method, never either), and by-value <c>float</c> speed + weight at the target's speed index.</summary>
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

    // ---- the prefixes (static, by position; never throw into the native caller) ----

    private static void LayerData(uint __0, int __1, ref float __2, float __3) => Ask(EcsSpeedGate.Writer.LayerData, __0, __1, ref __2, __3);

    private static void PlayState(uint __0, ushort __1, ref float __6, float __7) => Ask(EcsSpeedGate.Writer.PlayState, __0, __1, ref __6, __7);

    private static void PlayClip(uint __0, ushort __1, ref float __5, float __6) => Ask(EcsSpeedGate.Writer.PlayClip, __0, __1, ref __5, __6);

    private static void PlayDynamic(uint __0, ushort __1, ref float __6, float __7) => Ask(EcsSpeedGate.Writer.PlayDynamic, __0, __1, ref __6, __7);

    // Unarmed: a bare early-out (two field reads). Armed: the diagnostics-only call counter, then the gate.
    private static void Ask(EcsSpeedGate.Writer writer, uint uid, int layer, ref float speed, float weight)
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
