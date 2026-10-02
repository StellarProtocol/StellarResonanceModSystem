using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.Injection;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Rendering;

/// <summary>The game-apply signal on <c>QualityGradeSetting.ApplyAllData(in QualityData data, bool excludeFrameRate)</c> as a
/// NATIVE detour (patch-safety review 2026-10-02). It was a HarmonyX postfix, and Il2CppInterop's trampoline mis-marshals the
/// by-ref 96-byte <c>QualityData</c> — measured on TEST (CoreCLR 6.0.7): the game APPLIED a QualityData with bytes 8..95
/// zeroed (render scale, frame rate, shadows, limits, every toggle) and the caller's first 8 bytes (grade, resolution) came
/// back overwritten (<see cref="Il2CppPatchSafety"/>). Here the struct stays a pointer the detour never reads: the original
/// runs with every argument as received, THEN <c>applied</c> fires — the postfix's timing, so the re-assert still follows
/// the game's own apply. Native signature (Win64): <c>void(QualityData*, bool, MethodInfo*)</c>. Goes live only on the exact
/// interop signature <see cref="QualityApplySignature.Expected"/>.
/// <para><b>Static state — the hook exception</b> (as <c>EcsPlayDetours</c>): the original-trampoline delegate, the detour
/// handle and the callback, written once at install on the main thread, then read-only; holding the delegates keeps them
/// alive for the process.</para></summary>
internal static class QualityApplyDetour
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void ApplyAllDataFn(nint data, byte excludeFrameRate, nint method);

    private static ApplyAllDataFn? s_original;
    private static IDetour? s_detour;
    private static Action? s_applied;

    private static readonly ApplyAllDataFn s_hook = (data, excludeFrameRate, method) =>
    {
        s_original!(data, excludeFrameRate, method);
        try { s_applied?.Invoke(); }
        catch { /* trust boundary: never throw into the game's native caller */ }
    };

    internal static bool Installed => s_detour is not null;

    /// <summary>Detours <paramref name="method"/> (the exact-signature <c>ApplyAllData</c>) once. False (warned) when its
    /// native entry cannot be found or the detour fails; true when installed now or already.</summary>
    internal static bool Install(MethodInfo method, Action applied, Action<string> warn)
    {
        if (s_detour is not null) return true;
        try
        {
            if (NativeDetour.EntryOf(method) is not (not 0 and var entry)) { warn("no native entry for ApplyAllData"); return false; }
            s_applied = applied;
            s_detour = NativeDetour.Apply(entry, s_hook, out s_original);
            return true;
        }
        catch (Exception ex)
        {
            warn($"ApplyAllData detour failed: {ex.Message}");
            return false;
        }
    }
}
