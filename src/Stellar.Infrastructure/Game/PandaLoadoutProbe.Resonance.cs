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
/// <para><b>Primary mechanism: an EVENT-DRIVEN read of the skill HOTBAR slots 7/8</b> — fired by the
/// container-merge event, never by a timer (owner ruling 2026-08-23; the ~1 s poll this replaced is
/// described in <c>PandaLoadoutProbe.LiveState.cs</c>). Channel map from owner diagnostics run
/// <c>sea/pNhmVQvVmV</c>, 2026-08-23: the swap's method-22 delta carried
/// fields 2/55/96/104 — field 28 (<c>cs.resonance</c>) is NEVER re-serialized mid-session (its
/// only write paths are login firstSync ResetData/MergeData), so BOTH the field-28 trigger AND
/// the <c>cs.resonance.installed</c> read are login-stale. Worse, measured against the game
/// tables: <c>installed</c> ids (50101/50102) are ENVIRONMENT (Terra) resonance objects
/// ("Hero's Herb"/"Wind Core" — consumed only by <c>env_vm/env_service</c>), not Battle Imagines
/// at all. The live equipped-imagine representation is the hotbar: <c>cs.slots.slots[7]/[8]</c>
/// (MysteriesSkill slots per <c>weapon_skill_vm.lua</c>), whose <c>skillId</c>s are the aoyi
/// SKILL ids — the id space the site's imagine chips (names <c>aoyi</c> map, 73 entries) and
/// <c>IGameDataResonance.GetImagineForSkill</c> ALREADY resolve, so they are emitted directly
/// with no mapping. The "RES" (installed) row is kept as diagnostics + a null-latch-only seed;
/// <see cref="SelectInstalledSource"/> pins the no-source-flapping policy. RESSLOTERR / absent
/// row = NO SIGNAL (<see cref="TryReadInstalled"/> returns false — the published snapshot is
/// kept); an empty RESSLOT row = genuinely nothing in slots 7/8.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe : IResonanceProbe
{
    // The published pair, PRIMARILY the hotbar slots 7/8 aoyi SKILL ids (slots row); null until a
    // read succeeds (bridge unresolved, or every section errored → RESERR/RESSLOTERR only). Latch
    // updates only through ApplyResonanceSources — never wiped by a no-signal read.
    private IReadOnlyList<int>? _resonanceInstalled;

    public bool TryReadInstalled(out IReadOnlyList<int> installed)
    {
        var latched = _resonanceInstalled;
        installed = latched ?? Array.Empty<int>();
        return latched is not null;
    }

    // Shared latch-update for both read paths (the on-demand refresh dump, which carries no RESSLOT
    // row, and the merge-event live-state chunk, which does). Pure policy in SelectInstalledSource;
    // the change log + first-read one-shot are diagnostics-gated no-ops in production.
    //
    // Returns TRUE only when the published pair actually changed — that is one half of the
    // ILoadout.LiveStateChanged gate (PandaLoadoutProbe.LiveState.cs § ApplyLiveRows), so a no-signal
    // read or an identical re-read must return false.
    private bool ApplyResonanceSources(
        IReadOnlyList<int>? slotsRow, IReadOnlyList<int>? installedRow, string raw)
    {
        LogResonanceFirstRead(slotsRow, installedRow, raw);
        var (next, source) = SelectInstalledSource(slotsRow, installedRow, _resonanceInstalled);
        if (next is null) return false;
        if (InstalledEquals(_resonanceInstalled, next)) return false;
        LogResonanceChanged(_resonanceInstalled, next, source!);   // the owner's next-test discriminator
        _resonanceInstalled = next;
        return true;
    }

    /// <summary>Pure source-selection policy (unit-tested): the hotbar slots row is the PRIMARY
    /// source and wins whenever present; the legacy installed row (login-stale env-resonance ids)
    /// may only SEED a still-null latch — old-chunk-dump tolerance — so identity can never flap
    /// between differently-sourced lists tick-to-tick. Null Next = keep the current latch.</summary>
    internal static (IReadOnlyList<int>? Next, string? Source) SelectInstalledSource(
        IReadOnlyList<int>? slotsRow, IReadOnlyList<int>? installedRow, IReadOnlyList<int>? currentLatch)
    {
        if (slotsRow is not null) return (slotsRow, "slots-poll");
        if (currentLatch is null && installedRow is not null) return (installedRow, "installed-fallback");
        return (null, null);
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

    // ── Live-mirror read (owner run sea/pNhmVQvVmV, 2026-08-23) ────────────────────────────────
    // The equipped pair is read from the hotbar slots each time the game merges fresh container data.
    // It used to be a ~1 s tick-counter POLL here (PollResonanceIfDue + ResonancePollChunk); owner
    // ruling 2026-08-23 retired that — capture is event-driven at the right probe point, never
    // timer-based. The chunk now lives in PandaLoadoutProbe.LiveState.cs (LiveStateChunk), which
    // reads the imagine slots AND the current class's equipment/talents in one pass and calls
    // ApplyResonanceSources above with both rows.

    /// <summary>Pure "RESSLOT" row parser — hotbar slots 7/8 as <c>7:&lt;skillId&gt;,8:&lt;skillId&gt;</c>
    /// (the chunk emits only slots present with a non-zero skillId). Returns the aoyi SKILL ids in
    /// CANONICAL SLOT ORDER (7 then 8 — setup identity is order-sensitive) regardless of pair order
    /// in the row. Null when NO "RESSLOT" row is present (an old poll dump, or the slots pcall
    /// failed and only "RESSLOTERR" was appended) — never for a genuinely empty hotbar, which still
    /// carries a "RESSLOT" row with an empty payload. Malformed pairs and zero/negative ids are
    /// skipped, never thrown.</summary>
    internal static IReadOnlyList<int>? ParseResonanceSlotsLine(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            if (!line.StartsWith("RESSLOT\t", StringComparison.Ordinal)) continue;
            var csv = line.Substring(8);
            if (csv.Length == 0) return Array.Empty<int>();
            int slot7 = 0, slot8 = 0;
            foreach (var part in csv.Split(','))
            {
                var colon = part.IndexOf(':');
                if (colon <= 0) continue;
                if (!int.TryParse(part.Substring(0, colon), NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot)) continue;
                if (!int.TryParse(part.Substring(colon + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var skillId)) continue;
                if (skillId <= 0) continue;
                if (slot == 7) slot7 = skillId;
                else if (slot == 8) slot8 = skillId;
            }
            if (slot7 == 0 && slot8 == 0) return Array.Empty<int>();
            var ids = new List<int>(2);
            if (slot7 != 0) ids.Add(slot7);
            if (slot8 != 0) ids.Add(slot8);
            return ids;
        }
        return null;
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

    // The RESSLOT/RES read chunk moved to PandaLoadoutProbe.LiveState.cs (LiveStateChunk) when the
    // ~1 s poll became the container-merge EVENT — it now reads the hotbar imagine slots together
    // with the current class's equipment/modules/talents in a single dispatch.

    // The "RES" fragment RefreshChunk (PandaLoadoutProbe.Resolution.cs) appends to its dump.
    // cs.resonance.installed is a PLAIN Lua array in the container's __data__ (ordinary __index
    // read, unaffected by the __pairs trap), indexed 1..#inst per the banked
    // never-trust-a-bare-loop-value rule. CORRECTED 2026-08-23 (owner run sea/pNhmVQvVmV): this
    // list is LOGIN-STALE (field 28's only write paths are login firstSync) and its ids are
    // ENVIRONMENT-resonance objects, not Battle Imagines — kept ONLY as a null-latch seed +
    // diagnostics under SelectInstalledSource; the hotbar slots poll is the real source. The row
    // is appended ONLY when the pcall succeeded; a failure appends "RESERR\t<msg>" instead, which
    // the parser treats as no-signal.
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
