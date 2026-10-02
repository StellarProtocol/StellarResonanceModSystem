using System;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Hooks;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>The scene freeze's global time pause on Unity's clock (<c>Time.timeScale</c>; the rules are
/// <see cref="ClockPauseState"/>'s). Held against the game's own writes by <see cref="TimeScalePatch"/>; watched every
/// framework tick (the tick runs on unscaled time while paused) and every paused frame for a stall, so a lost pause never
/// leaves the game stopped. <see cref="Changed"/> tells the framework's tick host (switch to the unscaled driver) and the
/// camera (Cut blends while paused). Main thread.</summary>
internal sealed class GameClockPause : IDisposable
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

    /// <summary>The watchdog at one framework tick. True = the world is gone: the caller releases the scene (which resumes).</summary>
    public bool Watch(bool holderFrozen, bool worldActive)
    {
        switch (_state.Watch(holderFrozen, worldActive, Time.timeScale, Time.realtimeSinceStartup))
        {
            case ClockPauseState.WatchVerdict.Reasserted:
                Time.timeScale = 0f;
                if (!_warnedBypass) { _warnedBypass = true; _log.Warning(Tag + "the clock was set behind the time-scale hook while paused; paused again"); }
                return false;
            case ClockPauseState.WatchVerdict.LostPause:
                _log.Warning(Tag + "watchdog: the time pause outlived its freeze; resuming the game");
                Resume();
                return false;
            case ClockPauseState.WatchVerdict.LeftWorld:
                _log.Warning(Tag + "watchdog: paused outside the world; releasing the scene freeze");
                return true;
            default:
                return false;
        }
    }

    /// <summary>Every paused frame (the unscaled driver): resumes on its own when the framework tick stopped beating.</summary>
    public void CheckStall()
    {
        if (!_state.Stalled(Time.realtimeSinceStartup)) return;
        _log.Warning($"{Tag}watchdog: no framework tick for {ClockPauseState.StallSeconds:F0} s while paused; resuming the game");
        Resume();
    }

    /// <summary>The framework is going away: never leave the game paused.</summary>
    public void Dispose() => Resume();

    private void Raise(bool paused)
    {
        try { Changed?.Invoke(paused); }
        catch (Exception ex) { _log.Warning(Tag + "a time-pause listener threw: " + ex.Message); }
    }
}
