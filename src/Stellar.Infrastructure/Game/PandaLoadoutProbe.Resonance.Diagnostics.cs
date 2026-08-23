using System;
using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostic sibling partial for the equipped-imagine reads
/// (<c>PandaLoadoutProbe.ApplyResonanceSources</c>). Gated on
/// <see cref="StellarDiagnostics.IsEnabled"/>, mirroring the Deep-Slumber one-shot. The first-read
/// one-shot fires once, from whichever path reads first, when the dump shows ANY resonance section
/// actually RAN — a "RESSLOT"/"RES" data row OR a "RESSLOTERR"/"RESERR" failure row — carrying both
/// lists and both errors. The error cases fire on purpose: the Deep-Slumber lesson (owner run
/// sea/O1jJepsgKC) was an error-silent empty capture, so an erroring read must surface itself
/// rather than stay quiet. A dump with none of the rows (bridge unresolved, or a stale in-flight
/// read from before this enrichment shipped) logs nothing, so the first read that ran is the one
/// logged.
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    private bool _resonanceFirstReadLogged;

    private void LogResonanceFirstRead(IReadOnlyList<int>? slots, IReadOnlyList<int>? installed, string raw)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        if (_resonanceFirstReadLogged) return;
        var installedError = ParseErrorLine(raw, "RESERR\t");
        var slotsError = ParseErrorLine(raw, "RESSLOTERR\t");
        if (slots is null && installed is null && installedError is null && slotsError is null) return;
        _resonanceFirstReadLogged = true;

        var slotIds = slots is null ? "n/a" : string.Join(",", slots);
        var ids = installed is null ? "n/a" : string.Join(",", installed);
        _log.Info($"[Stellar][Loadout][Resonance] first read via Lua bridge: slots=[{slotIds}] installed=[{ids}] slotsError={slotsError ?? "none"} error={installedError ?? "none"}");
    }

    /// <summary>Change log — the owner's next-test discriminator (run <c>sea/pNhmVQvVmV</c>):
    /// fires whenever the selected source's list differs from the current latch, tagged with the
    /// source that produced it ("slots-poll" primary / "installed-fallback" null-latch seed), so
    /// an in-session imagine swap must produce exactly one "(slots-poll)" line within ~1 s.
    /// Diagnostics-gated; NOT a one-shot (every change is a data point).</summary>
    private void LogResonanceChanged(IReadOnlyList<int>? old, IReadOnlyList<int> next, string source)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        var from = old is null ? "n/a" : string.Join(",", old);
        _log.Info($"[Stellar][Loadout][Resonance] installed changed: [{from}] -> [{string.Join(",", next)}] ({source})");
    }

    // Pure error-row extractor for "RESERR\t" / "RESSLOTERR\t" — the chunk appends the error row
    // INSTEAD of that section's data row when its pcall failed. Diagnostics-only; never used for
    // state-building (an erroring read is no-signal, see SelectInstalledSource).
    private static string? ParseErrorLine(string raw, string prefix)
    {
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal)) return line.Substring(prefix.Length);
        }
        return null;
    }
}
