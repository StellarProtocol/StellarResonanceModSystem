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
/// <para><b>Primary mechanism: a 1 Hz POLL of the skill HOTBAR slots 7/8</b> (channel map from
/// owner diagnostics run <c>sea/pNhmVQvVmV</c>, 2026-08-23): the swap's method-22 delta carried
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
    // The published pair, PRIMARILY the hotbar slots 7/8 aoyi SKILL ids (slots-poll); null until a
    // read succeeds (bridge unresolved, or every section errored → RESERR/RESSLOTERR only). Latch
    // updates only through ApplyResonanceSources — never wiped by a no-signal read.
    private IReadOnlyList<int>? _resonanceInstalled;

    public bool TryReadInstalled(out IReadOnlyList<int> installed)
    {
        var latched = _resonanceInstalled;
        installed = latched ?? Array.Empty<int>();
        return latched is not null;
    }

    // Called once per ParseLoadoutData pass (refresh-chunk path). The dump's "RES" row is the
    // login-stale installed list (env-resonance ids) — under SelectInstalledSource it may only
    // SEED a still-null latch (old-dump tolerance); the hotbar slots poll is the live source and
    // always wins. Never nulls the latch (a dump without a RES row is no-signal).
    private void UpdateResonanceState(string raw)
    {
        ApplyResonanceSources(slotsRow: null, installedRow: ParseResonanceLine(raw), raw);
    }

    // Shared latch-update for both read paths (refresh dump + 1 Hz poll). Pure policy in
    // SelectInstalledSource; the change log + first-read one-shot are diagnostics-gated no-ops in
    // production.
    private void ApplyResonanceSources(
        IReadOnlyList<int>? slotsRow, IReadOnlyList<int>? installedRow, string raw)
    {
        LogResonanceFirstRead(slotsRow, installedRow, raw);
        var (next, source) = SelectInstalledSource(slotsRow, installedRow, _resonanceInstalled);
        if (next is null) return;
        if (!InstalledEquals(_resonanceInstalled, next))
        {
            LogResonanceChanged(_resonanceInstalled, next, source!);   // the owner's next-test discriminator
        }
        _resonanceInstalled = next;
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
        ApplyResonanceSources(ParseResonanceSlotsLine(raw!), ParseResonanceLine(raw!), raw!);
    }

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

    // Poll chunk, two independently-pcall'd sections, ONE global write:
    //   RESSLOT (PRIMARY) — hotbar slots 7/8: cs.slots.slots is the Slot container's message-valued
    //     map (zcontainer/slot.lua mergeDataFuncs[1]); DIRECT numeric index (ss[7]/ss[8] via
    //     setForbidenMt's __index — an ordinary table read, immune to the __pairs nil-value trap);
    //     SlotInfo's Lua-side field is `skillId` (zcontainer/slot_info.lua mergeDataFuncs[2]).
    //     Slots 7/8 = the MysteriesSkill (aoyi/imagine) slots per weapon_skill_vm's skillTypeInSlot.
    //     Only slots present with a non-zero skillId are emitted.
    //   RES (fallback/diagnostics) — cs.resonance.installed, kept for old-latch seeding + the
    //     first-read log; login-stale (field 28 never re-serializes mid-session).
    // Const string — zero per-call string building; parsed by ParseResonanceSlotsLine +
    // ParseResonanceLine.
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
        " local sl=\"\"" +
        " local slOk,slErr=pcall(function()" +
        "  local cs=(Z.ContainerMgr).CharSerialize" +
        "  local ss=(cs.slots) and (cs.slots).slots" +
        "  if ss~=nil then" +
        "   local s7=ss[7] local s8=ss[8]" +
        "   if s7~=nil and s7.skillId~=nil and s7.skillId~=0 then sl=\"7:\"..tostring(s7.skillId) end" +
        "   if s8~=nil and s8.skillId~=nil and s8.skillId~=0 then sl=(sl==\"\" and \"\" or sl..\",\")..\"8:\"..tostring(s8.skillId) end" +
        "  end" +
        " end)" +
        " local out" +
        " if resOk then out=\"RES\\t\"..res else out=\"RESERR\\t\"..tostring(resErr) end" +
        " if slOk then out=out..\"\\nRESSLOT\\t\"..sl else out=out..\"\\nRESSLOTERR\\t\"..tostring(slErr) end" +
        " rawset(_G,\"" + ResonanceLiveGlobal + "\", out)";

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
