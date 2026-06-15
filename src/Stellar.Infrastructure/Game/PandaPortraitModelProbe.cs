using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Creates a posed, real-outfit character model for the Entity Inspector portrait through the game's OWN
/// model pipeline — the exact calls the game's inspect-bubble runs (<c>investigation_clue_window_view</c>):
/// <c>social.AsyncGetSocialData(0, charId)</c> → <c>Z.ModelManager:GenModelByLuaSocialData(socialData)</c> →
/// named idle clip via <c>EModelAnimOverrideByName</c>. The created <c>ZModel</c> is stashed in a Lua global;
/// <see cref="TryTakeModel"/> pulls it back into C# (raw <c>LuaGetGlobal</c> + <c>ToVariant</c>) so
/// <c>PortraitModelHost</c> can hand it to the game's <c>ZModel2RTMono</c> UI-model renderer.
///
/// <para>Mirrors <see cref="PandaTeamControlProbe"/>'s Lua bridge: resolves
/// <c>LuaInterface.LuaState.mainState</c> + <c>DoString(chunk, name)</c> lazily via reflection.</para>
/// <para><b>Threading:</b> the Lua VM is main-thread-only; the only callers are the inspector-open path and the
/// per-frame texture poll, both already on the main thread. Any resolution failure logs once and disables.</para>
/// </summary>
internal sealed class PandaPortraitModelProbe
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const string ChunkName = "stellar.portrait";
    private const string ModelGlobal = "__stellar_portrait_model";

    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;

    private bool _resolved;
    private bool _failLogged;
    private MethodInfo? _mainStateGetter;
    private MethodInfo? _doString;
    private MethodInfo? _luaGetGlobal;
    private MethodInfo? _toVariant;
    private MethodInfo? _luaPop;

    public PandaPortraitModelProbe(IGameTypeRegistry types, IPluginLog log)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Create the portrait model for a player (async — the model lands in the Lua global a few frames
    /// later, once the social-data RPC answers). No-op if the Lua bridge is unresolved.</summary>
    public void BuildModel(int charId) => Run(BuildModelChunk(charId), $"[Portrait] BuildModel({charId})");

    /// <summary>Recycle the portrait model through the game's pool and clear the global. No-op if unresolved.</summary>
    public void ClearModel() => Run(BuildClearChunk(), null);

    /// <summary>Fetch the created <c>ZModel</c> (an Il2Cpp proxy object) from the Lua global, or null while the
    /// async creation is still in flight. Cheap — three raw Lua stack ops; safe to poll once per frame.</summary>
    public object? TryTakeModel()
    {
        if (!_resolved || _luaGetGlobal is null || _toVariant is null || _luaPop is null) return null;
        var state = _mainStateGetter!.Invoke(null, null);
        if (state is null) return null;
        try
        {
            _luaGetGlobal.Invoke(state, new object[] { ModelGlobal });
            var model = _toVariant.Invoke(state, new object[] { -1 });
            _luaPop.Invoke(state, new object[] { 1 });
            return model;
        }
        catch (Exception ex)
        {
            WarnOnce($"TryTakeModel threw: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private void Run(string chunk, string? what)
    {
        if (!TryResolve()) return;
        var state = _mainStateGetter!.Invoke(null, null);
        if (state is null) { WarnOnce("LuaState.mainState was null"); return; }
        try
        {
            _doString!.Invoke(state, new object[] { chunk, ChunkName });
            if (what != null) _log.Info(what);
        }
        catch (Exception ex)
        {
            WarnOnce($"DoString threw ({what ?? "clear"}): {ex.GetType().Name}: {ex.Message}");
        }
    }

    // The game's own UI-model recipe (investigation_clue_window_view.initPlayerModelBubble): social data by
    // charId → GenModelByLuaSocialData → gender-named idle clip. Weapons stay mounted (full outfit, like
    // Personal Space). Runs inside CoroUtil.create_coro_xpcall because AsyncGetSocialData awaits an RPC.
    internal static string BuildModelChunk(int charId) =>
        "local ok, err = pcall(function()\n" +
        "  local coroFn = ((Z.CoroUtil).create_coro_xpcall)(function()\n" +
        "    local socialVM = ((Z.VMMgr).GetVM)('social')\n" +
        "    if not socialVM then logError('[Portrait.lua] no socialVM') return end\n" +
        "    local socialData = (socialVM.AsyncGetSocialData)(0, " + charId + ", (ZUtil.ZCancelSource).NeverCancelToken)\n" +
        "    if not socialData then logError('[Portrait.lua] socialData nil') return end\n" +
        "    local m = (Z.ModelManager):GenModelByLuaSocialData(socialData)\n" +
        "    if not m then logError('[Portrait.lua] gen nil') return end\n" +
        "    local clip = 'as_f_base_idle'\n" +
        "    pcall(function()\n" +
        "      if ((socialData.basicData).gender) ~= (Z.PbEnum)('EGender', 'GenderMale') then clip = 'as_m_base_idle' end\n" +
        "    end)\n" +
        "    pcall(function() m:SetLuaAttr((Z.ModelAttr).EModelAnimOverrideByName, ((Z.AnimBaseData).Rent)(clip, ((Panda.ZAnim).EAnimBase).EIdle)) end)\n" +
        "    rawset(_G, '" + ModelGlobal + "', m)\n" +
        "  end)\n" +
        "  coroFn()\n" +
        "end)\n" +
        "if not ok and logError then logError('[Portrait.lua] err: ' .. tostring(err)) end";

    internal static string BuildClearChunk() =>
        "pcall(function()\n" +
        "  local m = rawget(_G, '" + ModelGlobal + "')\n" +
        "  if m and Z.ModelManager then ((Z.ModelManager).RecycleModelByLua)(Z.ModelManager, m) end\n" +
        "  rawset(_G, '" + ModelGlobal + "', nil)\n" +
        "end)";

    private bool TryResolve()
    {
        if (_resolved) return true;
        var luaStateType = _types.FindType("ZLuaFramework.LuaState") ?? _types.FindType("LuaInterface.LuaState");
        if (luaStateType is null) return false;   // hot-update not loaded yet — retry on next call

        _mainStateGetter = luaStateType.GetProperty("mainState", AnyStatic)?.GetGetMethod(nonPublic: true);
        _doString = FindMethod(luaStateType, "DoString", typeof(string), typeof(string));
        _luaGetGlobal = FindMethod(luaStateType, "LuaGetGlobal", typeof(string));
        _toVariant = FindMethod(luaStateType, "ToVariant", typeof(int));
        _luaPop = FindMethod(luaStateType, "LuaPop", typeof(int));
        if (_mainStateGetter is null || _doString is null)
        {
            WarnOnce("LuaState.mainState / DoString(string,string) not found");
            return false;
        }
        if (_luaGetGlobal is null || _toVariant is null || _luaPop is null)
            WarnOnce("LuaGetGlobal/ToVariant/LuaPop not found — model handoff to C# disabled");

        _resolved = true;
        _log.Info("[Portrait] resolved: GenModelByLuaSocialData via LuaState.DoString + ToVariant handoff");
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
        _log.Warning($"[Portrait] {msg}");
    }
}
