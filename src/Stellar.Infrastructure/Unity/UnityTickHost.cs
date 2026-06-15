using System;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Owns the <see cref="StellarTicker"/> component: registers the injected type, attaches it to the
/// BepInEx manager GameObject, wires the tick callback, and re-rates it when the user changes the
/// Update Rate setting. Mirrors <c>UnityOverlayHost</c>.
/// </summary>
internal sealed class UnityTickHost
{
    private readonly BasePlugin _plugin;
    private readonly IPluginLog _log;
    private StellarTicker? _ticker;

    public UnityTickHost(BasePlugin plugin, IPluginLog log) { _plugin = plugin; _log = log; }

    /// <summary>Install the throttled tick. <paramref name="onTick"/> receives seconds since the last tick.</summary>
    public void Install(Action<float> onTick)
    {
        StellarTicker.OnTick = onTick;
        StellarTicker.OnError = m => _log.Error($"[Ticker] tick threw: {m}");
        try { ClassInjector.RegisterTypeInIl2Cpp<StellarTicker>(); }
        catch (Exception ex) { _log.Debug($"[Ticker] RegisterTypeInIl2Cpp: {ex.Message}"); }
        _ticker = _plugin.AddComponent<StellarTicker>();
        _log.Info($"[Ticker] installed — framework tick on InvokeRepeating @ {PerfControls.UpdateRateHz} Hz " +
                  "(per-frame managed entry eliminated)");
    }

    /// <summary>Apply a changed <see cref="PerfControls.UpdateRateHz"/> to the live ticker.</summary>
    public void Reschedule() => _ticker?.Reschedule();
}
