using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.Startup;
namespace Stellar.Infrastructure.Hooks;

/// <summary>The native-detour path for a game method HarmonyX must not patch (<see cref="Il2CppPatchSafety"/>):
/// Il2CppInterop's <c>IDetourProvider</c> (BepInEx backs it with Dobby) on the method's compiled entry, with a caller-declared
/// delegate that IS the native signature — so every argument reaches the game untouched. The caller holds the returned
/// detour and both delegates for the process (a collected detour delegate is a native crash).</summary>
internal static class NativeDetour
{
    /// <summary>The game's compiled entry for a generated interop method: its <c>MethodInfo*</c> (the interop's
    /// <c>NativeMethodInfoPtr_…</c> field) → <c>methodPointer</c>, the struct's first field. 0 when unresolved.</summary>
    internal static nint EntryOf(MethodInfo method)
    {
        var field = Il2CppInteropUtils.GetIl2CppMethodInfoPointerFieldForGeneratedMethod(method);
        if (field?.GetValue(null) is not IntPtr info || info == IntPtr.Zero) return 0;
        return Marshal.ReadIntPtr(info);
    }

    /// <summary>Detours <paramref name="entry"/> to <paramref name="hook"/>. The trampoline to the original is written to
    /// <paramref name="original"/> BEFORE the detour goes live (a call landing right after Apply must find it — BepInEx's
    /// own CreateAndApply order; pass the static field holding it).</summary>
    internal static IDetour Apply<T>(nint entry, T hook, out T? original) where T : Delegate
    {
        var detour = Il2CppInteropRuntime.Instance.DetourProvider.Create(entry, hook);
        original = detour.GenerateTrampoline<T>();
        detour.Apply();
        return detour;
    }
}
