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
    // weapon_data, then serialize CurPlanId + each plan's id/name into the data
    // global. Run inside the canonical coroutine wrapper (the RPC yields). No
    // external text is interpolated — no Lua-injection surface.
    private const string RefreshChunk =
        "(Z.CoroUtil.create_coro_xpcall(function()" +
        " local token=(ZUtil.ZCancelSource).NeverCancelToken" +
        " Z.VMMgr.GetVM(\"weapon\").AsyncGetRolePlanData(token)" +
        " local wd=Z.DataMgr.Get(\"weapon_data\") local d=wd.rolePlanServerData_" +
        " local out=\"CUR=\"..tostring(d.CurPlanId)" +
        " if d.PlanDataDict then for pid,pd in pairs(d.PlanDataDict) do" +
        "  local nm=(pd and pd.projectName~=nil and pd.projectName~=\"\") and pd.projectName or (\"Loadout \"..tostring(pid))" +
        "  out=out..\"\\n\"..tostring(pid)..\"\\t\"..nm end end" +
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
