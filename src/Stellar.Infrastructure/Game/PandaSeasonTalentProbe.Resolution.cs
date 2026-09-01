using System;
using System.Globalization;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Lua-bridge reflection-resolution + <b>WorldProxy</b> season-talent write-RPC chunk builders +
/// reply parsing for <see cref="PandaSeasonTalentProbe"/>.
///
/// <para>Resolves the game's <b>tolua#</b> <c>LuaState</c> + <c>DoString</c> entry point identically
/// to <see cref="PandaExchangeProbe"/> / <see cref="PandaLoadoutProbe"/> (static
/// <c>ZLuaFramework.LuaState.mainState</c> + <c>void DoString(string,string)</c>), then drives
/// <c>require("zproxy.world_proxy").&lt;Rpc&gt;(requestTable, NeverCancelToken)</c> inside
/// <c>Z.CoroUtil.create_coro_xpcall</c>. Request fields are camelCase; the reply is a BARE
/// <c>EErrorCode</c> number (<c>0</c> = ok), read back via the <c>LuaState</c> string indexer
/// (decoding the IL2CPP-wrapped string) and <c>int.TryParse</c>'d — see
/// <c>docs/driving-game-actions.md</c> § CONFIRMED (spike 2026-08-24).</para>
/// </summary>
internal sealed partial class PandaSeasonTalentProbe
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private const string ChunkName = "Stellar.SeasonTalentWrite";
    private const string NeverCancelToken = "ZUtil.ZCancelSource.NeverCancelToken";

    private volatile bool _bridgeResolved;
    private bool _resolutionFailureLogged;

    private MethodInfo? _mainStateGetter;   // static LuaState mainState { get; }
    private MethodInfo? _doString;          // void DoString(string chunk, string chunkName)
    private MethodInfo? _getItem;           // object get_Item(string global) — Lua string indexer

    private int _resolveTickCounter;
    private const int ResolveAttemptEveryTicks = 60;

    /// <summary>Proactively resolve the Lua bridge off the Update tick (throttled) so
    /// <see cref="IsResolved"/> / <c>IDeepSlumber.IsAvailable</c> flips true without a dispatch.
    /// No-op once resolved.</summary>
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

    // Runs a chunk via DoString. Returns false on any marshalling failure so the caller maps it to
    // UnavailableCode; a Lua-side error (failed pre-flight / refusal) is reported by the game's own
    // xpcall handler under ChunkName, not thrown as a C# exception.
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
            _log.Warning($"[Stellar][SeasonTalent] Lua dispatch threw: {inner.GetType().Name}: {inner.Message} | chunk={chunk}");
            return false;
        }
    }

    // Reads one Lua string global via the tolua# LuaState string indexer, decoding the IL2CPP-wrapped
    // result. Returns null if the bridge / indexer is unresolved or the global is unset.
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

    // The tolua# indexer returns the Lua string boxed as an Il2CppSystem.Object whose managed
    // ToString() yields the wrapper type name, not the content. Decode the underlying IL2CPP string.
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

    // The reply is a bare EErrorCode number (0 = ok). Unparseable → UnavailableCode (never a hang,
    // never treated as success).
    private static int ParseCode(string reply)
        => int.TryParse(reply, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
            ? code
            : UnavailableCode;

    // ── Chunk builders (Approach A — worldProxy.<Rpc>) ───────────────────────────
    // Each runs inside create_coro_xpcall and writes the bare reply code (0 = ok) straight into
    // resultGlobal — no wrapper object, unlike the read-side exchange chunks. Server pushes the new
    // cultivate state back via CharSerialize; the existing Deep-Slumber capture (PandaLoadoutProbe)
    // latches it. No external text is interpolated beyond internal numeric ids — no Lua-injection
    // surface.

    // Enable a cultivate line/area (the "Switch" button). request.zoneId = areaId. Enabling is 1-of-N
    // (server auto-disables the sibling — confirmed in the spike), so no explicit disable is needed.
    private static string EnableChunk(int areaId, string resultGlobal) =>
        $@"(Z.CoroUtil.create_coro_xpcall(function()
  local worldProxy = require(""zproxy.world_proxy"")
  local code = worldProxy.EnableCultivateLine({{ zoneId = {areaId.ToString(CultureInfo.InvariantCulture)} }}, {NeverCancelToken})
  rawset(_G, ""{resultGlobal}"", tostring(code))
end))()";

    // Reset a whole area's tree — every anchor + factor returns to inactive/the bag (the game has no
    // per-node anchor removal). request.zoneId = areaId. Costs the game's reset currency; a raw RPC so
    // it skips the in-game confirm dialog. Server refuses (non-zero code) on combat / insufficient cost.
    private static string ResetChunk(int areaId, string resultGlobal) =>
        $@"(Z.CoroUtil.create_coro_xpcall(function()
  local worldProxy = require(""zproxy.world_proxy"")
  local code = worldProxy.ResetAllNodes({{ zoneId = {areaId.ToString(CultureInfo.InvariantCulture)} }}, {NeverCancelToken})
  rawset(_G, ""{resultGlobal}"", tostring(code))
end))()";

    // Activate a normal node ("Anchor of the Mind") in the currently-active area. request.nodeId. Node
    // ids are area-relative — the game resolves them against the ACTIVE area — so the area must be enabled
    // (and, on a rebuild, reset) before this fires; the caller's phase barrier guarantees that ordering.
    private static string ActivateChunk(int nodeId, string resultGlobal) =>
        $@"(Z.CoroUtil.create_coro_xpcall(function()
  local worldProxy = require(""zproxy.world_proxy"")
  local code = worldProxy.ActiveNormalNode({{ nodeId = {nodeId.ToString(CultureInfo.InvariantCulture)} }}, {NeverCancelToken})
  rawset(_G, ""{resultGlobal}"", tostring(code))
end))()";

    // Socket a phantom factor into a middle node. request.{nodeId, itemConfigId}.
    private static string SocketChunk(int nodeId, int itemId, string resultGlobal) =>
        $@"(Z.CoroUtil.create_coro_xpcall(function()
  local worldProxy = require(""zproxy.world_proxy"")
  local code = worldProxy.InstallItemToMiddleNode({{ nodeId = {nodeId.ToString(CultureInfo.InvariantCulture)}, itemConfigId = {itemId.ToString(CultureInfo.InvariantCulture)} }}, {NeverCancelToken})
  rawset(_G, ""{resultGlobal}"", tostring(code))
end))()";

    // Unsocket a phantom factor. Server request carries request.nodeId ONLY (the VM's configId arg
    // is toast-only — see IDeepSlumberWriteProbe.UnsocketFactorAsync).
    private static string UnsocketChunk(int nodeId, string resultGlobal) =>
        $@"(Z.CoroUtil.create_coro_xpcall(function()
  local worldProxy = require(""zproxy.world_proxy"")
  local code = worldProxy.UnInstallItemToMiddleNode({{ nodeId = {nodeId.ToString(CultureInfo.InvariantCulture)} }}, {NeverCancelToken})
  rawset(_G, ""{resultGlobal}"", tostring(code))
end))()";

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
