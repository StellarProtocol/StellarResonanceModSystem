using System;
using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic sibling partial for the Lua-bridge equipped-imagine read
/// (<c>PandaLoadoutProbe.UpdateResonanceState</c>). Gated on
/// <see cref="StellarDiagnostics.IsEnabled"/>, mirroring the Deep-Slumber one-shot. Fires once on
/// the first parse whose dump shows the resonance fragment actually RAN — a "RES" row OR a "RESERR"
/// failure row — carrying the installed ids or the Lua error. The RESERR case fires too on purpose:
/// the Deep-Slumber lesson (owner run sea/O1jJepsgKC) was an error-silent empty capture, so an
/// erroring resonance walk must surface itself rather than stay quiet. A dump with neither row
/// (bridge unresolved, or a stale in-flight read from before this enrichment shipped) logs nothing
/// and leaves the latch unset, so the first read that ran the walk is the one logged.
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    private bool _resonanceFirstReadLogged;

    private void LogResonanceFirstRead(IReadOnlyList<int>? installed, string raw)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_resonanceFirstReadLogged) return;
        var error = ParseResonanceErrorLine(raw);
        if (installed is null && error is null) return;   // resonance fragment hasn't run yet
        _resonanceFirstReadLogged = true;

        var ids = installed is null ? "n/a" : string.Join(",", installed);
        _log.Info($"[Stellar][Loadout][Resonance] first read via Lua bridge: installed=[{ids}] error={error ?? "none"}");
    }

    // Pure "RESERR" row extractor — the chunk appends it INSTEAD of "RES" when its pcall failed.
    // Diagnostics-only; never used for state-building (an erroring walk is no-signal, see
    // ParseResonanceLine).
    private static string? ParseResonanceErrorLine(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith("RESERR\t", StringComparison.Ordinal)) return line.Substring(7);
        }
        return null;
    }
}
