using System;
using Stellar.Abstractions.Diagnostics;
using Stellar.Application.Services;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaPlayerStatsProbe
{
    // Bounds the "sheet not ready" line to one per contiguous streak of skipped passes —
    // the login window is many ticks long and this runs at the framework tick rate.
    private bool _notReadyStreakLogged;

    /// <summary>
    /// One line per memo decision, gated on <c>STELLAR_DIAGNOSTICS=1</c>. The silence around
    /// this decision is what made the 2026-09-21 "eight dashes" report a full investigation:
    /// a poisoned memo produced no log output at all once the ids were latched.
    /// </summary>
    private void LogMemoPass(in AttrMemoPassResult pass)
    {
        if (!StellarDiagnostics.IsEnabled) return;

        if (pass.SkippedNotReady)
        {
            if (_notReadyStreakLogged) return;
            _notReadyStreakLogged = true;
            _log.Info($"[Stellar][PlayerStats] memo: skipped-all-miss-pass attempted={pass.Attempted} " +
                      "(attribute sheet not ready — latching nothing)");
            return;
        }

        _notReadyStreakLogged = false;
        if (pass.Latched.Count == 0) return;

        // via=readiness means the pass read nothing and the explicit sheet-populated signal
        // is what allowed the latch — the CombatMeter-only case, where no subscribed id can
        // ever read. via=hit is the ordinary mixed pass. Expected cadence: ONCE per id set per
        // AttrReadabilityMemo.RetryAfterTicks window (the verdict expires, the id is re-probed
        // once, misses again and re-latches). More often than that means something is dropping
        // verdicts early; an id that stops appearing has started reading — the retry paid off.
        var via = pass.Hits == 0 ? "readiness" : "hit";
        _log.Info($"[Stellar][PlayerStats] memo: latched={string.Join(",", pass.Latched)} " +
                  $"hits={pass.Hits}/{pass.Attempted} via={via}");
    }
}
