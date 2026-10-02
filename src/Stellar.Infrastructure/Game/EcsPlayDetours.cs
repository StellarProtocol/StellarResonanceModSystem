using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Startup;
namespace Stellar.Infrastructure.Game;

/// <summary>Native detours on the ECS animator's three PLAY writers (<see cref="EcsSpeedPatch"/>), declared with their exact
/// native (IL2CPP, Win64) signatures so every argument reaches the game untouched — the freeze-crash root cause 2026-10-02
/// (framework e4f925b): the HarmonyX/Il2CppInterop trampoline handed the original <c>PlayClip</c> the first 8 bytes of the
/// clip blob as its <c>ExternalBlobPtr</c> (by-value 8-byte struct read as a pointer) and the game faulted inside
/// <c>PlayClip</c>. Here the struct arguments are plain machine words: <c>ExternalBlobPtr&lt;T&gt;</c> = its one pointer field
/// (<c>nint</c>), <c>PlayDynamicState</c>'s by-value <c>float2</c> = the 8 bytes the caller packs into one integer register
/// (<c>long</c>), <c>PlayState</c>'s <c>in float2</c> = a pointer (<c>nint</c>); the trailing <c>nint</c> is IL2CPP's hidden
/// <c>MethodInfo*</c>. Argument order and slots read from the release_3.7 ISIL of the game's own caller
/// (<c>Panda.ZGame.ECSAnimState.Play</c>). The detour only asks <see cref="EcsSpeedPatch.Ask"/> about the speed, then calls
/// the original with every other argument as received.
/// <para>The detour bodies are lambdas over the native delegate types (their parameter count is the native ABI's, not a
/// design choice). <b>Static state — the hook exception</b> (as <see cref="EcsSpeedPatch"/>): the three original-trampoline
/// delegates and detour handles (Il2CppInterop's <c>IDetourProvider</c>, which BepInEx backs with its native detour), written once at install on the main thread, then read-only; holding them keeps both
/// delegates alive for the process (a collected detour delegate is a native crash).</para></summary>
internal static class EcsPlayDetours
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint PlayStateFn(uint uid, ushort layer, uint stateHash, nint parameters, float normalizedTime,
        float fade, float speed, float weight, int avatarMask, float normalizedEndTime, nint method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint PlayClipFn(uint uid, ushort layer, nint clip, float fade, float normalizedTime, float speed,
        float weight, int avatarMask, float normalizedEndTime, nint method);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint PlayDynamicFn(uint uid, ushort layer, nint state, long parameters, float normalizedTime, float fade,
        float speed, float weight, float normalizedEndTime, nint method);

    private static PlayStateFn? s_stateOriginal;
    private static PlayClipFn? s_clipOriginal;
    private static PlayDynamicFn? s_dynamicOriginal;
    private static readonly IDetour?[] s_detours = new IDetour?[3];

    private static readonly PlayStateFn s_state = (uid, layer, hash, parameters, time, fade, speed, weight, mask, end, method) =>
    {
        EcsSpeedPatch.Ask(EcsSpeedGate.Writer.PlayState, uid, layer, ref speed, weight);
        return s_stateOriginal!(uid, layer, hash, parameters, time, fade, speed, weight, mask, end, method);
    };

    private static readonly PlayClipFn s_clip = (uid, layer, clip, fade, time, speed, weight, mask, end, method) =>
    {
        EcsSpeedPatch.Ask(EcsSpeedGate.Writer.PlayClip, uid, layer, ref speed, weight);
        return s_clipOriginal!(uid, layer, clip, fade, time, speed, weight, mask, end, method);
    };

    private static readonly PlayDynamicFn s_dynamic = (uid, layer, state, parameters, time, fade, speed, weight, end, method) =>
    {
        EcsSpeedPatch.Ask(EcsSpeedGate.Writer.PlayDynamic, uid, layer, ref speed, weight);
        return s_dynamicOriginal!(uid, layer, state, parameters, time, fade, speed, weight, end, method);
    };

    /// <summary>Detours <paramref name="method"/> (one of <see cref="EcsSpeedPatch.NativeTargets"/>) once. False (warned)
    /// when its native entry cannot be found or the detour fails; true when installed now or already.</summary>
    internal static bool Install(string name, MethodInfo method, Action<string> warn)
    {
        var slot = Array.IndexOf(EcsSpeedPatch.NativeTargets, name);
        if (slot < 0) return false;
        if (s_detours[slot] is not null) return true;
        try
        {
            if (NativeEntry(method) is not (not 0 and var entry)) { warn($"no native entry for ECS {name}"); return false; }
            var provider = Il2CppInteropRuntime.Instance.DetourProvider;   // BepInEx's native (Dobby) provider
            // The trampoline to the original is kept BEFORE the detour goes live: a call landing right after Apply must
            // find it (BepInEx's own CreateAndApply order).
            var detour = slot switch
            {
                0 => Prepared(provider.Create(entry, s_state), out s_stateOriginal),
                1 => Prepared(provider.Create(entry, s_clip), out s_clipOriginal),
                _ => Prepared(provider.Create(entry, s_dynamic), out s_dynamicOriginal),
            };
            detour.Apply();
            s_detours[slot] = detour;
            return true;
        }
        catch (Exception ex)
        {
            warn($"ECS {name} detour failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Generates <paramref name="detour"/>'s trampoline to the original into <paramref name="original"/>.</summary>
    private static IDetour Prepared<T>(IDetour detour, out T? original) where T : Delegate
    {
        original = detour.GenerateTrampoline<T>();
        return detour;
    }

    /// <summary>The game's compiled entry for a generated interop method: its <c>MethodInfo*</c> (the interop's
    /// <c>NativeMethodInfoPtr_…</c> field) → <c>methodPointer</c>, the struct's first field. 0 when unresolved.</summary>
    private static nint NativeEntry(MethodInfo method)
    {
        var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
        if (field?.GetValue(null) is not IntPtr info || info == IntPtr.Zero) return 0;
        return Marshal.ReadIntPtr(info);
    }
}
