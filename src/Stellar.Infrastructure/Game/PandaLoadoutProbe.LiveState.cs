using System;
using System.Collections.Generic;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// EVENT-DRIVEN live build-state re-read for <see cref="PandaLoadoutProbe"/> — owner ruling
/// 2026-08-23: <b>capture must be event-driven at the right probe point; no polling / timer-based
/// data gathering</b> (equipment, modules, battle imagines, talents alike).
///
/// <para><b>The event.</b> Every <c>CharSerialize</c> update — login full sync (WorldNtf method 21)
/// and dirty delta (method 22) alike — funnels through ONE game service
/// (<c>Panda.ZGame.ContainerSyncService</c> → Lua <c>ContainerSyncService.OnSync</c> →
/// <c>MergeData</c> → watcher dispatch). So the ARRIVAL of a merge is the correct "the Lua mirror the
/// framework reads is now fresh" signal, and the wire capture raises it FIELD-AGNOSTICALLY (see
/// <c>ContainerDirtyDeltaReader.IsMergeSignal</c>). It reaches this probe as
/// <see cref="PandaLoadoutProbe.OnGearChanged"/> on the network thread, which only flips
/// <c>_mergePending</c>; the read itself runs on the next main-thread drain tick, coalesced — N
/// deltas arriving inside one tick produce exactly ONE re-read.</para>
///
/// <para><b>What it replaced.</b> A ~1 s tick-counter (<c>PollResonanceIfDue</c>) fired the imagine
/// chunk, and one-shot wire-time recaptures raced the framework's async refresh. Three owner-visible
/// bugs came out of that: a single talent edit was missed, an imagine swap served the pre-swap pair,
/// and a revert to setup 1 was never seen. The per-field trigger allowlist (CharSerialize 12 equip /
/// 28 resonance / 57 mod / 61 professionList / 101 seasonCultivate) missed the gear UI's "Replace"
/// button outright — measured, its delta's top-level fields were 2/55/96/104, none of the five.</para>
///
/// <para><b>Cost shape.</b> One <c>DoString</c> of a const chunk over LOCAL Lua containers — no RPC,
/// no server traffic, nothing yields — plus a string compare. The expensive downstream work (the
/// whole-item-container <c>ResolvePlanLoadouts</c> scan) is unchanged and still gated by
/// <c>_resolvePending</c>. The RPC refresh (<c>SyncProjectList</c>) stays ON DEMAND with its own
/// cooldown: an unprompted recurring RPC is a policy violation.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    // Set on the network thread by OnGearChanged (flag only — never a game/IL2CPP read there),
    // consumed on the main-thread drain tick. Starts TRUE so the first drain after the bridge
    // resolves reads once without waiting for a delta (login's own m21 raises it again anyway).
    private volatile bool _mergePending = true;

    // "The last re-read changed what we SERVE." Set only on a structural difference (see
    // ApplyLiveRows); consumed once by LoadoutService.Tick via ConsumeLiveStateChanged.
    private bool _liveStateChanged;

    // Lua global the live-state chunk writes; C# reads it back the same tick (DoString is synchronous).
    private const string LiveStateGlobal = "_StellarLiveState";

    // Raw-string memo (mirrors _lastDataRaw): a byte-identical re-read does zero parse work. NOT the
    // change decision — Lua `pairs` order over equipList/modSlots is unspecified, so the same state can
    // serialize differently. The authoritative gate is the structural compare in ApplyLiveRows.
    private string? _lastLiveStateRaw;

    /// <summary>Consumes the "served state changed" flag. See <c>ILoadoutProbe</c> for the contract.</summary>
    public bool ConsumeLiveStateChanged()
    {
        if (!_liveStateChanged) return false;
        _liveStateChanged = false;
        return true;
    }

    // Main-thread, called from DrainPendingCompletions after the bridge gate. Consumes the coalesced
    // merge flag, runs the LOCAL live-state chunk, and applies its rows. A failed dispatch or an unset
    // global is NO-SIGNAL: the latched state is kept (never blanked) and the flag stays consumed —
    // the next merge re-arms it.
    private void RefreshLiveStateIfArmed()
    {
        if (!_mergePending) return;
        _mergePending = false;
        _refreshPending = true;   // also re-fire the on-demand SyncProjectList (cooldown-coalesced)

        if (!InvokeChunk(LiveStateChunk)) return;
        var raw = ReadLuaGlobalString(LiveStateGlobal);
        if (string.IsNullOrEmpty(raw)) return;
        LogLiveStateRead(raw!);   // no-op unless STELLAR_DIAGNOSTICS
        if (raw == _lastLiveStateRaw) return;
        _lastLiveStateRaw = raw;
        ApplyLiveRows(raw!, ParseResonanceSlotsLine(raw!));
    }

    // Applies the LIVE + RES/RESSLOT rows of a dump and decides whether what we SERVE actually
    // changed. Shared by both read paths: the merge-driven live-state chunk (which carries a RESSLOT
    // row) and ParseLoadoutData's refresh dump (which does not — slotsRow null there, so the hotbar
    // latch is left alone by SelectInstalledSource).
    //
    // A dump with NO "LIVE" row is no-signal for the live fields (the chunk's live section pcall
    // failed): applying ParseLiveLine's all-empty result would blank profession/talents and read as a
    // change. Same never-blank-on-a-failed-read rule the resonance latch already follows.
    private void ApplyLiveRows(string raw, IReadOnlyList<int>? slotsRow)
    {
        var liveChanged = false;
        if (HasLiveRow(raw))
        {
            var before = CurrentLive();
            ReadLiveLine(raw);
            liveChanged = LiveStateDiffers(before, CurrentLive());
        }

        var imaginesChanged = ApplyResonanceSources(slotsRow, ParseResonanceLine(raw), raw);
        if (!liveChanged && !imaginesChanged) return;

        _resolvePending = true;    // re-resolve this class's gear/modules from the fresh slot→uuid maps
        _liveStateChanged = true;  // consumer-facing post-parse event (ILoadout.LiveStateChanged)
        LogLiveStateChanged(liveChanged ? (imaginesChanged ? "live+imagines" : "live") : "imagines");
    }

    private LiveLoadout CurrentLive()
        => new(_liveEquipUuids, _liveModUuids, _liveProfessionId, _liveTalentStageId, _liveTalentNodes);

    /// <summary>True when <paramref name="raw"/> carries a "LIVE" row at all. A dump WITHOUT one is a
    /// failed/old read, not an empty setup — the caller must keep the latched live state rather than
    /// parse an all-empty <see cref="LiveLoadout"/> over it.</summary>
    internal static bool HasLiveRow(string raw)
    {
        foreach (var line in raw.Split('\n'))
        {
            if (line.StartsWith("LIVE\t", StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>Pure structural difference over everything the LIVE row feeds: the equipped
    /// gear/module slot→uuid maps, the class, and the talent stage + allocated nodes. This is the
    /// change-event gate — an identical re-parse must raise NOTHING (pinned:
    /// <c>PandaLoadoutProbeLiveStateTests</c>), because a spurious event makes every consumer
    /// re-snapshot the player's setup on every container delta.</summary>
    internal static bool LiveStateDiffers(in LiveLoadout a, in LiveLoadout b)
        => a.ProfessionId != b.ProfessionId
        || a.TalentStageId != b.TalentStageId
        || !SameIntList(a.TalentNodes, b.TalentNodes)
        || !SameUuidMap(a.Equip, b.Equip)
        || !SameUuidMap(a.Mod, b.Mod);

    /// <summary>Order-insensitive slot→uuid map equality (the maps are keyed by slot; Lua
    /// <c>pairs</c> yields them in an unspecified order).</summary>
    internal static bool SameUuidMap(IReadOnlyDictionary<int, long> a, IReadOnlyDictionary<int, long> b)
    {
        if (ReferenceEquals(a, b)) return true;
        if (a.Count != b.Count) return false;
        foreach (var (slot, uuid) in a)
        {
            if (!b.TryGetValue(slot, out var other) || other != uuid) return false;
        }
        return true;
    }

    /// <summary>Order-SENSITIVE id-list equality (talent node order is how the game emits the tree;
    /// null and empty are the same "nothing allocated" signal here, unlike the imagine latch where
    /// null means "never read").</summary>
    internal static bool SameIntList(IReadOnlyList<int>? a, IReadOnlyList<int>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        var countA = a?.Count ?? 0;
        var countB = b?.Count ?? 0;
        if (countA != countB) return false;
        for (var i = 0; i < countA; i++)
        {
            if (a![i] != b![i]) return false;
        }
        return true;
    }

    // LIVE-STATE CHUNK — the merge event's read. Three independently-pcall'd sections, ONE global
    // write, no coroutine wrapper (nothing yields — same non-async shape as ClearSwitchGlobalChunk),
    // no interpolation (no Lua-injection surface). Reads ONLY local containers:
    //   RESSLOT (PRIMARY imagines) — cs.slots.slots[7]/[8].skillId, the aoyi hotbar slots. DIRECT
    //     numeric index (immune to the zcontainer __pairs nil-value trap); cs.resonance is login-stale
    //     and its ids are ENVIRONMENT resonance objects, so RES below is fallback/diagnostics only.
    //   RES (fallback/diagnostics) — cs.resonance.installed.
    //   LIVE — the CURRENT class's equipped set + identity: cs.equip.equipList[slot].itemUuid,
    //     cs.mod.modSlots[slot], cs.professionList.curProfessionId, and that class's
    //     talentList[curProf] stage + allocated nodes. Byte-for-byte the same row shape RefreshChunk
    //     emits, so the SAME pinned ParseLiveLine parses both.
    // Each section that fails appends "<NAME>ERR\t<msg>" INSTEAD of its data row — a silent bail is
    // indistinguishable from "the player changed nothing", which is exactly how the Deep-Slumber
    // empty-capture hid (owner run sea/O1jJepsgKC).
    private const string LiveStateChunk =
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
        " local le=\"\" local lm=\"\" local lp=0 local lstage=0 local lnodes=\"\"" +
        " local lvOk,lvErr=pcall(function()" +
        "  local cs=(Z.ContainerMgr).CharSerialize" +
        "  pcall(function() local el=(cs.equip).equipList if el~=nil then for s,info in pairs(el) do if info~=nil and info.itemUuid~=nil then le=(le==\"\" and \"\" or le..\",\")..tostring(s)..\":\"..tostring(info.itemUuid) end end end end)" +
        "  pcall(function() local ms=(cs.mod).modSlots if ms~=nil then for s,u in pairs(ms) do lm=(lm==\"\" and \"\" or lm..\",\")..tostring(s)..\":\"..tostring(u) end end end)" +
        "  lp=(cs.professionList).curProfessionId or 0" +
        "  pcall(function() local ti=((cs.professionList).talentList)[lp] if ti~=nil then lstage=ti.talentStageCfgId or 0 if ti.talentNodeIds~=nil then for _,nid in ipairs(ti.talentNodeIds) do lnodes=(lnodes==\"\" and tostring(nid)) or (lnodes..\",\"..tostring(nid)) end end end end)" +
        " end)" +
        " local out" +
        " if resOk then out=\"RES\\t\"..res else out=\"RESERR\\t\"..tostring(resErr) end" +
        " if slOk then out=out..\"\\nRESSLOT\\t\"..sl else out=out..\"\\nRESSLOTERR\\t\"..tostring(slErr) end" +
        " if lvOk then out=out..\"\\nLIVE\\t\"..le..\"\\t\"..lm..\"\\t\"..tostring(lp)..\"\\t\"..tostring(lstage)..\"\\t\"..lnodes" +
        " else out=out..\"\\nLIVEERR\\t\"..tostring(lvErr) end" +
        " rawset(_G,\"" + LiveStateGlobal + "\", out)";
}
