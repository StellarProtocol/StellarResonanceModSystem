using System;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// <see cref="ILua"/> implementation over the game's tolua# <c>LuaInterface.LuaState.mainState</c>.
/// Resolution of the state + the <c>void DoString(string,string)</c> entry point mirrors the proven path in
/// <see cref="NoticeTipService"/> (<c>EnsureLuaState</c>); the typed-global readback uses the tolua# stack
/// primitives (<c>LuaGetGlobal</c>/<c>LuaGetTop</c>/<c>LuaType</c>/<c>LuaToBoolean|Number|String</c>/
/// <c>LuaSetTop</c>) documented in <c>Knowledge Base\Lua-Injection-from-CSharp.md</c> §3b.
/// <para>All members are main-thread only (the Lua stack is not thread-safe). Shared across all plugins —
/// there is one main state — so this is a single instance on the shared services bag.</para>
/// </summary>
internal sealed class LuaService : ILua
{
    // LuaInterface.LuaTypes constants (from the IL2CPP dump): the values we branch on.
    private const int LUA_TNIL = 0;
    private const int LUA_TBOOLEAN = 1;
    private const int LUA_TNUMBER = 3;
    private const int LUA_TSTRING = 4;

    private readonly Action<string>? _log;

    private object? _luaState;
    private MethodInfo? _doString;
    private bool _coreLogged;

    // tolua# stack API (resolved lazily, all-or-nothing).
    private MethodInfo? _getGlobal, _getTop, _setTop, _type, _toBoolean, _toNumber, _toString;
    private bool _stackResolved;

    public LuaService(Action<string>? log = null) => _log = log;

    public bool Ready
    {
        get { EnsureCore(); return _luaState is not null && _doString is not null; }
    }

    public void DoString(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return;
        EnsureCore();
        if (_luaState is null || _doString is null) return;
        try { _doString.Invoke(_luaState, new object[] { chunk, "stellar.lua" }); }
        catch (Exception ex) { _log?.Invoke($"[Lua] DoString threw: {ex.Message}"); }
    }

    public bool TryReadGlobalBool(string key, out bool value)
    {
        value = false;
        if (!EnsureStack()) return false;
        var baseTop = 0;
        try
        {
            baseTop = GetTop();
            _getGlobal!.Invoke(_luaState, new object[] { key });
            var top = GetTop();
            if (top <= baseTop || TypeAt(top) != LUA_TBOOLEAN) return false;
            value = (bool)_toBoolean!.Invoke(_luaState, new object[] { top })!;
            return true;
        }
        catch (Exception ex) { _log?.Invoke($"[Lua] TryReadGlobalBool('{key}') threw: {ex.Message}"); return false; }
        finally { SetTop(baseTop); }
    }

    public bool TryReadGlobalNumber(string key, out double value)
    {
        value = 0;
        if (!EnsureStack()) return false;
        var baseTop = 0;
        try
        {
            baseTop = GetTop();
            _getGlobal!.Invoke(_luaState, new object[] { key });
            var top = GetTop();
            if (top <= baseTop || TypeAt(top) != LUA_TNUMBER) return false;
            value = (double)_toNumber!.Invoke(_luaState, new object[] { top })!;
            return true;
        }
        catch (Exception ex) { _log?.Invoke($"[Lua] TryReadGlobalNumber('{key}') threw: {ex.Message}"); return false; }
        finally { SetTop(baseTop); }
    }

    public string? ReadGlobalString(string key)
    {
        if (!EnsureStack()) return null;
        var baseTop = 0;
        try
        {
            baseTop = GetTop();
            _getGlobal!.Invoke(_luaState, new object[] { key });
            var top = GetTop();
            if (top <= baseTop) return null;
            var lt = TypeAt(top);
            if (lt is not (LUA_TSTRING or LUA_TNUMBER)) return null;   // LuaToString on other types is null/mutates
            return _toString!.Invoke(_luaState, new object[] { top }) as string;
        }
        catch (Exception ex) { _log?.Invoke($"[Lua] ReadGlobalString('{key}') threw: {ex.Message}"); return null; }
        finally { SetTop(baseTop); }
    }

    // --- resolution (same path NoticeTipService.EnsureLuaState uses) ---

    private void EnsureCore()
    {
        if (_doString is not null) return;

        var lsType = StellarInterop.FindType("LuaInterface.LuaState")
                     ?? StellarInterop.FindType("ZLuaFramework.LuaState");
        if (lsType is not null)
        {
            _luaState =
                StellarInterop.FindPropertyUp(lsType, "mainState")?.GetGetMethod(nonPublic: true)?.Invoke(null, null)
                ?? StellarInterop.FindFieldUp(lsType, "mainState")?.GetValue(null);
        }

        if (_luaState is null)
        {
            var clientInst = StellarInterop.GetSingleton("LuaClient");
            if (clientInst is not null)
            {
                var t = clientInst.GetType();
                _luaState =
                    StellarInterop.FindPropertyUp(t, "luaState")?.GetGetMethod(nonPublic: true)?.Invoke(clientInst, null)
                    ?? StellarInterop.FindFieldUp(t, "luaState")?.GetValue(clientInst);
            }
        }

        if (_luaState is not null)
        {
            // Match by name + arity: the il2cpp proxy may type the first param as Il2CppSystem.String,
            // so an exact-signature GetMethod can miss. Pick the void (string, ...) overload.
            foreach (var m in _luaState.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (m.Name != "DoString" || m.IsGenericMethod) continue;
                var ps = m.GetParameters();
                if (ps.Length != 2 || ps[0].ParameterType != typeof(string)) continue;
                if (m.ReturnType == typeof(void)) { _doString = m; break; }
            }
        }

        if (!_coreLogged && _doString is not null)
        {
            _coreLogged = true;
            _log?.Invoke("[Lua] resolved LuaState.mainState + DoString");
        }
    }

    private bool EnsureStack()
    {
        if (_stackResolved) return true;
        EnsureCore();
        if (_luaState is null) return false;
        var t = _luaState.GetType();
        _getGlobal = StellarInterop.FindMethod(t, "LuaGetGlobal", 1);
        _getTop = StellarInterop.FindMethod(t, "LuaGetTop", 0);
        _setTop = StellarInterop.FindMethod(t, "LuaSetTop", 1);
        _type = StellarInterop.FindMethod(t, "LuaType", 1);
        _toBoolean = StellarInterop.FindMethod(t, "LuaToBoolean", 1);
        _toNumber = StellarInterop.FindMethod(t, "LuaToNumber", 1);
        _toString = StellarInterop.FindMethod(t, "LuaToString", 1);
        _stackResolved = _getGlobal is not null && _getTop is not null && _setTop is not null
                         && _type is not null && _toBoolean is not null && _toNumber is not null && _toString is not null;
        return _stackResolved;
    }

    private int GetTop() => Convert.ToInt32(_getTop!.Invoke(_luaState, null));
    private void SetTop(int top) => _setTop!.Invoke(_luaState, new object[] { top });
    private int TypeAt(int index) => Convert.ToInt32(_type!.Invoke(_luaState, new object[] { index }));   // LuaTypes enum → int
}
