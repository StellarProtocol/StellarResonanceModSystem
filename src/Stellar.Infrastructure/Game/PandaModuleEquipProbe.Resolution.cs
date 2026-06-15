using System;
using System.Globalization;
using System.Reflection;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Lua-bridge reflection-resolution for <see cref="PandaModuleEquipProbe"/>.
///
/// <para>Resolves the game's <b>tolua#</b> <c>LuaState</c> and the
/// <c>DoString</c> entry point used to run a Lua chunk from C#. The surface was
/// confirmed against the <c>ZLuaFramework.dll</c> interop metadata:</para>
/// <list type="bullet">
///   <item><c>static LuaState mainState { get; }</c> — the live main state</item>
///   <item><c>void DoString(string chunk, string chunkName)</c></item>
/// </list>
///
/// <para>Install runs (in a child coroutine — see
/// <see cref="PandaModuleEquipProbe.BuildEquipChunk"/>)
/// <c>Z.VMMgr.GetVM("mod").AsyncEquipMod(uuid, slot)</c>; uninstall
/// <c>… .AsyncUninstallMod(slot)</c> — the exact call the game's own equip
/// buttons make (<c>ui/item_btns/mod_install_btn.lua</c>,
/// <c>ui/view/mod_main_view.lua</c>). A dotted-path <c>Call</c> can't be used:
/// tolua# resolves dotted paths against globals, and <c>ModVM</c> is a
/// module-local reachable only through the <c>Z.VMMgr.GetVM("mod")</c> call.</para>
///
/// <para>Resolution is lazy + retried each call until it succeeds (HybridCLR
/// may not have loaded <c>ZLuaFramework</c> at first call). On hard failure
/// <see cref="PandaModuleEquipProbe.IsResolved"/> stays false and callers get
/// <c>EquipResult.GameApiUnavailable</c>.</para>
/// </summary>
internal sealed partial class PandaModuleEquipProbe
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // chunkName passed to DoString — surfaces as the source label in any Lua
    // traceback the game logs, so an equip-chunk error is greppable.
    private const string ChunkName = "Stellar.ModuleEquip";

    private volatile bool _bridgeResolved;
    private bool _resolutionFailureLogged;

    // Resolved bridge handles (tolua# LuaState — confirmed via ZLuaFramework.dll
    // interop metadata: the main state is the static PROPERTY `mainState`, and a
    // Lua chunk is run via the non-generic void DoString(string, string)).
    private MethodInfo? _mainStateGetter;              // static LuaState mainState { get; }
    private MethodInfo? _doString;                     // void DoString(string chunk, string chunkName)

    // Throttle for the proactive (off-dispatch) resolve. Resolution only needs
    // LuaInterface.LuaState to be loaded (early, post-hot-update), so an attempt
    // ~1s apart resolves shortly after entering the world without churning the
    // Update thread.
    private int _resolveTickCounter;
    private const int ResolveAttemptEveryTicks = 60;

    /// <summary>
    /// Proactively resolve the Lua bridge off the Update tick (throttled) so
    /// <see cref="IsResolved"/> / <c>IModuleEquip.IsAvailable</c> flips true
    /// WITHOUT requiring an equip dispatch. Without this the Apply button — which
    /// is gated on <c>IsAvailable</c> — could never be clicked to trigger the
    /// lazy resolve, a chicken-and-egg deadlock. No-op once resolved.
    /// </summary>
    internal void TryResolveBridgeIfDue()
    {
        if (_bridgeResolved) return;
        if (_resolveTickCounter++ % ResolveAttemptEveryTicks != 0) return;
        EnsureBridgeResolved();
    }

    private bool EnsureBridgeResolved()
    {
        if (_bridgeResolved)
        {
            return true;
        }

        try
        {
            return TryResolveBridge();
        }
        catch (Exception ex)
        {
            OnResolutionFailure($"bridge resolution threw {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool TryResolveBridge()
    {
        // Recon §B puts GetMainState/CallTableFunc on ZLuaFramework.LuaState. The
        // game ALSO ships LuaInterface.LuaState (a tolua wrapper) with NO
        // GetMainState — resolving that one is why the bridge failed
        // ("LuaState.GetMainState() not found"). Prefer the ZLuaFramework type.
        var luaStateType = _typeRegistry.FindType("ZLuaFramework.LuaState")
            ?? _typeRegistry.FindType("LuaInterface.LuaState")
            ?? FindTypeByShortName("LuaState");
        if (luaStateType is null)
        {
            OnResolutionFailure("ZLuaFramework.LuaState type not loaded yet");
            return false;
        }

        // tolua# exposes the main state as a static PROPERTY `mainState`
        // (get_mainState), not a GetMainState() method.
        _mainStateGetter = luaStateType.GetProperty("mainState", AnyStatic)?.GetGetMethod(nonPublic: true);
        if (_mainStateGetter is null)
        {
            DiagLuaStateApi(luaStateType);
            OnResolutionFailure("LuaState.mainState (static property) not found");
            return false;
        }

        // tolua# runs a Lua chunk via the non-generic void DoString(string chunk,
        // string chunkName). Match by signature so the generic T DoString<T>(...)
        // overload (also present) is skipped.
        _doString = FindDoString(luaStateType);
        if (_doString is null)
        {
            DiagLuaStateApi(luaStateType);
            OnResolutionFailure("LuaState.DoString(string,string) not found");
            return false;
        }

        _bridgeResolved = true;
        OnResolutionSucceeded();
        return true;
    }

    // void DoString(string chunk, string chunkName) — non-generic, instance.
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

    // Fetches the live main LuaState. Returns null on any failure.
    private object? GetMainLuaState()
    {
        if (_mainStateGetter is null) return null;
        try { return _mainStateGetter.Invoke(null, Array.Empty<object>()); }
        catch { return null; }
    }

    // Dispatches via DoString: Z.VMMgr.GetVM("mod").AsyncEquipMod(uuid, slot) for
    // install, .AsyncUninstallMod(slot) for uninstall. Returns false on any
    // marshalling failure so the caller maps it to GameApiUnavailable. A Lua-side
    // error (e.g. a failed pre-flight check) is reported by the game's own Lua
    // error handler under ChunkName, not as a C# exception.
    private bool InvokeLuaDispatch(EquipRequest request)
    {
        var state = GetMainLuaState();
        if (state is null)
        {
            OnResolutionFailure("LuaState.mainState returned null at dispatch");
            return false;
        }

        if (_doString is null) return false;

        var chunk = request.IsUninstall
            ? BuildUninstallChunk(ModVmName, AsyncUninstallModFunc, request.SlotId)
            : BuildEquipChunk(ModVmName, AsyncEquipModFunc, request.ModuleUuid, request.SlotId);

        try
        {
            _doString.Invoke(state, new object[] { chunk, ChunkName });
            return true;
        }
        catch (Exception ex)
        {
            // MethodInfo.Invoke wraps the real error in TargetInvocationException;
            // unwrap to the innermost (the Lua/Il2Cpp error message) and include
            // the chunk so a bad dispatch is diagnosable from a single log line.
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            _log.Warning($"[Stellar][ModuleEquip] Lua dispatch threw: {inner.GetType().Name}: {inner.Message} | chunk={chunk}");
            return false;
        }
    }

    // The mandatory cancelToken arg the game passes to every *Mod call. It MUST
    // be non-nil: WorldProxy.InstallMod feeds it as the 6th of coro_util
    // async_to_sync's fixed param_cnt=6, and a nil there leaves the {...} params
    // table 5 long, so async_to_sync's `table.insert(params, 7, cb)` is "out of
    // bounds". NeverCancelToken is the game's own fire-and-forget token (used
    // verbatim for proxy RPCs, e.g. login_vm.lua charactorProxy.Login).
    private const string NeverCancelToken = "ZUtil.ZCancelSource.NeverCancelToken";

    // Builds the install chunk mirroring the game's own equip button:
    // (Z.VMMgr.GetVM("mod")).AsyncEquipMod(uuid, slot, token), launched inside a
    // child coroutine via the game's canonical Z.CoroUtil.create_coro_xpcall(fn)()
    // idiom (used 900+ times game-wide). The coroutine is REQUIRED: AsyncEquipMod
    // -> AsyncInstallMod -> WorldProxy.InstallMod calls coro_util.async_to_sync,
    // which yields the running coroutine until the RPC replies — and DoString
    // runs the chunk on the bare main thread, where coroutine.yield throws.
    // vmName/func are internal constants and uuid/slot are numeric — no external
    // text is interpolated, so there is no Lua-injection surface. The game builds
    // + validates the request; create_coro_xpcall logs any Lua-side error.
    internal static string BuildEquipChunk(string vmName, string func, long uuid, int slot)
        => string.Format(
            CultureInfo.InvariantCulture,
            "(Z.CoroUtil.create_coro_xpcall(function() local vm=Z.VMMgr.GetVM(\"{0}\"); vm.{1}({2}, {3}, {4}) end))()",
            vmName, func, uuid, slot, NeverCancelToken);

    // Builds the uninstall chunk: (Z.VMMgr.GetVM("mod")).AsyncUninstallMod(slot,
    // token), launched in a child coroutine (see BuildEquipChunk for why).
    internal static string BuildUninstallChunk(string vmName, string func, int slot)
        => string.Format(
            CultureInfo.InvariantCulture,
            "(Z.CoroUtil.create_coro_xpcall(function() local vm=Z.VMMgr.GetVM(\"{0}\"); vm.{1}({2}, {3}) end))()",
            vmName, func, slot, NeverCancelToken);

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
