using System;
using BepInEx.Unity.IL2CPP;
using Il2CppInterop.Runtime.Injection;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Owns the <see cref="StellarTicker"/> component: registers the injected type, attaches it to the
/// BepInEx manager GameObject, wires the tick callback, and re-rates it when the user changes the
/// Update Rate setting. Mirrors <c>UnityOverlayHost</c>.
/// <para>While the game's clock is paused (the scene freeze, <see cref="SetUnscaled"/>) the scheduled ticker cannot fire, so
/// a second, otherwise-disabled component (<see cref="StellarPausedTicker"/>) drives the same tick from real time at the
/// same rate; <see cref="TickPacer"/> keeps one dt and never two ticks in a frame across the switch.</para>
/// </summary>
internal sealed class UnityTickHost
{
    private readonly BasePlugin _plugin;
    private readonly IPluginLog _log;
    private readonly TickPacer _pacer = new();
    private StellarTicker? _ticker;
    private StellarPausedTicker? _paused;
    private Action<float>? _onTick;
    private Action? _pausedFrame;
    private int _rateHz = PerfControls.UpdateRateHz;

    public UnityTickHost(BasePlugin plugin, IPluginLog log) { _plugin = plugin; _log = log; }

    /// <summary>Install the throttled tick. <paramref name="onTick"/> receives seconds since the last tick.</summary>
    public void Install(Action<float> onTick)
    {
        // Repair Il2CppInterop's Class::Init resolution before the framework's FIRST type injection,
        // so builds whose GameAssembly.dll defeats FindClassInit's signature scan (e.g. StarSEA_STEAM)
        // don't fatally crash here. No-op when already resolved. See Il2CppClassInitFix.
        Il2CppClassInitFix.EnsureSeeded(_log);
        _onTick = onTick;
        StellarTicker.OnStart = () => _pacer.Start(Time.realtimeSinceStartup);
        StellarTicker.OnTick = FireScheduled;
        StellarTicker.OnError = m => _log.Error($"[Ticker] tick threw: {m}");
        try { ClassInjector.RegisterTypeInIl2Cpp<StellarTicker>(); }
        catch (Exception ex) { _log.Debug($"[Ticker] RegisterTypeInIl2Cpp: {ex.Message}"); }
        _ticker = _plugin.AddComponent<StellarTicker>();
        _log.Info($"[Ticker] installed — framework tick on InvokeRepeating @ {PerfControls.UpdateRateHz} Hz " +
                  "(per-frame managed entry eliminated)");
    }

    /// <summary>Re-rate the live ticker to an explicit Hz. Idempotent (no-op when unchanged). Driven both by
    /// TickScheduler.MasterRateChanged and by the host's per-tick safety reconcile.</summary>
    public void Reschedule(int hz)
    {
        _rateHz = hz;
        _ticker?.RescheduleTo(hz);
    }

    /// <summary>The game's clock stopped (true) or runs again (false): the unscaled driver takes the tick while stopped.
    /// <paramref name="pausedFrame"/> runs on every paused frame AFTER that frame's tick had its chance to run (the pause
    /// watchdog's stall check — checked before the tick, the first frame after a long hitch read as a stall, qa M-8);
    /// <paramref name="gone"/> if the driver is destroyed.</summary>
    public void SetUnscaled(bool on, Action? pausedFrame = null, Action? gone = null)
    {
        _pacer.Unscaled = on;
        _pausedFrame = on ? pausedFrame : null;
        var d = on ? EnsurePaused(gone) : _paused;
        if (d != null) d.enabled = on;
    }

    private void FireScheduled()
    {
        if (_pacer.TryScheduled(Time.realtimeSinceStartup, Time.frameCount, out var dt)) _onTick?.Invoke(dt);
    }

    private void FirePaused()
    {
        if (_pacer.TryUnscaled(Time.realtimeSinceStartup, Time.frameCount, _rateHz, out var dt))
        {
            try { _onTick?.Invoke(dt); }
            catch (Exception ex) { _log.Error($"[Ticker] tick threw: {ex.Message}"); }
        }
        try { _pausedFrame?.Invoke(); }
        catch (Exception ex) { _log.Error($"[Ticker] paused-frame check threw: {ex.Message}"); }
    }

    private StellarPausedTicker? EnsurePaused(Action? gone)
    {
        if (_paused == null)
        {
            try { ClassInjector.RegisterTypeInIl2Cpp<StellarPausedTicker>(); }
            catch (Exception ex) { _log.Debug($"[Ticker] RegisterTypeInIl2Cpp(StellarPausedTicker): {ex.Message}"); }
            _paused = _plugin.AddComponent<StellarPausedTicker>();
            if (_paused == null) return null;
            _paused.OnFrame = FirePaused;
        }
        _paused.OnGone = gone;
        return _paused;
    }
}
