using System;
using System.Globalization;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Domain.Loadout;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Lua-bridge reflection-resolution + chunk builders + Lua-global reads for
/// <see cref="PandaLoadoutProbe"/>.
///
/// <para>Resolves the game's <b>tolua#</b> <c>LuaState</c> + <c>DoString</c> entry
/// point identically to <see cref="PandaModuleEquipProbe"/> (static property
/// <c>ZLuaFramework.LuaState.mainState</c> + <c>void DoString(string,string)</c>),
/// then drives the loadout ("Role Plan") system through the <c>weapon</c> Lua VM and
/// <c>WorldProxy</c> RPCs — the CONFIRMED mechanism from
/// <c>recon/loadout-switch-findings.md</c> (§ CONFIRMED MECHANISM): the switch goes
/// through the game's own VM wrapper
/// <c>Z.VMMgr.GetVM("weapon").AsyncSwitchRolePlan(planId, token)</c> (which internally
/// calls <c>WorldProxy.SwitchProject</c> and runs the client-side post-switch handling +
/// the game's own success/error toast), and the list/current id come from
/// <c>SyncProjectList</c> cached in the <c>weapon_data</c> model. All async calls run inside the canonical
/// <c>Z.CoroUtil.create_coro_xpcall(fn)()</c> wrapper with the
/// <c>ZUtil.ZCancelSource.NeverCancelToken</c> cancel token (REQUIRED — the RPC
/// yields, and a nil token never resumes).</para>
///
/// <para>Results are read back from Lua globals via the <c>LuaState</c> string
/// indexer, decoding the IL2CPP-wrapped string with
/// <c>IL2CPP.Il2CppStringToManaged</c>.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // chunkName passed to DoString — surfaces as the source label in any Lua
    // traceback the game logs, so a switch/refresh-chunk error is greppable.
    private const string ChunkName = "Stellar.LoadoutSwitch";

    // Lua globals the chunks write their results into; C# reads them back each tick.
    private const string DataGlobal = "_StellarLoadoutData";
    private const string SwitchGlobal = "_StellarLoadoutSwitch";

    // The mandatory cancelToken arg the game passes to async VM/proxy RPCs. NeverCancelToken
    // is the game's own fire-and-forget token; a nil token leaves the await suspended forever.
    private const string NeverCancelToken = "ZUtil.ZCancelSource.NeverCancelToken";

    private volatile bool _bridgeResolved;
    private bool _resolutionFailureLogged;

    private MethodInfo? _mainStateGetter;   // static LuaState mainState { get; }
    private MethodInfo? _doString;          // void DoString(string chunk, string chunkName)
    private MethodInfo? _getItem;           // object get_Item(string global) — Lua string indexer

    private int _resolveTickCounter;
    private const int ResolveAttemptEveryTicks = 60;

    /// <summary>
    /// Proactively resolve the Lua bridge off the Update tick (throttled) so
    /// <see cref="PandaLoadoutProbe.IsResolved"/> / <c>ILoadout.IsAvailable</c> flips
    /// true WITHOUT requiring an apply dispatch. No-op once resolved.
    /// </summary>
    internal void TryResolveBridgeIfDue()
    {
        if (_bridgeResolved) return;
        if (_resolveTickCounter++ % ResolveAttemptEveryTicks != 0) return;
        EnsureBridgeResolved();
    }

    private bool EnsureBridgeResolved()
    {
        if (_bridgeResolved) return true;
        try { return TryResolveBridge(); }
        catch (Exception ex)
        {
            OnResolutionFailure($"bridge resolution threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool TryResolveBridge()
    {
        var luaStateType = _typeRegistry.FindType("ZLuaFramework.LuaState")
            ?? _typeRegistry.FindType("LuaInterface.LuaState")
            ?? FindTypeByShortName("LuaState");
        if (luaStateType is null)
        {
            OnResolutionFailure("ZLuaFramework.LuaState type not loaded yet");
            return false;
        }

        _mainStateGetter = luaStateType.GetProperty("mainState", AnyStatic)?.GetGetMethod(nonPublic: true);
        if (_mainStateGetter is null)
        {
            OnResolutionFailure("LuaState.mainState (static property) not found");
            return false;
        }

        _doString = FindDoString(luaStateType);
        if (_doString is null)
        {
            OnResolutionFailure("LuaState.DoString(string,string) not found");
            return false;
        }

        _getItem = luaStateType.GetMethod("get_Item", AnyInstance, binder: null,
            types: new[] { typeof(string) }, modifiers: null);

        _bridgeResolved = true;
        OnResolutionSucceeded();
        return true;
    }

    private static MethodInfo? FindDoString(Type luaStateType)
    {
        foreach (var m in luaStateType.GetMethods(AnyInstance))
        {
            if (m.Name != "DoString" || m.IsGenericMethodDefinition) continue;
            if (m.ReturnType != typeof(void)) continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
            {
                return m;
            }
        }
        return null;
    }

    private object? GetMainLuaState()
    {
        if (_mainStateGetter is null) return null;
        try { return _mainStateGetter.Invoke(null, Array.Empty<object>()); }
        catch { return null; }
    }

    // Runs a chunk via DoString. Returns false on any marshalling failure so the
    // caller maps it to GameApiUnavailable; a Lua-side error (failed pre-flight /
    // refusal EErrorCode) is reported by the game's own xpcall handler under
    // ChunkName + cached in the result global, not thrown as a C# exception.
    private bool InvokeChunk(string chunk)
    {
        var state = GetMainLuaState();
        if (state is null)
        {
            OnResolutionFailure("LuaState.mainState returned null at dispatch");
            return false;
        }
        if (_doString is null) return false;

        try
        {
            _doString.Invoke(state, new object[] { chunk, ChunkName });
            return true;
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            _log.Warning($"[Stellar][Loadout] Lua dispatch threw: {inner.GetType().Name}: {inner.Message} | chunk={chunk}");
            return false;
        }
    }

    // Reads one Lua string global via the tolua# LuaState string indexer, decoding
    // the IL2CPP-wrapped result. Returns null if the bridge / indexer is unresolved
    // or the global is unset.
    private string? ReadLuaGlobalString(string globalName)
    {
        var state = GetMainLuaState();
        if (state is null || _getItem is null) return null;
        try
        {
            var text = CoerceLuaString(_getItem.Invoke(state, new object[] { globalName }));
            return string.Equals(text, "Il2CppSystem.Object", StringComparison.Ordinal) ? null : text;
        }
        catch { return null; }
    }

    // The tolua# LuaState string indexer returns the Lua string boxed as an
    // Il2CppSystem.Object whose managed ToString() yields the wrapper type name, not
    // the content. Decode the underlying IL2CPP string via the interop runtime.
    private static string? CoerceLuaString(object? val)
    {
        if (val is null) return null;
        if (val is string s) return s;
        if (val is Il2CppObjectBase ob)
        {
            try
            {
                var ptr = ob.Pointer;
                if (ptr != IntPtr.Zero) return IL2CPP.Il2CppStringToManaged(ptr);
            }
            catch { /* not an IL2CPP string — fall through */ }
        }
        return val.ToString();
    }

    // ── Chunk builders ─────────────────────────────────────────────────────────

    // Refresh chunk: fire SyncProjectList (AsyncGetRolePlanData) to populate
    // weapon_data, then serialize CurPlanId + each plan's id/name/professionId/
    // talentStageCfgId/talentNodeIds into the data global. BOTH the talent stage AND the
    // allocated node ids come from the SAME per-profession container object —
    // Z.ContainerMgr.CharSerialize.professionList.talentList[professionId]. The node-read path
    // (talentNodeIds) is the one CONFIRMED against the game's own
    // talent_skill_vm.GetWeaponActiveTalentTreeNode; talentStageCfgId lives alongside it in that
    // same entry, so reading both from it keeps the uploaded stage matching the tree its nodes
    // populate. (The
    // saved plan's pd.currentTalentStageCfgId is a STALE latch — it drifts from the live nodes,
    // which surfaced as a wrong spec on the site, run sea/ZEEJjddKHN; kept only as a nil-safe
    // fallback.) All read nil-safely so a missing container just yields an empty node list (the
    // site then shows the recommended build, never a crash). Run inside the
    // canonical coroutine wrapper (the RPC yields). No external text is interpolated — no
    // Lua-injection surface.
    private const string RefreshChunk =
        "(Z.CoroUtil.create_coro_xpcall(function()" +
        " local token=(ZUtil.ZCancelSource).NeverCancelToken" +
        " Z.VMMgr.GetVM(\"weapon\").AsyncGetRolePlanData(token)" +
        " local wd=Z.DataMgr.Get(\"weapon_data\") local d=wd.rolePlanServerData_" +
        " local cs=(Z.ContainerMgr).CharSerialize" +
        " local tl=(cs.professionList).talentList" +
        " local out=\"CUR=\"..tostring(d.CurPlanId)" +
        " if d.PlanDataDict then for pid,pd in pairs(d.PlanDataDict) do" +
        "  local nm=(pd and pd.projectName~=nil and pd.projectName~=\"\") and pd.projectName or (\"Loadout \"..tostring(pid))" +
        "  local prof=(pd and pd.professionId) or 0" +
        "  local stage=(tl and tl[prof] and tl[prof].talentStageCfgId) or ((pd and pd.currentTalentStageCfgId) or 0)" +
        "  local nodes=\"\"" +
        "  if tl and tl[prof] and tl[prof].talentNodeIds then for _,nid in ipairs(tl[prof].talentNodeIds) do nodes=(nodes==\"\" and tostring(nid)) or (nodes..\",\"..tostring(nid)) end end" +
        // Per-class gear/modules (2026-08-03): serialize this plan's equipInfoMap + modInfoMap as
        // "slot:uuid,slot:uuid" (cols 6,7). The maps are pairs-iterable (the game does the same:
        // equip_vm.IsEquipByOtherPlan). C# resolves each uuid -> full gear/module via itemPackage.
        "  local eq=\"\" if pd and pd.equipInfoMap then for s,u in pairs(pd.equipInfoMap) do eq=(eq==\"\" and \"\" or eq..\",\")..tostring(s)..\":\"..tostring(u) end end" +
        "  local md=\"\" if pd and pd.modInfoMap then for s,u in pairs(pd.modInfoMap) do md=(md==\"\" and \"\" or md..\",\")..tostring(s)..\":\"..tostring(u) end end" +
        "  out=out..\"\\n\"..tostring(pid)..\"\\t\"..nm..\"\\t\"..tostring(prof)..\"\\t\"..tostring(stage)..\"\\t\"..nodes..\"\\t\"..eq..\"\\t\"..md end end" +
        // Live overlay: the CURRENT class's actually-equipped set — cs.equip.equipList[slot].itemUuid +
        // cs.mod.modSlots[slot] — PLUS its profession id + allocated talents from
        // cs.professionList.talentList[curProfessionId] (the exact container the game's own
        // talent_skill_vm reads). This is the LIVE container (reflects manual equips/refines/removals) —
        // NOT the method-21 capture latch the C# reader was stuck on. C# overlays this onto the CURRENT
        // plan's saved-loadout gear/modules AND, when the current class has NO saved plan, uses it as the
        // sole source of that class's loadout (owner requirement 2026-08-05 — capture live-current, not the
        // saved loadout). "LIVE\t<eq slot:uuid,...>\t<mod slot:uuid,...>\t<curProf>\t<talentStage>\t<talentNodes csv>".
        " local le=\"\" pcall(function() local el=(cs.equip).equipList if el~=nil then for s,info in pairs(el) do if info~=nil and info.itemUuid~=nil then le=(le==\"\" and \"\" or le..\",\")..tostring(s)..\":\"..tostring(info.itemUuid) end end end end)" +
        " local lm=\"\" pcall(function() local ms=(cs.mod).modSlots if ms~=nil then for s,u in pairs(ms) do lm=(lm==\"\" and \"\" or lm..\",\")..tostring(s)..\":\"..tostring(u) end end end)" +
        " local lp=(cs.professionList).curProfessionId" +
        " local lstage=0 local lnodes=\"\"" +
        " pcall(function() local ti=((cs.professionList).talentList)[lp] if ti~=nil then lstage=ti.talentStageCfgId or 0 if ti.talentNodeIds~=nil then for _,nid in ipairs(ti.talentNodeIds) do lnodes=(lnodes==\"\" and tostring(nid)) or (lnodes..\",\"..tostring(nid)) end end end end)" +
        " out=out..\"\\nLIVE\\t\"..le..\"\\t\"..lm..\"\\t\"..tostring(lp)..\"\\t\"..tostring(lstage)..\"\\t\"..lnodes" +
        // Deep-Slumber Psychoscope (season cultivate) — owner-verified gap (2026-08-19): the C#
        // reflection mirror (PandaInventoryPullReader.ReadDeepSlumber) populates the SAME containers
        // LAZILY (empty until the player opens the Psychoscope UI at least once this session), so a
        // fresh session's archive uploaded no Deep-Slumber block. This reads the LUA mirror instead —
        // populated at login, the same source the game's own season views read. Field names are the
        // Lua mirror's lowercase-camel shape (recon-pinned), NOT the C# "__Value" forms the reflection
        // walk uses. Two INDEPENDENT pcalls (season levels / cultivate lines) so a missing container on
        // one side still yields the other's rows — never breaks the rest of this dump.
        // "DSLV\t<seasonId>:<level>,..." — cs.seasonRoleLevelData.seasonRoleLevelMap.
        " local dslv=\"\" pcall(function()" +
        "  local srl=(cs.seasonRoleLevelData) and (cs.seasonRoleLevelData).seasonRoleLevelMap" +
        "  if srl~=nil then for sid,sl in pairs(srl) do" +
        "   dslv=(dslv==\"\" and \"\" or dslv..\",\")..tostring(sid)..\":\"..tostring(sl and sl.level or 0)" +
        "  end end" +
        " end)" +
        " out=out..\"\\nDSLV\\t\"..dslv" +
        // One "DSA\t<lineId>\t<subType>\t<areaId>\t<0|1 active>\t<score>\t<big>\t<middle>\t<normal>" row per
        // (lineId, subType, areaId) variant — cs.seasonCultivateLineData.seasonCultivateLineMap ->
        // cultivateLineMap (by subType) -> cultivateLineDataMap (by areaId); each node map serialized as
        // "nodeId:value,..." (fantasyId / itemId / activeLevel for big / middle / normal respectively).
        " pcall(function()" +
        "  local scl=(cs.seasonCultivateLineData) and (cs.seasonCultivateLineData).seasonCultivateLineMap" +
        "  if scl~=nil then for lid,ld in pairs(scl) do local clm=ld and ld.cultivateLineMap" +
        "   if clm~=nil then for st,subd in pairs(clm) do local cldm=subd and subd.cultivateLineDataMap" +
        "    if cldm~=nil then for aid,ar in pairs(cldm) do" +
        "     local active=(ar and ar.isActive) and 1 or 0" +
        "     local score=(ar and ar.activateEffectScore) or 0" +
        "     local big=\"\" local bm=ar and ar.cultivateBigNodeMap" +
        "     if bm~=nil then for nid,nv in pairs(bm) do big=(big==\"\" and \"\" or big..\",\")..tostring(nid)..\":\"..tostring(nv and nv.fantasyId or 0) end end" +
        "     local mid=\"\" local mm=ar and ar.cultivateMiddleNodeMap" +
        "     if mm~=nil then for nid,nv in pairs(mm) do mid=(mid==\"\" and \"\" or mid..\",\")..tostring(nid)..\":\"..tostring(nv and nv.itemId or 0) end end" +
        "     local nor=\"\" local nm=ar and ar.cultivateNormalNodeMap" +
        "     if nm~=nil then for nid,nv in pairs(nm) do nor=(nor==\"\" and \"\" or nor..\",\")..tostring(nid)..\":\"..tostring(nv and nv.activeLevel or 0) end end" +
        "     out=out..\"\\nDSA\\t\"..tostring(lid)..\"\\t\"..tostring(st)..\"\\t\"..tostring(aid)..\"\\t\"..tostring(active)..\"\\t\"..tostring(score)..\"\\t\"..big..\"\\t\"..mid..\"\\t\"..nor" +
        "    end end end end end end" +
        " end)" +
        " rawset(_G,\"" + DataGlobal + "\", out)" +
        " end))()";

    // Switch chunk: drive the game's OWN weapon-VM wrapper AsyncSwitchRolePlan(planId,
    // token) inside the coroutine wrapper — i.e. EXACTLY what clicking the in-game
    // loadout dropdown does. The wrapper reads oldProjectId from
    // weaponData.rolePlanServerData_.CurPlanId, calls WorldProxy.SwitchProject, then runs
    // the client-side post-switch handling (SaveRolePlanId, current-project sync cache,
    // OnRolePlanChange event dispatch) AND shows the game's own success/error toast — none
    // of which the raw RPC does (skipping it corrupted local player state after a
    // class-changing switch). The wrapper returns a bool (true=success); cache its
    // tostring() in the switch global. planId is a numeric int interpolated via
    // InvariantCulture — no injection surface. The server runs every validation
    // (combat-lock etc.) and the game toasts the reason itself.
    private static string BuildSwitchChunk(int planId)
        => string.Format(
            CultureInfo.InvariantCulture,
            "(Z.CoroUtil.create_coro_xpcall(function()" +
            " local ok=Z.VMMgr.GetVM(\"weapon\").AsyncSwitchRolePlan({0}, {1})" +
            " rawset(_G,\"{2}\", tostring(ok))" +
            " end))()",
            planId, NeverCancelToken, SwitchGlobal);

    // Clears the switch result global before a dispatch so a stale value isn't read.
    private const string ClearSwitchGlobalChunk = "rawset(_G,\"" + SwitchGlobal + "\", nil)";

    // Diagnostic-only global written by ProbeChunk (per-class gear RE, 2026-08-03).
    private const string EquipProbeGlobal = "_StellarEquipProbe";

    // Diagnostic-only global written by LiveProbeChunk (partial-account modules/talents RE, 2026-08-05).
    private const string LiveProbeGlobal = "_StellarLiveProbe";

    // LIVE-CONTAINER PROBE (diagnostics only, 2026-08-05). A NEW/PARTIAL account (owner's alt Ribery, no
    // saved role-plans) uploads modules=0 gear=0 talentStageId=None talentNodes=null even though the game
    // plainly shows 20/36 allocated talent nodes and configured module presets. The production RefreshChunk
    // reads talents ONLY inside the plan loop (tl[pd.professionId]) — so with zero plans it never touches the
    // talent container — and reads live modules from cs.mod.modSlots (empty). This probe dumps the game's OWN
    // authoritative live containers, indexed by the CURRENT profession the game itself uses
    // (cs.professionList.curProfessionId — what weapon_vm.GetCurWeapon returns), to distinguish "genuinely
    // empty" from "populated but our read path never reached it / pairs does not enumerate an IL2CPP dict":
    //   • Talents  = cs.professionList.talentList[prof]{talentNodeIds, talentStageCfgId, usedTalentPoints}
    //                — the EXACT path talent_skill_vm.GetWeaponActiveTalentTreeNode / CheckTalentIsActive use.
    //                Dumped for EVERY prof key present AND direct-indexed by curProfessionId (the game does
    //                talentList[professionId] directly; pairs may not enumerate an IL2CPP map).
    //   • Modules  = cs.mod.modSlots (equipped-per-slot: counted BOTH via pairs AND a numeric [1..14] index
    //                scan, because mod_vm reads it as modSlots[i]) + cs.mod.modInfos (owned-module inventory,
    //                keyed by uuid — may hold the "configured but not equipped" set the owner described).
    //   • Context  = curProfessionId, CurPlanId, PlanDataDict count.
    // Read-only, no interpolation (no injection surface), every risky access pcall-guarded so one nil section
    // never aborts the dump, coroutine-wrapped like the production chunks. Owner runs it ONCE on the broken
    // account; the fix follows from the real data (docs/agent-process-rules.md § 31 — probe, do not guess).
    private const string LiveProbeChunk =
        "(Z.CoroUtil.create_coro_xpcall(function()" +
        " local cs=(Z.ContainerMgr).CharSerialize" +
        " local out=\"\"" +
        " local pl=cs.professionList" +
        " local curProf=pl and pl.curProfessionId" +
        " out=\"CURPROF=\"..tostring(curProf)..\"\\n\"" +
        // Talent container — every profession key present.
        " local tl=pl and pl.talentList" +
        " if tl~=nil then local tc=0" +
        "  pcall(function() for prof,ti in pairs(tl) do tc=tc+1" +
        "   local nn=0 local nl=\"\"" +
        "   pcall(function() if ti.talentNodeIds~=nil then for _,nid in ipairs(ti.talentNodeIds) do nn=nn+1 if nn<=24 then nl=(nl==\"\" and tostring(nid)) or (nl..\",\"..tostring(nid)) end end end end)" +
        "   out=out..\"talentList[\"..tostring(prof)..\"] nodes#\"..tostring(nn)..\" stage=\"..tostring(ti.talentStageCfgId)..\" used=\"..tostring(ti.usedTalentPoints)..\" [\"..nl..\"]\\n\" end end)" +
        "  out=out..\"talentList.pairsCount#\"..tostring(tc)..\"\\n\"" +
        // Direct index by the current profession (the game's own access shape) — proves whether the
        // container exists for curProf even if pairs did not enumerate it.
        "  pcall(function() local ti=tl[curProf] if ti~=nil then local nn=0" +
        "   pcall(function() if ti.talentNodeIds~=nil then for _,nid in ipairs(ti.talentNodeIds) do nn=nn+1 end end end)" +
        "   out=out..\"talentList[CURPROF] present nodes#\"..tostring(nn)..\" stage=\"..tostring(ti.talentStageCfgId)..\" used=\"..tostring(ti.usedTalentPoints)..\"\\n\"" +
        "  else out=out..\"talentList[CURPROF] NIL\\n\" end end)" +
        " else out=out..\"professionList.talentList=nil\\n\" end" +
        // Module container — equipped slots (pairs + numeric scan) and owned inventory.
        " local mod=cs.mod" +
        " if mod~=nil then" +
        "  local ms=mod.modSlots" +
        "  if ms~=nil then local pc=0 local pcl=\"\"" +
        "   pcall(function() for s,u in pairs(ms) do pc=pc+1 if pc<=16 then pcl=(pcl==\"\" and \"\" or pcl..\",\")..tostring(s)..\":\"..tostring(u) end end end)" +
        "   out=out..\"modSlots.pairs#\"..tostring(pc)..\" [\"..pcl..\"]\\n\"" +
        "   local ic=0 local icl=\"\"" +
        "   pcall(function() for i=1,14 do local u=ms[i] if u~=nil then ic=ic+1 icl=(icl==\"\" and \"\" or icl..\",\")..tostring(i)..\":\"..tostring(u) end end end)" +
        "   out=out..\"modSlots.index1_14#\"..tostring(ic)..\" [\"..icl..\"]\\n\"" +
        "  else out=out..\"mod.modSlots=nil\\n\" end" +
        "  local mi=mod.modInfos" +
        "  if mi~=nil then local c=0 local il=\"\"" +
        "   pcall(function() for uuid,info in pairs(mi) do c=c+1 if c<=16 then il=(il==\"\" and \"\" or il..\",\")..tostring(uuid) end end end)" +
        "   out=out..\"modInfos.pairs#\"..tostring(c)..\" [\"..il..\"]\\n\"" +
        "  else out=out..\"mod.modInfos=nil\\n\" end" +
        " else out=out..\"CharSerialize.mod=nil\\n\" end" +
        // Plan context (why the production read paths were empty).
        " pcall(function() local sd=(Z.DataMgr.Get(\"weapon_data\")).rolePlanServerData_" +
        "  local pdc=0 if sd~=nil and sd.PlanDataDict~=nil then for _ in pairs(sd.PlanDataDict) do pdc=pdc+1 end end" +
        "  out=out..\"CurPlanId=\"..tostring(sd and sd.CurPlanId)..\" PlanDataDict#\"..tostring(pdc)..\"\\n\" end)" +
        " rawset(_G,\"" + LiveProbeGlobal + "\", out)" +
        " end))()";

    // EQUIP/MOD RESOLUTION PROBE (diagnostics only, 2026-08-03 iter 2). The per-class gear+modules are
    // NOT on the live wire ([ClassGearDiag]-proven) — but the game's OWN Lua (weapon_vm.CheckRolePlanIsChange,
    // equip_vm.IsEquipByOtherPlan, items_vm.GetItemInfobyItemId) shows the exact paths:
    //   • per-plan gear   = weapon_data.rolePlanServerData_.PlanDataDict[planId].equipInfoMap[slot] = itemUuid
    //   • per-plan modules= …PlanDataDict[planId].modInfoMap[slot] = moduleUuid
    //   • uuid -> detail  = CharSerialize.itemPackage.packages[*].items[uuid] (configId + equipAttr + modNewAttr)
    // The plan *maps* are pairs-iterable (the game does `for _,u in pairs(planInfo.equipInfoMap)`), so this
    // dumps slot->uuid per plan reliably. The single empirical unknown §29 must confirm before implementing:
    // do a NON-ACTIVE plan's uuids RESOLVE in itemPackage (FOUND/NIL)? — plus prove the roll/part carriers
    // (equipAttr.totalRecastCount/perfectionValue, modNewAttr.modParts) are populated for a non-active item.
    // The full equipAttr roll-field enumeration is done at implement-time via C# GetProperties (Lua pairs
    // does NOT enumerate an IL2CPP object's named properties). Read-only, no interpolation, coroutine-wrapped.
    private const string ProbeChunk =
        "(Z.CoroUtil.create_coro_xpcall(function()" +
        " local sd=(Z.DataMgr.Get(\"weapon_data\")).rolePlanServerData_" +
        " local cs=(Z.ContainerMgr).CharSerialize" +
        " local out=\"\"" +
        " local function findItem(uuid) local ok,res=pcall(function()" +
        "   local pkgs=(cs.itemPackage).packages" +
        "   for _,pkg in pairs(pkgs) do local it=(pkg.items)[uuid] if it~=nil then return it end end end)" +
        "  if ok then return res end end" +
        " local cur=sd and sd.CurPlanId" +
        " out=\"CUR=\"..tostring(cur)..\"\\n\"" +
        " local se,sm=nil,nil" +
        " if sd~=nil and sd.PlanDataDict~=nil then local n=0 for pid,pd in pairs(sd.PlanDataDict) do" +
        "  out=out..\"PLAN \"..tostring(pid)..\" prof=\"..tostring(pd.professionId)..((pid==cur) and \" [CUR]\" or \"\")..\"\\n\"" +
        "  pcall(function() local ei=pd.equipInfoMap if ei~=nil then local m=0 for slot,uuid in pairs(ei) do out=out..\"  eq[\"..tostring(slot)..\"]=\"..tostring(uuid)..\"\\n\" if pid~=cur and se==nil then se=uuid end m=m+1 if m>=12 then break end end else out=out..\"  equipInfoMap=nil\\n\" end end)" +
        "  pcall(function() local mi=pd.modInfoMap if mi~=nil then local m=0 for slot,uuid in pairs(mi) do out=out..\"  mod[\"..tostring(slot)..\"]=\"..tostring(uuid)..\"\\n\" if pid~=cur and sm==nil then sm=uuid end m=m+1 if m>=12 then break end end else out=out..\"  modInfoMap=nil\\n\" end end)" +
        "  n=n+1 if n>=6 then break end end end" +
        " if se~=nil then local it=findItem(se) out=out..\"RESOLVE eq=\"..tostring(se)..\" -> \"..((it~=nil) and \"FOUND\" or \"NIL\")..\"\\n\"" +
        "  if it~=nil then pcall(function() out=out..\"  configId=\"..tostring(it.configId) end)" +
        "   pcall(function() local ea=it.equipAttr out=out..\" equipAttr=\"..((ea~=nil) and \"present\" or \"nil\") if ea~=nil then out=out..\" recast=\"..tostring(ea.totalRecastCount)..\" perfection=\"..tostring(ea.perfectionValue) end end)" +
        "   out=out..\"\\n\" end" +
        " else out=out..\"(no non-active eq uuid)\\n\" end" +
        " if sm~=nil then local it=findItem(sm) out=out..\"RESOLVE mod=\"..tostring(sm)..\" -> \"..((it~=nil) and \"FOUND\" or \"NIL\")..\"\\n\"" +
        "  if it~=nil then pcall(function() out=out..\"  configId=\"..tostring(it.configId) end)" +
        "   pcall(function() local mn=it.modNewAttr out=out..\" modNewAttr=\"..((mn~=nil) and \"present\" or \"nil\") if mn~=nil then local mp=mn.modParts if mp~=nil then local c=0 out=out..\" modParts=[\" for _,p in ipairs(mp) do out=out..tostring(p)..\",\" c=c+1 if c>=12 then break end end out=out..\"]#\"..tostring(c) else out=out..\" modParts=nil\" end end end)" +
        "   out=out..\"\\n\" end" +
        " else out=out..\"(no non-active mod uuid)\\n\" end" +
        " rawset(_G,\"" + EquipProbeGlobal + "\", out)" +
        " end))()";

    private static Type? FindTypeByShortName(string shortName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string asmName;
            try { asmName = asm.GetName().Name ?? string.Empty; }
            catch { continue; }
            if (ShouldSkipAssemblyForScan(asmName)) continue;

            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types!; }
            catch { continue; }
            foreach (var t in types)
            {
                if (t is null) continue;
                string name;
                try { name = t.Name; } catch { continue; }
                if (string.Equals(name, shortName, StringComparison.Ordinal)) return t;
            }
        }
        return null;
    }

    private static bool ShouldSkipAssemblyForScan(string asmName)
    {
        if (string.IsNullOrEmpty(asmName)) return false;
        if (asmName.StartsWith("UnityEngine", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("System", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("Microsoft", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("Il2Cpp", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("BepInEx", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("MonoMod", StringComparison.Ordinal)) return true;
        if (asmName.StartsWith("HarmonyX", StringComparison.Ordinal) || asmName == "0Harmony") return true;
        if (asmName.StartsWith("mscorlib", StringComparison.Ordinal) || asmName.StartsWith("netstandard", StringComparison.Ordinal)) return true;
        return false;
    }
}
