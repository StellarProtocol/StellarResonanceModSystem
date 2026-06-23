using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// RECON-ONLY (throwaway) Lua-bridge partial for <see cref="PandaExchangeReconProbe"/>.
/// Resolves the game's tolua# <c>LuaState.mainState</c> + <c>DoString</c> exactly like
/// <see cref="PandaLoadoutProbe"/> and runs a discovery chunk that enumerates candidate
/// exchange VMs. Removed/replaced by the real PandaExchangeProbe in Phase 1.
/// </summary>
internal sealed partial class PandaExchangeReconProbe
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private const string ChunkName = "Stellar.ExchangeRecon";
    private const string ReconGlobal = "_StellarExchangeRecon";

    // Cancel token the game passes to async VM RPCs (a nil token never resumes a yield).
    private const string NeverCancelToken = "ZUtil.ZCancelSource.NeverCancelToken";

    private volatile bool _bridgeResolved;
    private bool _resolutionFailureLogged;

    private MethodInfo? _mainStateGetter;   // static LuaState mainState { get; }
    private MethodInfo? _doString;          // void DoString(string chunk, string chunkName)
    private MethodInfo? _getItem;           // object get_Item(string global)

    private int _resolveTickCounter;
    private const int ResolveAttemptEveryTicks = 60;

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
        catch (Exception ex) { OnResolutionFailure($"bridge resolution threw {ex.GetType().Name}: {ex.Message}"); return false; }
    }

    private bool TryResolveBridge()
    {
        var luaStateType = _typeRegistry.FindType("ZLuaFramework.LuaState")
            ?? _typeRegistry.FindType("LuaInterface.LuaState")
            ?? FindTypeByShortName("LuaState");
        if (luaStateType is null) { OnResolutionFailure("ZLuaFramework.LuaState type not loaded yet"); return false; }

        _mainStateGetter = luaStateType.GetProperty("mainState", AnyStatic)?.GetGetMethod(nonPublic: true);
        if (_mainStateGetter is null) { OnResolutionFailure("LuaState.mainState not found"); return false; }

        _doString = FindDoString(luaStateType);
        if (_doString is null) { OnResolutionFailure("LuaState.DoString(string,string) not found"); return false; }

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
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string)) return m;
        }
        return null;
    }

    private object? GetMainLuaState()
    {
        if (_mainStateGetter is null) return null;
        try { return _mainStateGetter.Invoke(null, Array.Empty<object>()); }
        catch { return null; }
    }

    private bool InvokeChunk(string chunk)
    {
        var state = GetMainLuaState();
        if (state is null) { OnResolutionFailure("LuaState.mainState returned null at dispatch"); return false; }
        if (_doString is null) return false;
        try { _doString.Invoke(state, new object[] { chunk, ChunkName }); return true; }
        catch (Exception ex)
        {
            var inner = ex; while (inner.InnerException is not null) inner = inner.InnerException;
            _log.Warning($"[Stellar][ExchangeRecon] Lua dispatch threw: {inner.GetType().Name}: {inner.Message}");
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
            try { var ptr = ob.Pointer; if (ptr != IntPtr.Zero) return IL2CPP.Il2CppStringToManaged(ptr); }
            catch { }
        }
        return val.ToString();
    }

    private static Type? FindTypeByShortName(string shortName)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            string asmName;
            try { asmName = asm.GetName().Name ?? string.Empty; } catch { continue; }
            if (ShouldSkipAssemblyForScan(asmName)) continue;
            Type?[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types!; }
            catch { continue; }
            foreach (var t in types)
            {
                if (t is null) continue;
                string name; try { name = t.Name; } catch { continue; }
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
