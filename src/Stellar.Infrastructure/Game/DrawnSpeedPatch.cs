using System;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>The HarmonyX prefix on <c>Panda.ZGame.AnimCompBase.set_Speed(float)</c> (recon run 7 R7-5: 4 native callers,
/// non-virtual, armed 244 frames without trouble) that hands every game write to the freeze's
/// <see cref="DrawnSpeedGate"/>. A dedicated static prefix taking the value by position (<c>ref float __0</c>) — no
/// <c>__args</c> array, no boxing; an unarmed gate returns at once. Installed once, lazily, on the first freeze (with the
/// freeze's other hooks). Never patch <c>tryCalculateAnimSpeed</c> instead: it hung the main thread (R7-6).
/// <para><b>Static state — the HarmonyX exception</b> (perf review): <c>s_gate</c> is static mutable state outside Host,
/// which the coding standards otherwise forbid. HarmonyX calls a patch method statically with no instance to carry state,
/// and the shared <see cref="HarmonyGameMethodHooker"/> trampoline (the usual way round that) costs a per-call
/// <c>__args</c> array on this hot setter. So the one gate is reached through this one field: written once at install on
/// the main thread, read-only afterwards, holding no game object. The same reason keeps the hooker's own callback tables
/// static.</para></summary>
internal static class DrawnSpeedPatch
{
    internal const string AnimCompType = "Panda.ZGame.AnimCompBase";

    // Harmony prefixes are static, so the gate is reached through this one field, written once at install (main thread).
    private static DrawnSpeedGate? s_gate;

    /// <summary>Patches <c>set_Speed</c> on <paramref name="animComp"/>. False when the setter is missing or the patch failed.</summary>
    internal static bool Install(HarmonyGameMethodHooker hooker, Type animComp, DrawnSpeedGate gate)
    {
        var setter = animComp.GetProperty("Speed", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)?.GetSetMethod(true);
        var prefix = typeof(DrawnSpeedPatch).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic);
        if (setter is null || prefix is null) return false;
        s_gate = gate;
        return hooker.PrefixWith(setter, prefix);
    }

    // __instance = the AnimCompBase wrapper; __0 = the speed the game is writing. Never throws into the native caller.
    private static void Prefix(object __instance, ref float __0)
    {
        var gate = s_gate;
        if (gate is null) return;
        gate.CountCall();   // every entry, armed or not: the real set_Speed rate for the gated summary line
        if (!gate.Armed || gate.OwnWrite) return;
        try
        {
            if (__instance is Il2CppObjectBase comp) gate.TrySubstitute(comp.Pointer, ref __0, Environment.CurrentManagedThreadId);
        }
        catch
        {
            // Trust boundary: a managed exception must not propagate back into the IL2CPP caller.
        }
    }
}
