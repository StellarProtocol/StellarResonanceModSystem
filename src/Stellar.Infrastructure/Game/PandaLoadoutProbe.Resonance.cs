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
/// <para><b>Primary mechanism: a 1 Hz POLL of the live Lua mirror</b> (owner diagnostics run
/// <c>sea/pNhmVQvVmV</c>, 2026-08-23): zero of that session's 8 method-22 deltas carried
/// top-level field 28 — the swap's container sync never reaches the WorldNtf hook, so NO trigger
/// on that hook can re-fire the read (and generic unknown-field skipping is inherently
/// unreliable: field 104 proved to be a RAW SCALAR, not a container — per-field encodings are
/// unknowable). <see cref="PollResonanceIfDue"/> therefore reads the mirror directly on the
/// drain tick. The trigger paths (refresh-chunk "RES" row + the field-28 delta trigger) remain
/// as belt-and-braces. Absent row / RESERR = NO SIGNAL (<see cref="TryReadInstalled"/> returns
/// false — the published snapshot is kept); an empty row = genuinely no imagines equipped.</para>
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

    // ── Live-mirror poll (owner run sea/pNhmVQvVmV, 2026-08-23) ────────────────────────────────
    // POLL the mirror instead of waiting for a trigger: cs.resonance.installed is provably live
    // mid-session (the game's own imagine UI reacts via the container watcher on dirty.installed,
    // and installed has no other write path). A pure LOCAL container read — no RPC, no server
    // traffic — so the no-recurring-RPC policy does not apply, and nothing yields, so no
    // coroutine wrapper is needed (same non-async shape as ClearSwitchGlobalChunk). Driven from
    // DrainPendingCompletions AFTER the _bridgeResolved gate (world-gated, main thread).

    // Lua global the poll chunk writes; C# reads it back the same tick (DoString is synchronous).
    private const string ResonanceLiveGlobal = "_StellarResonanceLive";

    // Raw-string memo (mirrors _lastDataRaw): an unchanged read does zero parse work.
    private string? _lastResonanceLiveRaw;

    // ~1 s at the 30 Hz loadout drain (ResolveAttemptEveryTicks pattern — a counter, no clock).
    private int _resonancePollTickCounter;
    private const int ResonancePollEveryTicks = 30;

    private void PollResonanceIfDue()
    {
        if (_resonancePollTickCounter++ % ResonancePollEveryTicks != 0) return;
        if (!InvokeChunk(ResonancePollChunk)) return;
        var raw = ReadLuaGlobalString(ResonanceLiveGlobal);
        if (string.IsNullOrEmpty(raw) || raw == _lastResonanceLiveRaw) return;
        _lastResonanceLiveRaw = raw;

        var parsed = ParseResonanceLine(raw!);
        LogResonanceFirstRead(parsed, raw!);   // idempotent one-shot — fires on RES or RESERR
        if (parsed is null) return;            // RESERR: never wipe a good latch with no-signal
        if (!InstalledEquals(_resonanceInstalled, parsed))
        {
            LogResonanceChanged(_resonanceInstalled, parsed);   // the owner's next-test discriminator
        }
        _resonanceInstalled = parsed;
    }

    /// <summary>Pure order-sensitive id-list equality (installed is positional — imagine slot 1 /
    /// slot 2, so a reorder IS a change). Internal static for direct unit coverage; drives the
    /// poll's latch-update + change-log decision.</summary>
    internal static bool InstalledEquals(IReadOnlyList<int>? a, IReadOnlyList<int>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a is null || b is null) return false;
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    // Poll chunk: pcall-read cs.resonance.installed (same keys-first + 1..#inst index idioms as
    // ResonanceChunkFragment below — never a bare pairs() loop value), build the same csv, and
    // rawset "RES\t<csv>" on success or "RESERR\t<msg>" on pcall failure into ResonanceLiveGlobal.
    // Const string — zero per-call string building; parsed by the SAME ParseResonanceLine.
    private const string ResonancePollChunk =
        " local res=\"\"" +
        " local resOk,resErr=pcall(function()" +
        "  local cs=(Z.ContainerMgr).CharSerialize" +
        "  local inst=(cs.resonance) and (cs.resonance).installed" +
        "  if inst~=nil then" +
        "   for i=1,#inst do" +
        "    local v=inst[i]" +
        "    if v~=nil then res=(res==\"\" and \"\" or res..\",\")..tostring(v) end" +
        "   end" +
        "  end" +
        " end)" +
        " if resOk then rawset(_G,\"" + ResonanceLiveGlobal + "\",\"RES\\t\"..res)" +
        " else rawset(_G,\"" + ResonanceLiveGlobal + "\",\"RESERR\\t\"..tostring(resErr)) end";

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
