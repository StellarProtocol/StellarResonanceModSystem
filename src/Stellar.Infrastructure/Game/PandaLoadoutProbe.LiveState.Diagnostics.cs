using System;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic sibling partial for the merge-event live-state read
/// (<c>PandaLoadoutProbe.RefreshLiveStateIfArmed</c>). Gated on
/// <see cref="StellarDiagnostics.IsEnabled"/> — a production no-op.
///
/// <para>These lines are the owner's acceptance discriminator for the event-driven rework: a gear
/// "Replace" (or a talent edit, or an imagine swap) must produce a <c>[LiveState] read</c> line
/// naming the fresh row, followed on the SAME action by <c>served state CHANGED</c>. A read line with
/// no change line means the game merged something this class does not serve; NO read line at all
/// means the merge signal never reached the probe (look for the wire capture's
/// <c>[Inventory][Merge]</c> census / envelope-fail lines instead).</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    private bool _liveStateFirstReadLogged;

    // Per read (not one-shot after the first): the read IS the measurement, and merges only storm
    // under a deliberate player action. The first read additionally announces itself so the log shows
    // the event path came up at all.
    private void LogLiveStateRead(string raw)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        try
        {
            var live = FirstRowWithPrefix(raw, "LIVE\t") ?? FirstRowWithPrefix(raw, "LIVEERR\t") ?? "(no LIVE row)";
            var slots = FirstRowWithPrefix(raw, "RESSLOT\t") ?? FirstRowWithPrefix(raw, "RESSLOTERR\t") ?? "(no RESSLOT row)";
            var first = _liveStateFirstReadLogged ? string.Empty : " FIRST";
            _liveStateFirstReadLogged = true;
            _log.Info($"[Stellar][Loadout][LiveState]{first} read on container-merge event: {live} | {slots}");
        }
        catch { /* diagnostics must never disturb the drain tick */ }
    }

    /// <summary>Fires when a live re-read actually CHANGED what the framework serves — the signal that
    /// becomes <c>ILoadout.LiveStateChanged</c>. Not a one-shot: every change is a data point.</summary>
    private void LogLiveStateChanged(string what)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        try { _log.Info($"[Stellar][Loadout][LiveState] served state CHANGED ({what}) — raising LiveStateChanged"); }
        catch { /* diagnostics must never disturb the drain tick */ }
    }

    private static string? FirstRowWithPrefix(string raw, string prefix)
    {
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
        }
        return null;
    }
}
