using System;
using Stellar.Abstractions.Diagnostics;
namespace Stellar.Infrastructure.Game;

/// <summary>StellarDiagnostics-gated logging for the freeze backend: one line on freeze and one summary on unfreeze —
/// the clock saved / restored, how many game time-scale writes the hook held (and drifts the watchdog caught behind it),
/// how many entities the position hold pinned, the animation requests held, replayed and dropped as stale, the gated
/// entry points' calls (always counted: during this freeze and since install — the real rate on a busy scene), and the deferred removals
/// (deferred / replayed / stale).</summary>
internal sealed partial class GameFreezeBackend
{
    private long _frozenAtMs;
    private int _heldAtUnfreeze, _animDeferredAtUnfreeze;
    private long _animCallsAtFreeze;

    partial void OnFrozen()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _frozenAtMs = Environment.TickCount64;
        _animCallsAtFreeze = AnimGatePatch.Calls;
        var s = _clock.State;
        _log.Info($"[FreeCam] freeze on: timeScale saved={s.Saved:F2} paused={_clock.IsPaused} hook={(_clock.Hooked ? "on" : "OFF")} " +
                  $"entities={_ids.Count} positionsHeld={_held.Count} animTracked={_anim.Tracked} self={_ledger.Self} deferRemovals={(_removeEntity is not null ? "on" : "OFF")} " +
                  $"animGate={(_animLive > 0 ? $"on({_animLive})" : "OFF")}");
    }

    partial void OnUnfreezing()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _heldAtUnfreeze = _held.Count;
        _animDeferredAtUnfreeze = _anim.Deferred;
    }

    partial void OnUnfrozen()
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var s = _clock.State;
        _log.Info($"[FreeCam] freeze off: timeScale restored={UnityEngine.Time.timeScale:F2} saved={s.Saved:F2} " +
                  $"wanted={(s.Wanted is float w ? w.ToString("F2") : "none")} gameWritesHeld={s.HeldWrites} bypassed={s.Bypassed} " +
                  $"positionsHeld={_heldAtUnfreeze} animRequestsHeld={_animDeferredAtUnfreeze} animReplayed={_animReplayed} animStale={_anim.Stale} deferred={_removals.Deferred} replayed={_removals.Replayed} stale={_removals.Stale} " +
                  $"animCalls={AnimGatePatch.Calls - _animCallsAtFreeze} animCallsSinceInstall={AnimGatePatch.Calls} " +
                  $"ms={Environment.TickCount64 - _frozenAtMs}");
    }

    partial void OnDeferredFlushed(string why, int replayed)
    {
        if (!StellarDiagnostics.IsEnabled || replayed == 0) return;
        _log.Info($"[FreeCam] deferred removals replayed: {replayed} ({why})");
    }
}
