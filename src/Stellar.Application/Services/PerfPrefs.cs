using System;
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

    /// <summary>Set by the host so a global-rate change re-rates the master clock. May be null in tests.</summary>
    public Action<int>? OnGlobalRateChanged { get; set; }

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
            OnGlobalRateChanged?.Invoke(hz);
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

    private const string PluginRatePrefix = "plugin_rate.";
    private const string PluginSelfCtlPrefix = "plugin_selfcontrol.";
    private const string PluginSustainedPrefix = "plugin_sustained.";

    /// <summary>Set by the host to push a per-plugin change into the TickScheduler. (guid, staticRateHz?, allowSelfControl, allowSustained).</summary>
    public Action<string, int?, bool, bool>? OnPluginConfigChanged { get; set; }

    /// <summary>Per-plugin static rate in Hz; 0 = follow global.</summary>
    public int GetPluginRate(string guid) => _config.Get<int>(PluginRatePrefix + guid, 0);

    public void SetPluginRate(string guid, int hz)
    {
        var clamped = hz <= 0 ? 0 : PerfControls.ClampRate(hz);
        _config.Set(PluginRatePrefix + guid, clamped);
        _config.Save();
        Push(guid);
    }

    /// <summary>Whether the user has allowed this plugin to control its own rate (dynamic ramp).</summary>
    public bool GetPluginSelfControl(string guid) => _config.Get<bool>(PluginSelfCtlPrefix + guid, false);

    public void SetPluginSelfControl(string guid, bool allow)
    {
        _config.Set(PluginSelfCtlPrefix + guid, allow);
        _config.Save();
        Push(guid);
    }

    /// <summary>Whether the user has allowed this plugin to hold a dynamic rate indefinitely (no leak-guard auto-release).</summary>
    public bool GetPluginSustained(string guid) => _config.Get<bool>(PluginSustainedPrefix + guid, false);

    public void SetPluginSustained(string guid, bool sustained)
    {
        _config.Set(PluginSustainedPrefix + guid, sustained);
        _config.Save();
        Push(guid);
    }

    private void Push(string guid)
    {
        var rate = GetPluginRate(guid);
        OnPluginConfigChanged?.Invoke(guid, rate > 0 ? rate : (int?)null, GetPluginSelfControl(guid), GetPluginSustained(guid));
    }
}
