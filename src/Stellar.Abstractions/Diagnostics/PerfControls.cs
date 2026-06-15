using System.Collections.Generic;

namespace Stellar.Abstractions.Diagnostics;

/// <summary>
/// Runtime bisect toggles for the overlay perf harness, mutated from the perf
/// overlay and read by <c>UiBuilderService.Tick</c> and <c>ThemeRenderer</c>.
/// Only meaningful while <see cref="PerfProbe.IsEnabled"/> — callers still read
/// these unconditionally, but the overlay that flips them only exists when the
/// harness is enabled, so production state stays at the inert defaults.
/// </summary>
public static class PerfControls
{
    /// <summary>Skip drawing every registered window (run with the menu closed
    /// to prove the draw is the cost and rule out menu-induced game changes).</summary>
    public static bool MasterHudKill { get; set; }

    /// <summary>Skip <c>ThemeRenderer.DrawWindowChrome</c> (isolate chrome cost).</summary>
    public static bool ChromeKill { get; set; }

    /// <summary>Render large framework background fills opaque (isolate blend/overdraw).</summary>
    public static bool ForceOpaque { get; set; }

    /// <summary>Draw window content only every Nth frame. 1 = every frame (off).</summary>
    public static int ThrottleN { get; set; } = 1;

    private static readonly HashSet<string> _muted = new();

    /// <summary>Returns true if the named window is currently muted (skipped) by the perf harness.</summary>
    public static bool IsMuted(string windowId) => _muted.Contains(windowId);

    /// <summary>Add or remove <paramref name="windowId"/> from the muted set; muted windows are skipped by <see cref="PerfControls.MasterHudKill"/> isolation tests.</summary>
    public static void SetMuted(string windowId, bool muted)
    {
        if (muted) _muted.Add(windowId);
        else _muted.Remove(windowId);
    }

    /// <summary>Snapshot of currently-muted window ids (overlay display).</summary>
    public static IReadOnlyCollection<string> MutedIds => _muted;

    // --- perf-experiment launch flags (resolved ONCE at startup) ---
    // Each flag is true when STELLAR_<NAME>=1 in the environment, OR when a
    // line "<NAME>" / "<NAME>=1" appears in a "stellar_perf.flags" file in the
    // game's working directory. The file lets an external A/B driver toggle the
    // experiment between launches by writing/removing the file — no Heroic env
    // edit, no per-launch env plumbing. Inert (all false) in normal play.

    /// <summary>Set <c>vSyncCount=0</c> + a very high <c>targetFrameRate</c> so the
    /// frame rate is CPU/GPU-bound rather than pinned to the display refresh —
    /// makes a per-frame CPU saving visible as an FPS delta instead of vsync-masked.
    /// Mutable so the Settings → Performance "Uncap Frame Rate" toggle re-applies vSync
    /// live (the host tick reconciles <c>QualitySettings.vSyncCount</c> to this value).</summary>
    public static bool Uncap { get; set; } = ResolveFlag("UNCAP");

    /// <summary>True when UNCAP came from an explicit env var / flags-file entry (a dev/measurement
    /// override), so the persisted user setting must NOT clobber it at boot.</summary>
    public static readonly bool UncapFromOverride = ResolveFlag("UNCAP");

    /// <summary>
    /// Framework tick rate in Hz — how many times per second Stellar runs its whole per-frame
    /// work (services, HUD animation, plugin Updates, window interaction, input). The tick is
    /// driven by an <c>InvokeRepeating</c> schedule at this rate instead of every render frame,
    /// so most frames have ZERO managed-runtime entry — that entry is the ~12-18 fps "managed
    /// crossing" tax measured on this BepInEx 6 IL2CPP stack (a frame with no managed entry costs
    /// nothing; cost is linear in entries/sec). Lower = more game FPS (near-vanilla at 30); higher
    /// = smoother/lower-latency Stellar UI but more FPS cost. Mutable so the Settings → Performance
    /// slider re-rates the live ticker. Default 30 (near-vanilla FPS, ~33 ms UI latency).
    /// <see cref="MaxUpdateRateHz"/> means "every frame" (Unity caps the schedule to the frame rate).
    /// </summary>
    public static int UpdateRateHz { get; set; } = ResolveRate();

    /// <summary>True when the rate came from an explicit env var / flags-file entry (a dev override),
    /// so the persisted user setting must NOT clobber it at boot.</summary>
    public static readonly bool RateFromOverride = RawRate() != null;

    /// <summary>Slider floor — below this the HUD/input feel unusably laggy.</summary>
    public const int MinUpdateRateHz = 10;
    /// <summary>Slider ceiling — treated as "every frame" (the per-frame, max-cost mode).</summary>
    public const int MaxUpdateRateHz = 240;
    /// <summary>Default tick rate (near-vanilla FPS while keeping the HUD/input smooth).</summary>
    public const int DefaultUpdateRateHz = 30;

    /// <summary>Clamp an arbitrary rate into the supported [<see cref="MinUpdateRateHz"/>,
    /// <see cref="MaxUpdateRateHz"/>] range. Shared by the env resolver and the Settings persistence.</summary>
    public static int ClampRate(int hz)
        => hz < MinUpdateRateHz ? MinUpdateRateHz : hz > MaxUpdateRateHz ? MaxUpdateRateHz : hz;

    // Raw rate value from env/flags (null if neither set) — used both to resolve the boot default
    // and to detect an explicit dev override (RateFromOverride).
    private static string? RawRate()
        => System.Environment.GetEnvironmentVariable("STELLAR_UPDATE_RATE") ?? FlagValue("UPDATE_RATE");

    private static int ResolveRate()
        => RawRate() is { } v && int.TryParse(v, out var hz) ? ClampRate(hz) : DefaultUpdateRateHz;

    /// <summary>Read a numeric value flag: <c>STELLAR_&lt;NAME&gt;=NN</c> in env, or a line
    /// "<c>NAME=NN</c>" in <c>stellar_perf.flags</c>. Returns null if absent.</summary>
    private static string? FlagValue(string name)
    {
        try
        {
            var path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "stellar_perf.flags");
            if (!System.IO.File.Exists(path)) return null;
            foreach (var raw in System.IO.File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.StartsWith(name + "=", System.StringComparison.OrdinalIgnoreCase))
                    return line.Substring(name.Length + 1).Trim();
            }
        }
        catch { /* optional */ }
        return null;
    }

    /// <summary>Resolve a launch flag the same way as the perf flags: <c>STELLAR_&lt;NAME&gt;=1</c> in the env,
    /// OR a line "<c>NAME</c>"/"<c>NAME=1</c>" in <c>game_mini/stellar_perf.flags</c>. Shared so other gates
    /// (e.g. <see cref="StellarDiagnostics"/>) read the deploy-script mode file without Heroic env edits.</summary>
    public static bool Flag(string name) => ResolveFlag(name);

    private static bool ResolveFlag(string name)
    {
        var env = System.Environment.GetEnvironmentVariable("STELLAR_" + name);
        if (env == "1" || (env != null && env.Equals("true", System.StringComparison.OrdinalIgnoreCase)))
            return true;
        try
        {
            var path = System.IO.Path.Combine(System.IO.Directory.GetCurrentDirectory(), "stellar_perf.flags");
            if (!System.IO.File.Exists(path)) return false;
            foreach (var raw in System.IO.File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Equals(name, System.StringComparison.OrdinalIgnoreCase) ||
                    line.Equals(name + "=1", System.StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* flags file is optional; absence/error means flag off */ }
        return false;
    }

    /// <summary>
    /// True when this frame should be SKIPPED under the throttle. Frame 0 and
    /// every Nth frame draw; the N-1 frames between are skipped. N&lt;=1 disables.
    /// </summary>
    public static bool IsThrottledOut(int frameCounter)
        => ThrottleN > 1 && (frameCounter % ThrottleN) != 0;

    internal static void ResetForTests()
    {
        MasterHudKill = false;
        ChromeKill = false;
        ForceOpaque = false;
        ThrottleN = 1;
        _muted.Clear();
    }
}
