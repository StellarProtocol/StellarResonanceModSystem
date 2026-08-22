using System;
using System.Collections.Generic;
using System.Globalization;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// <see cref="IResonanceProbe"/> via the SAME Lua bridge + on-demand refresh chunk
/// <see cref="PandaLoadoutProbe"/> already drives for Role Plan + Deep-Slumber data. Owner-verified
/// bug (staging run <c>sea/445626427740520448</c>, 2026-08-23): after an in-session Battle Imagine
/// swap, <c>IResonanceState.Installed</c> still served the PRE-SWAP pair — the C# reflection mirror
/// (<c>PandaInventoryPullReader.TryReadInstalled</c>, still implemented on
/// <see cref="PandaInventoryProbe"/> but no longer the Host-selected implementation) is the THIRD
/// confirmed organ of the stale <c>CharSerialize</c> mirror (after gear/modules and Deep-Slumber —
/// <c>docs/recon/combatmeter-data-facts.md</c>). This reads the LUA mirror instead:
/// <c>Z.ContainerMgr.CharSerialize.resonance.installed</c> is replaced WHOLESALE by the game's own
/// field-28 dirty-delta merge (<c>lua/zcontainer/resonance.lua</c> <c>mergeDataFuncs[2]</c>), so it
/// is live the moment the swap syncs.
///
/// <para>No separate resolution/refresh path: the "RES" row rides the SAME
/// <c>_StellarLoadoutData</c> global, re-parsed on every changed dump and re-fired by
/// <see cref="PandaLoadoutProbe.OnGearChanged"/> — which a field-28 delta now triggers
/// (<c>ContainerDirtyDeltaReader.TouchesResonance</c>), so a swap re-reads within one on-demand
/// refresh window. Absent row = NO SIGNAL (<see cref="TryReadInstalled"/> returns false — the
/// published snapshot is kept); an empty row = genuinely no imagines equipped.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe : IResonanceProbe
{
    // Latched by UpdateResonanceState on each changed parse; null until a dump carries a "RES" row
    // (bridge unresolved, an old in-flight dump, or the chunk's pcall failed → "RESERR" only).
    private IReadOnlyList<int>? _resonanceInstalled;

    public bool TryReadInstalled(out IReadOnlyList<int> installed)
    {
        var latched = _resonanceInstalled;
        installed = latched ?? Array.Empty<int>();
        return latched is not null;
    }

    // Called once per ParseLoadoutData pass (mirrors UpdateDeepSlumberState). Unconditional latch:
    // a dump without a RES row sets null → TryReadInstalled reports "not ready" and the
    // Application-side ResonanceService simply keeps its last published snapshot.
    private void UpdateResonanceState(string raw)
    {
        _resonanceInstalled = ParseResonanceLine(raw);
        // no-op unless STELLAR_DIAGNOSTICS; non-latching until the resonance fragment has run once
        LogResonanceFirstRead(_resonanceInstalled, raw);
    }

    /// <summary>Pure "RES" row parser — internal so it's directly unit-testable without the Lua
    /// bridge. Returns null when NO "RES" row is present (an old dump, or the chunk's pcall failed
    /// and only "RESERR" was appended) — never for a genuinely empty imagine set, which still
    /// carries a "RES" row with an empty payload. Malformed ids are skipped, never thrown.</summary>
    internal static IReadOnlyList<int>? ParseResonanceLine(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            if (!line.StartsWith("RES\t", StringComparison.Ordinal)) continue;
            var csv = line.Substring(4);
            if (csv.Length == 0) return Array.Empty<int>();
            List<int>? ids = null;
            foreach (var part in csv.Split(','))
            {
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    (ids ??= new List<int>()).Add(id);
                }
            }
            return ids ?? (IReadOnlyList<int>)Array.Empty<int>();
        }
        return null;
    }

    // The equipped-Battle-Imagine fragment RefreshChunk (PandaLoadoutProbe.Resolution.cs) appends
    // to its dump. cs.resonance.installed is a PLAIN Lua array living in the container's __data__
    // (resolved via setForbidenMt's __index — an ordinary table read, unaffected by the __pairs
    // trap), replaced wholesale on every field-28 merge — the live source. Indexed 1..#inst per the
    // banked never-trust-a-bare-loop-value rule. The "RES" row is appended ONLY when the pcall
    // succeeded (present-with-empty = genuinely no imagines); a failure appends "RESERR\t<msg>"
    // instead, which the parser treats as no-signal.
    private const string ResonanceChunkFragment =
        " local res=\"\"" +
        " local resOk,resErr=pcall(function()" +
        "  local inst=(cs.resonance) and (cs.resonance).installed" +
        "  if inst~=nil then" +
        "   for i=1,#inst do" +
        "    local v=inst[i]" +
        "    if v~=nil then res=(res==\"\" and \"\" or res..\",\")..tostring(v) end" +
        "   end" +
        "  end" +
        " end)" +
        " if resOk then out=out..\"\\nRES\\t\"..res else out=out..\"\\nRESERR\\t\"..tostring(resErr) end";
}
