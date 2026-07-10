using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Originates the game's own self social-data RPC (<c>socialVM.AsyncGetSocialData(0, charId, ...)</c>)
/// through the Lua VM, reusing the exact <c>LuaState.DoString</c> reflection bridge proven by
/// <see cref="PandaPortraitModelProbe"/>. Fire-and-forget: this class only originates the request —
/// the reply is captured by the existing <c>PandaSocialDataProbe</c> wire-tap into
/// <c>SocialDataCache</c>, which is what callers should poll afterward.
///
/// <para><b>Threading:</b> the Lua VM is main-thread-only. Callers on other threads must marshal
/// onto the main thread before calling <see cref="RequestSelfSocialData"/>.</para>
/// </summary>
internal sealed class PandaSocialRefresh : ISocialRefreshRequester
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const string ChunkName = "stellar.socialrefresh";

    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;

    private bool _resolved;
    private bool _failLogged;
    private MethodInfo? _mainStateGetter;
    private MethodInfo? _doString;

    public PandaSocialRefresh(IGameTypeRegistry types, IPluginLog log)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Originate a Social.GetSocialData request for the given charId (self). No-op if the
    /// Lua bridge is unresolved or <paramref name="charId"/> is non-positive.</summary>
    public void RequestSelfSocialData(long charId)
    {
        if (charId <= 0) return;
        Run(BuildRefreshChunk(charId), $"[SocialRefresh] RequestSelfSocialData({charId})");
    }

    // Originates AsyncGetSocialData only (no model gen — the reply is captured by the existing
    // PandaSocialDataProbe wire-tap). Runs inside CoroUtil.create_coro_xpcall because
    // AsyncGetSocialData awaits an RPC.
    internal static string BuildRefreshChunk(long charId) =>
        "pcall(function()\n" +
        "  local coroFn = ((Z.CoroUtil).create_coro_xpcall)(function()\n" +
        "    local socialVM = ((Z.VMMgr).GetVM)('social')\n" +
        "    if not socialVM then return end\n" +
        "    (socialVM.AsyncGetSocialData)(0, " + charId + ", (ZUtil.ZCancelSource).NeverCancelToken)\n" +
        "  end)\n" +
        "  coroFn()\n" +
        "end)";

    private void Run(string chunk, string what)
    {
        if (!TryResolve()) return;
        var state = _mainStateGetter!.Invoke(null, null);
        if (state is null) { WarnOnce("LuaState.mainState was null"); return; }
        try
        {
            _doString!.Invoke(state, new object[] { chunk, ChunkName });
            _log.Info(what);
        }
        catch (Exception ex)
        {
            WarnOnce($"DoString threw ({what}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool TryResolve()
    {
        if (_resolved) return true;
        var luaStateType = _types.FindType("ZLuaFramework.LuaState") ?? _types.FindType("LuaInterface.LuaState");
        if (luaStateType is null) return false;   // hot-update not loaded yet — retry on next call

        _mainStateGetter = luaStateType.GetProperty("mainState", AnyStatic)?.GetGetMethod(nonPublic: true);
        _doString = FindMethod(luaStateType, "DoString", typeof(string), typeof(string));
        if (_mainStateGetter is null || _doString is null)
        {
            WarnOnce("LuaState.mainState / DoString(string,string) not found");
            return false;
        }

        _resolved = true;
        _log.Info("[SocialRefresh] resolved: AsyncGetSocialData(self) via LuaState.DoString");
        return true;
    }

    private static MethodInfo? FindMethod(Type type, string name, params Type[] paramTypes)
    {
        foreach (var m in type.GetMethods(AnyInstance))
        {
            if (m.Name != name || m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length != paramTypes.Length) continue;
            var match = true;
            for (var i = 0; i < ps.Length; i++)
                if (ps[i].ParameterType != paramTypes[i]) { match = false; break; }
            if (match) return m;
        }
        return null;
    }

    private void WarnOnce(string msg)
    {
        if (_failLogged) return;
        _failLogged = true;
        _log.Warning($"[SocialRefresh] {msg}");
    }
}
