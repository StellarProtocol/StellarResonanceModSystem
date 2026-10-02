using System.Reflection;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

/// <summary>The HarmonyX prefix on <c>UnityEngine.Time.set_timeScale(float)</c> that holds the scene freeze's time pause
/// against the game's own writes (hit-stop / slow motion — <c>ZTimeScaleShowInfo</c> / <c>ZCurveTimeScaleShowInfo.OnStop</c>
/// writes 1.0, <c>Magic.*TimeScaleBehaviour</c>; recon § Run 9). Event-driven: the prefix runs only when something writes
/// the clock (a handful of times per fight), never per frame. The value is a plain <c>float</c> BY VALUE — a signature
/// <see cref="Il2CppPatchSafety"/> allows (no by-ref, no IL2CPP struct). Installed once, lazily, on the first freeze.
/// <para><b>Static state — the HarmonyX exception</b> (as the hooker's own tables): a patch method is called statically,
/// so the pause's book is reached through this one field, written once at install on the main thread.</para></summary>
internal static class TimeScalePatch
{
    private static ClockPauseState? s_state;

    /// <summary>Patches the setter. False when it is missing or the patch failed (logged by the hooker).</summary>
    internal static bool Install(HarmonyGameMethodHooker hooker, ClockPauseState state)
    {
        var setter = typeof(UnityEngine.Time).GetProperty(nameof(UnityEngine.Time.timeScale), BindingFlags.Public | BindingFlags.Static)?.GetSetMethod();
        var prefix = typeof(TimeScalePatch).GetMethod(nameof(Prefix), BindingFlags.Static | BindingFlags.NonPublic);
        if (setter is null || prefix is null) return false;
        s_state = state;
        return hooker.PrefixWith(setter, prefix);
    }

    // __0 = the value being written. false = the write is held (the clock stays at 0). Never throws into the native caller.
    private static bool Prefix(float __0)
    {
        try { return s_state?.NoteGameWrite(__0) ?? true; }
        catch { return true; }   // trust boundary: a broken book never blocks the game's own write
    }
}
