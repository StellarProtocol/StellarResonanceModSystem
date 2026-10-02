using System;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game;

/// <summary>The counted twin of <see cref="DrawnSpeedPatch"/>'s prefix (combat-freeze evidence capture, diagnostics only):
/// installed INSTEAD of the plain prefix only when diagnostics are on and the backend handed over its counters — with
/// diagnostics off the plain prefix is installed and nothing here runs. Same decision (it calls
/// <see cref="DrawnSpeedGate.TrySubstituteCounted"/>, which delegates the decision to <see cref="DrawnSpeedGate.TrySubstitute"/>),
/// plus per-entity counts of every substitution and every passthrough by reason (untracked component, excluded entity,
/// our own write, off the main thread).</summary>
internal static partial class DrawnSpeedPatch
{
    // Written once on the main thread before Install (diagnostics only); read-only afterwards. HarmonyX static exception.
    private static FreezeDiagCounters? s_counters;

    /// <summary>Hands the backend's diagnostic counters to the prefix; call before <see cref="Install"/>.</summary>
    internal static void UseCounters(FreezeDiagCounters counters)
    {
        if (StellarDiagnostics.IsEnabled) s_counters = counters;
    }

    static partial void ChoosePrefix(ref string name)
    {
        if (StellarDiagnostics.IsEnabled && s_counters is not null) name = nameof(PrefixCounted);
    }

    private static void PrefixCounted(object __instance, ref float __0)
    {
        var gate = s_gate;
        if (gate is null) return;
        gate.CountCall();
        if (!gate.Armed) return;
        try
        {
            if (__instance is not Il2CppObjectBase comp) return;
            var thread = Environment.CurrentManagedThreadId;
            if (s_counters is { } c) gate.TrySubstituteCounted(comp.Pointer, ref __0, thread, c);
            else if (!gate.OwnWrite) gate.TrySubstitute(comp.Pointer, ref __0, thread);
        }
        catch
        {
            // Trust boundary: a managed exception must not propagate back into the IL2CPP caller.
        }
    }
}
