using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Hooks;
namespace Stellar.Infrastructure.Game;

// StellarDiagnostics-gated logging: LuaTimeOfDayBackend.Diagnostics.cs.

/// <summary>
/// The game side of <c>ITimeOfDay</c>, ported from MahiruUtility's <c>DayNightControl</c> (proven on the owner's
/// client): the game's own <c>Panda.LuaAsyncBridge</c> statics — the calls interiors use (scene_10 pins 10:00 on
/// enter and hands back to the server on leave).
/// <para>⚠ It deliberately never touches the <c>ZServerTime</c> singleton: reflecting into it stalls the instance
/// whose heartbeat feeds the ping HUD (game-wide 0 ms ping) and pins night in town (TargetLens PR #2).</para>
/// The two setters are the feature; the two getters only power the re-assert check and the read-back. Reads and
/// writes run only in a stable world scene (<see cref="IClientState.IsWorldActive"/>). Writes never throw. Main
/// thread only. Recon: devkit docs/recon/photo-studio-render-recon.md § Render quality + time of day recon.
/// </summary>
internal sealed partial class LuaTimeOfDayBackend : ITimeOfDayBackend
{
    private const string Tag = "[PhotoTime] ";
    internal const string BridgeType = "Panda.LuaAsyncBridge";
    private const string BridgeWrapType = "Panda_LuaAsyncBridgeWrap";

    private readonly IGameTypeRegistry _types;
    private readonly IClientState _clientState;
    private readonly IPluginLog _log;
    private readonly HashSet<string> _warned = new(StringComparer.Ordinal);
    private bool _resolved;
    private MethodInfo? _setTime;        // SetCurWeatherTime(float time, bool isForced)
    private MethodInfo? _setFromServer;  // SetWeatherIsUpdateFromServer(bool)
    private MethodInfo? _getTime;        // GetCurWeatherTime24() → float
    private MethodInfo? _getFromServer;  // GetWeatherIsUpdateFromServer() → bool
    private bool _writing;               // true while WE call the bridge, so the hooks ignore our own calls

    public LuaTimeOfDayBackend(IGameTypeRegistry types, IClientState clientState, IPluginLog log)
    {
        _types = types;
        _clientState = clientState;
        _log = log;
    }

    /// <summary>Raised (main thread) after the GAME set the time of day or server-driven time — never for our own calls.</summary>
    public event Action? GameChanged;

    public bool IsAvailable => Resolve();
    public bool IsReady => Resolve() && _clientState.IsWorldActive;

    public float? ReadHour() => IsReady && _getTime is not null ? Invoke<float>(_getTime) : null;

    public bool? ReadServerDriven() => IsReady && _getFromServer is not null ? Invoke<bool>(_getFromServer) : null;

    public void Pin(float hour) => Write("pin", hour, () =>
    {
        _setFromServer!.Invoke(null, new object[] { false });
        _setTime!.Invoke(null, new object[] { hour, true });   // isForced = true → instant, no cross-fade
    });

    public void ReleaseToServer() => Write("server", true, () => _setFromServer!.Invoke(null, new object[] { true }));

    /// <summary>Installs the "the game set the time" postfixes on the bridge and its Lua wrap (the wrap catches a
    /// bridge call IL2CPP inlined into it). Call once, after the hot-update assemblies load.</summary>
    public void InstallHooks(HarmonyGameMethodHooker hooker)
    {
        foreach (var typeName in new[] { BridgeType, BridgeWrapType })
        {
            var t = SafeFind(typeName);
            if (t is null)
            {
                WarnOnce("hooktype:" + typeName, $"time-of-day re-assert hooks on {typeName} unavailable; scene changes still re-assert.");
                continue;
            }
            foreach (var method in new[] { "SetWeatherIsUpdateFromServer", "SetCurWeatherTime" })
            {
                var name = method;
                try { hooker.PostfixStaticOverloads(t, name, (_, _) => OnGameCall(typeName + "." + name)); }
                catch (Exception ex) { WarnOnce("hook:" + typeName + name, $"hook {typeName}.{name} failed: {ex.Message}"); }
            }
        }
    }

    private bool Resolve()
    {
        if (_resolved) return _setTime is not null && _setFromServer is not null;
        var t = SafeFind(BridgeType);
        if (t is null) return false;   // not loaded yet — the registry memoizes the miss until an assembly loads
        _resolved = true;
        // All four are unique by (name, arity), so a count-only match is safe here.
        _setTime = StellarInterop.FindMethod(t, "SetCurWeatherTime", 2);
        _setFromServer = StellarInterop.FindMethod(t, "SetWeatherIsUpdateFromServer", 1);
        _getTime = StellarInterop.FindMethod(t, "GetCurWeatherTime24", 0);
        _getFromServer = StellarInterop.FindMethod(t, "GetWeatherIsUpdateFromServer", 0);
        _log.Info(Tag + $"resolve setTime={_setTime is not null} setFromServer={_setFromServer is not null} " +
                  $"getTime={_getTime is not null} getFromServer={_getFromServer is not null}");
        return _setTime is not null && _setFromServer is not null;
    }

    private T? Invoke<T>(MethodInfo getter) where T : struct
    {
        try { return getter.Invoke(null, Array.Empty<object>()) is T v ? v : null; }
        catch { return null; }
    }

    private void Write(string what, object value, Action call)
    {
        if (!IsReady) return;
        _writing = true;
        try
        {
            call();
            OnWritten(what, value);
        }
        catch (Exception ex)
        {
            WarnOnce("write:" + what, $"Could not set time of day ({what}): {(ex.InnerException ?? ex).Message}");
        }
        finally
        {
            _writing = false;
        }
    }

    private void OnGameCall(string method)
    {
        if (_writing) return;
        OnGameCallObserved(method);
        GameChanged?.Invoke();
    }

    private Type? SafeFind(string name)
    {
        try { return _types.FindType(name); }
        catch { return null; }
    }

    private void WarnOnce(string key, string message)
    {
        if (_warned.Add(key)) _log.Warning(Tag + message);
    }

    partial void OnWritten(string what, object value);
    partial void OnGameCallObserved(string method);
}
