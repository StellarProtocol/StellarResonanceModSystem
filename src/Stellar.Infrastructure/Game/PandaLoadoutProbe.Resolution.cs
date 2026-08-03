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
    // currentTalentStageCfgId/talentNodeIds into the data global. The allocated node ids
    // come from the CONFIRMED per-profession container path used by the game's own
    // talent_skill_vm.GetWeaponActiveTalentTreeNode:
    // Z.ContainerMgr.CharSerialize.professionList.talentList[professionId].talentNodeIds
    // (repeated uint32) — read nil-safely so a missing container just yields an empty node
    // list (the site then shows the recommended build, never a crash). Run inside the
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
        "  local stage=(pd and pd.currentTalentStageCfgId) or 0" +
        "  local nodes=\"\"" +
        "  if tl and tl[prof] and tl[prof].talentNodeIds then for _,nid in ipairs(tl[prof].talentNodeIds) do nodes=(nodes==\"\" and tostring(nid)) or (nodes..\",\"..tostring(nid)) end end" +
        // Per-class gear/modules (2026-08-03): serialize this plan's equipInfoMap + modInfoMap as
        // "slot:uuid,slot:uuid" (cols 6,7). The maps are pairs-iterable (the game does the same:
        // equip_vm.IsEquipByOtherPlan). C# resolves each uuid -> full gear/module via itemPackage.
        "  local eq=\"\" if pd and pd.equipInfoMap then for s,u in pairs(pd.equipInfoMap) do eq=(eq==\"\" and \"\" or eq..\",\")..tostring(s)..\":\"..tostring(u) end end" +
        "  local md=\"\" if pd and pd.modInfoMap then for s,u in pairs(pd.modInfoMap) do md=(md==\"\" and \"\" or md..\",\")..tostring(s)..\":\"..tostring(u) end end" +
        "  out=out..\"\\n\"..tostring(pid)..\"\\t\"..nm..\"\\t\"..tostring(prof)..\"\\t\"..tostring(stage)..\"\\t\"..nodes..\"\\t\"..eq..\"\\t\"..md end end" +
        // Live overlay: the CURRENT class's actually-equipped set — cs.equip.equipList[slot].itemUuid +
        // cs.mod.modSlots[slot]. This is the LIVE container (reflects manual equips/refines/removals) —
        // NOT the method-21 capture latch the C# reader was stuck on. C# overlays this onto the CURRENT
        // plan's saved-loadout gear/modules. "LIVE\t<eq slot:uuid,...>\t<mod slot:uuid,...>".
        " local le=\"\" pcall(function() local el=(cs.equip).equipList if el~=nil then for s,info in pairs(el) do if info~=nil and info.itemUuid~=nil then le=(le==\"\" and \"\" or le..\",\")..tostring(s)..\":\"..tostring(info.itemUuid) end end end end)" +
        " local lm=\"\" pcall(function() local ms=(cs.mod).modSlots if ms~=nil then for s,u in pairs(ms) do lm=(lm==\"\" and \"\" or lm..\",\")..tostring(s)..\":\"..tostring(u) end end end)" +
        " out=out..\"\\nLIVE\\t\"..le..\"\\t\"..lm" +
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
