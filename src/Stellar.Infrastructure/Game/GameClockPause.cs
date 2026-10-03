using System;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Hooks;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>The scene freeze's global time pause on Unity's clock (<c>Time.timeScale</c>; the rules are
/// <see cref="ClockPauseState"/>'s). Held against the game's own writes by <see cref="TimeScalePatch"/>; watched by
/// <see cref="TimePauseWatchdog"/> at the framework's global rate (outside the world gate) and on every paused frame for a
/// stall, so a lost pause never leaves the game stopped. <see cref="Changed"/> tells the framework's tick host (switch to the
/// unscaled driver), the camera (Cut blends while paused) and the input shield (the pause block). Main thread.</summary>
internal sealed class GameClockPause : ITimePauseClock, IDisposable
{
    private const string Tag = "[FreeCam] ";

    private readonly ClockPauseState _state = new();
    private readonly IPluginLog _log;
    private bool _hooked;
    private bool _warnedBypass;

    public GameClockPause(IPluginLog log) => _log = log;

    /// <summary>Raised after the clock stops (true) or runs again (false).</summary>
    public event Action<bool>? Changed;

    public bool IsPaused => _state.Paused;

    /// <summary>The pause's book (diagnostics summary).</summary>
    internal ClockPauseState State => _state;

    /// <summary>True once the setter hook is in place.</summary>
    internal bool Hooked => _hooked;

    /// <summary>Installs the setter hook (first freeze). Without it the watchdog's per-tick check still re-asserts 0.</summary>
    public void InstallHook(HarmonyGameMethodHooker hooker)
    {
        try { _hooked = TimeScalePatch.Install(hooker, _state); }
        catch (Exception ex) { _log.Warning(Tag + "time-scale hook failed: " + ex.Message); }
        if (!_hooked) _log.Warning(Tag + "the game's own time-scale writes are caught only at the framework tick (setter not patched)");
    }

    /// <summary>Stops the clock (no-op when already paused).</summary>
    public void Pause()
    {
        if (!_state.Begin(Time.timeScale, Time.realtimeSinceStartup)) return;
        Time.timeScale = 0f;   // a write of 0 always passes the hook
        Raise(true);
    }

    /// <summary>Runs the clock again at the value the game last wanted, else the saved one (no-op when not paused).</summary>
    public void Resume()
    {
        if (_state.End() is not float restore) return;
        Time.timeScale = restore;   // no longer paused: the hook lets it through
        Raise(false);
    }

    /// <summary>The watchdog at one framework tick: beats, re-asserts 0 on a drift behind the hook (warned once) and answers
    /// the verdict — <see cref="TimePauseWatchdog"/> acts on a lost pause or a pause outside the world.</summary>
    public ClockPauseState.WatchVerdict Watch(bool holderFrozen, bool worldActive)
    {
        var verdict = _state.Watch(holderFrozen, worldActive, Time.timeScale, Time.realtimeSinceStartup);
        if (verdict != ClockPauseState.WatchVerdict.Reasserted) return verdict;
        Time.timeScale = 0f;
        if (!_warnedBypass) { _warnedBypass = true; _log.Warning(Tag + "the clock was set behind the time-scale hook while paused; paused again"); }
        return verdict;
    }

    /// <summary>One paused frame's stall check (after that frame's tick): true when the framework tick stopped beating
    /// (<see cref="ClockPauseState.Stalled"/>) — <see cref="TimePauseWatchdog"/> then releases the whole freeze.</summary>
    public bool Stalled() => _state.Stalled(Time.realtimeSinceStartup);

    /// <summary>The framework is going away: never leave the game paused.</summary>
    public void Dispose() => Resume();

    private void Raise(bool paused)
    {
        try { Changed?.Invoke(paused); }
        catch (Exception ex) { _log.Warning(Tag + "a time-pause listener threw: " + ex.Message); }
    }
}
