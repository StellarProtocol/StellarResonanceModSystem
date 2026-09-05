using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Lua-bridge reflection-resolution + chunk builders + Lua-global reads for
/// <see cref="PandaFashionProbe"/>. Resolves the game's <b>tolua#</b> <c>LuaState</c> +
/// <c>DoString</c> entry point identically to <see cref="PandaLoadoutProbe"/> (static property
/// <c>ZLuaFramework.LuaState.mainState</c> + <c>void DoString(string,string)</c>), then drives the
/// fashion (wardrobe) system:
/// <list type="bullet">
/// <item><b>Capture</b> reads <c>Z.ContainerMgr.CharSerialize.fashion.wearInfo</c> (a region→fashionId
/// map — the game's own <c>fashion_vm.lua</c> iterates it value-form, so it is not the nil-value
/// zcontainer trap) into a Lua global.</item>
/// <item><b>Apply</b> sends <c>WorldProxy.FashionWear({fasionTypeToFasionIdMap = &lt;map&gt;}, token)</c>
/// (the game's own field name typo is verbatim) and replicates the VM's post-RPC work
/// (<c>OnFashionWearChange</c> dispatch + <c>fashion</c> VM <c>RefreshWearAttr</c>) so the local render
/// refreshes without the fashion window open. Returns the bare game code inline (0 = ok).</item>
/// </list>
/// Async RPC runs inside the canonical <c>Z.CoroUtil.create_coro_xpcall(fn)()</c> wrapper with
/// <c>ZUtil.ZCancelSource.NeverCancelToken</c> (REQUIRED — the RPC yields; a nil token never resumes).
/// Results are read back from Lua globals via the <c>LuaState</c> string indexer, decoding the
/// IL2CPP-wrapped string with <c>IL2CPP.Il2CppStringToManaged</c>.
/// </summary>
internal sealed partial class PandaFashionProbe
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private const string ChunkName = "Stellar.Wardrobe";
    private const string WornGlobal = "_StellarWardrobeWorn";
    private const string ApplyGlobal = "_StellarWardrobeApply";
    private const string NeverCancelToken = "ZUtil.ZCancelSource.NeverCancelToken";

    private volatile bool _bridgeResolved;
    private bool _resolutionFailureLogged;

    private MethodInfo? _mainStateGetter;
    private MethodInfo? _doString;
    private MethodInfo? _getItem;

    private int _resolveTickCounter;
    private const int ResolveAttemptEveryTicks = 60;

    /// <summary>Proactively resolve the Lua bridge off the Update tick (throttled). No-op once resolved.</summary>
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

    // Runs a chunk via DoString. Returns false on any marshalling failure (caller maps it to a
    // dispatch failure); a Lua-side error is reported by the game's own xpcall handler under
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
            _log.Warning($"[Stellar][Wardrobe] Lua dispatch threw: {inner.GetType().Name}: {inner.Message}");
            return false;
        }
    }

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

    // ── Chunk builders ──────────────────────────────────────────────────────────

    // Capture chunk: read cs.fashion.wearInfo (region→fashionId). The game's own fashion_vm.lua
    // iterates this map value-form (`for region,id in pairs(...) do wear[region]=id`), so it is a plain
    // value-yielding table — NOT the nil-value zcontainer trap. Prefix "R" once CharSerialize.fashion is
    // present so C# can tell "in world, empty wardrobe" from "not ready". The weapon-skin read
    // (WeaponCaptureLua, PandaFashionProbe.WeaponSkin.cs) is spliced in after the outfit and writes its own
    // global. No interpolation — no injection.
    internal const string CaptureChunk =
        "(Z.CoroUtil.create_coro_xpcall(function()" +
        " local cs=(Z.ContainerMgr).CharSerialize" +
        " local out=\"\"" +
        " if cs~=nil and cs.fashion~=nil then out=\"R\"" +
        "  local fw=(cs.fashion).wearInfo" +
        "  if fw~=nil then pcall(function() for region,fid in pairs(fw) do out=out..\";\"..tostring(region)..\":\"..tostring(fid) end end) end" +
        " end" +
        " rawset(_G,\"" + WornGlobal + "\", out)" +
        WeaponCaptureLua +
        " end))()";

    // Clears the apply result global before a dispatch so a stale value isn't read.
    private const string ClearApplyGlobalChunk = "rawset(_G,\"" + ApplyGlobal + "\", nil)";

    // Apply chunk: send FashionWear with the target region→fashionId map (all 14 regions, 0 = empty),
    // then, on ok, replicate the VM's post-RPC client work (OnFashionWearChange dispatch per non-zero id +
    // fashion VM RefreshWearAttr) so the local render refreshes with the fashion window closed. Both replays
    // are pcall-guarded so a missing VM/event never fails the apply. Region keys + fashionIds are ints we
    // control, interpolated via InvariantCulture — no injection surface. Returns the bare game code inline.
    private string BuildApplyChunk(IReadOnlyDictionary<int, int> outfit)
    {
        var map = new StringBuilder();
        foreach (var region in WardrobeRegions.All)
        {
            outfit.TryGetValue(region, out var id);
            if (map.Length > 0) map.Append(',');
            map.Append('[').Append(region.ToString(CultureInfo.InvariantCulture)).Append("]=")
               .Append(id.ToString(CultureInfo.InvariantCulture));
        }

        return string.Format(
            CultureInfo.InvariantCulture,
            "(Z.CoroUtil.create_coro_xpcall(function()" +
            " local map={{{0}}}" +
            " local wp=require(\"zproxy.world_proxy\")" +
            " local ret=(wp.FashionWear)({{fasionTypeToFasionIdMap = map}}, {1})" +
            " if ret==0 then" +
            "  pcall(function() for _,id in pairs(map) do if id~=0 then (Z.EventMgr):Dispatch(((Z.ConstValue).SteerEventName).OnFashionWearChange, id) end end end)" +
            "  pcall(function() (Z.VMMgr.GetVM(\"fashion\")).RefreshWearAttr() end)" +
            " end" +
            " rawset(_G,\"{2}\", tostring(ret))" +
            " end))()",
            map.ToString(), NeverCancelToken, ApplyGlobal);
    }

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
