using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// Persists the Settings → Performance preferences — the framework tick rate
/// (<see cref="PerfControls.UpdateRateHz"/>) and the frame-rate uncap
/// (<see cref="PerfControls.Uncap"/>) — through the framework config service.
/// Mirrors <see cref="LauncherPrefs"/>: a single <see cref="IConfigSection"/>,
/// round-tripping primitives.
///
/// <para>This type only owns the <em>value</em> (config ↔ <see cref="PerfControls"/>).
/// Applying a changed value to the live runtime — rescheduling the ticker and
/// re-applying V-Sync — is the host's job: the host tick reconciles the live
/// state to <see cref="PerfControls"/> each tick, so a setter here just needs to
/// update <see cref="PerfControls"/> + persist. Keeps this layer free of any
/// Unity/Infrastructure dependency.</para>
/// </summary>
internal sealed class PerfPrefs
{
    private const string RateKey = "update_rate_hz";
    private const string UncapKey = "uncap_framerate";

    private readonly IConfigSection _config;

    public PerfPrefs(IConfigSection config)
    {
        _config = config;

        // The persisted user choice loads at boot UNLESS an explicit env/flags override is present
        // (a dev/measurement override wins — the perf deploy mode sets these via stellar_perf.flags).
        if (!PerfControls.RateFromOverride)
            PerfControls.UpdateRateHz = PerfControls.ClampRate(config.Get(RateKey, PerfControls.UpdateRateHz));
        if (!PerfControls.UncapFromOverride)
            PerfControls.Uncap = config.Get(UncapKey, PerfControls.Uncap);
    }

    /// <summary>Framework tick rate in Hz (clamped to the supported range). Persisted on change;
    /// the host tick re-rates the live ticker.</summary>
    public int UpdateRateHz
    {
        get => PerfControls.UpdateRateHz;
        set
        {
            var hz = PerfControls.ClampRate(value);
            if (hz == PerfControls.UpdateRateHz) return;
            PerfControls.UpdateRateHz = hz;
            _config.Set(RateKey, hz);
            _config.Save();
        }
    }

    /// <summary>Whether the game's frame-rate cap (V-Sync + FPS limit) is removed. Persisted on change;
    /// the host tick re-applies/restores V-Sync.</summary>
    public bool Uncap
    {
        get => PerfControls.Uncap;
        set
        {
            if (value == PerfControls.Uncap) return;
            PerfControls.Uncap = value;
            _config.Set(UncapKey, value);
            _config.Save();
        }
    }
}
