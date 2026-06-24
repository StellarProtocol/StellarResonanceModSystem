using System;
using System.Reflection;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Switches the local party between 5- and 20-player through the game's OWN Lua
/// path — never by constructing a packet. Mirrors <see cref="PandaModuleEquipProbe"/>'s
/// Lua bridge: resolves the tolua# <c>ZLuaFramework.LuaState.mainState</c> +
/// <c>DoString(chunk, name)</c>, then runs the same chunk the in-game party UI
/// runs: <c>Z.VMMgr.GetVM("team").AsyncChangeTeamMemberType(type, targetId)</c>.
///
/// <para>That Lua function applies the game's own validation (the 20-player
/// function-unlock <c>gotofunc</c> gate, and a no-op when the size already
/// matches) and the C# RPC layer beneath builds + sends the protobuf — so this
/// stays on the sanctioned QoL side of the line (user-initiated command through
/// the game's dispatcher, never a hand-built message). The size change is
/// fire-and-forget; the meter follows the resulting <c>NoticeUpdateTeamInfo</c>
/// broadcast via <c>PartyService.PartyType</c>.</para>
///
/// <para><b>Threading:</b> the game's Lua VM is main-thread-only — touching it off
/// the Unity main thread corrupts IL2CPP/Lua state and hard-crashes. The only
/// caller is the meter's uGUI button click, which is already on the main thread.
/// Resolution is lazy + cached; any failure logs once and disables the path.</para>
/// </summary>
internal sealed class PandaTeamControlProbe : IPartyControlProbe
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const string ChunkName = "stellar.teamsize";

    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;

    private bool _resolved;
    private bool _failLogged;
    private MethodInfo? _mainStateGetter;   // static LuaState mainState { get; }
    private MethodInfo? _doString;          // void DoString(string chunk, string chunkName)

    public PandaTeamControlProbe(IGameTypeRegistry types, IPluginLog log)
    {
        _types = types ?? throw new ArgumentNullException(nameof(types));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public bool IsResolved => _resolved || TryResolve();

    /// <summary>Run the game's team-size switch for a 5- or 20-player target. No-op for Solo or when unresolved.</summary>
    public void CallSetMemberType(PartyType size)
    {
        // ETeamMemberType: Five = 0, Twenty = 1. Solo has no wire representation — nothing to switch to.
        int typeValue = size switch { PartyType.Regular5 => 0, PartyType.Raid20 => 1, _ => -1 };
        if (typeValue < 0) return;
        if (!TryResolve()) return;

        var state = _mainStateGetter!.Invoke(null, null);
        if (state is null) { WarnOnce("LuaState.mainState was null"); return; }

        try
        {
            _doString!.Invoke(state, new object[] { BuildChunk(typeValue), ChunkName });
        }
        catch (Exception ex)
        {
            WarnOnce($"DoString threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Move a member to a raid group (1-based) / slot (0-based) via the game's AsyncUpdateTeamGroup.</summary>
    public void CallMoveMember(long charId, int group, int slot)
    {
        if (group < 1 || group > 4 || slot < 0 || slot > 4) return;
        if (!TryResolve()) return;

        var state = _mainStateGetter!.Invoke(null, null);
        if (state is null) { WarnOnce("LuaState.mainState was null"); return; }

        try
        {
            _doString!.Invoke(state, new object[] { BuildMoveChunk(group, charId, slot), ChunkName });
        }
        catch (Exception ex)
        {
            WarnOnce($"DoString threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Transfer party leadership to charId via the game's own AsyncTransferLeader.</summary>
    public void CallTransferLeader(long charId)
    {
        if (!TryResolve()) return;
        var state = _mainStateGetter!.Invoke(null, null);
        if (state is null) { WarnOnce("LuaState.mainState was null"); return; }
        try { _doString!.Invoke(state, new object[] { BuildTransferLeaderChunk(charId), ChunkName }); }
        catch (Exception ex) { WarnOnce($"DoString threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    /// <summary>Kick charId from the party via the game's own AsyncTickOut.</summary>
    public void CallKickMember(long charId)
    {
        if (!TryResolve()) return;
        var state = _mainStateGetter!.Invoke(null, null);
        if (state is null) { WarnOnce("LuaState.mainState was null"); return; }
        try { _doString!.Invoke(state, new object[] { BuildKickMemberChunk(charId), ChunkName }); }
        catch (Exception ex) { WarnOnce($"DoString threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    internal static string BuildTransferLeaderChunk(long charId) =>
        "pcall(function()\n" +
        "  (Z.CoroUtil).create_coro_xpcall(function()\n" +
        "    local vm=(Z.VMMgr).GetVM('team')\n" +
        "    if vm then local cs=(Z.CancelSource).Rent() vm.AsyncTransferLeader(" + charId + ",cs:CreateToken()) end\n" +
        "  end,function() end)()\n" +
        "end)";

    internal static string BuildKickMemberChunk(long charId) =>
        "pcall(function()\n" +
        "  (Z.CoroUtil).create_coro_xpcall(function()\n" +
        "    local vm=(Z.VMMgr).GetVM('team')\n" +
        "    if vm then local cs=(Z.CancelSource).Rent() vm.AsyncTickOut(" + charId + ",cs:CreateToken()) end\n" +
        "  end,function() end)()\n" +
        "end)";

    /// <summary>Invite charId to the party via the game's own AsyncInviteToTeam.</summary>
    public void CallInviteToTeam(long charId)
    {
        if (!TryResolve()) return;
        var state = _mainStateGetter!.Invoke(null, null);
        if (state is null) { WarnOnce("LuaState.mainState was null"); return; }
        try { _doString!.Invoke(state, new object[] { BuildInviteToTeamChunk(charId), ChunkName }); }
        catch (Exception ex) { WarnOnce($"DoString threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    internal static string BuildInviteToTeamChunk(long charId) =>
        "pcall(function()\n" +
        "  (Z.CoroUtil).create_coro_xpcall(function()\n" +
        "    local vm=(Z.VMMgr).GetVM('team')\n" +
        "    if vm then local cs=(Z.CancelSource).Rent() vm.AsyncInviteToTeam(" + charId + ",cs:CreateToken()) end\n" +
        "  end,function() end)()\n" +
        "end)";

    /// <summary>Leave the current party via AsyncQuitTeam (cancelSource, not cancelToken — the VM calls CreateToken internally).</summary>
    public void CallLeaveParty()
    {
        if (!TryResolve()) return;
        var state = _mainStateGetter!.Invoke(null, null);
        if (state is null) { WarnOnce("LuaState.mainState was null"); return; }
        try { _doString!.Invoke(state, new object[] { BuildLeavePartyChunk(), ChunkName }); }
        catch (Exception ex) { WarnOnce($"DoString threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    internal static string BuildLeavePartyChunk() =>
        "pcall(function()\n" +
        "  (Z.CoroUtil).create_coro_xpcall(function()\n" +
        "    local vm=(Z.VMMgr).GetVM('team')\n" +
        "    if vm then local cs=(Z.CancelSource).Rent() vm.AsyncQuitTeam(cs) end\n" +
        "  end,function() end)()\n" +
        "end)";

    // The Lua chunk the in-game party-rearrange drag runs (team_mine_view onEndDrag).
    // group is 1-based; slot is 0-based — both already resolved by the caller. pcall-guarded
    // so a Lua-side error can't escape into the engine.
    internal static string BuildMoveChunk(int group, long charId, int slot) =>
        "pcall(function()\n" +
        "  local t = ((Z.VMMgr).GetVM)(\"team\")\n" +
        "  if not t then return end\n" +
        "  (t.AsyncUpdateTeamGroup)(" + group + ", " + charId + ", " + slot + ")\n" +
        "end)";

    // The Lua chunk the in-game party UI runs. Reads the team's CURRENT activity target from team_data so a
    // size switch never changes it; pcall-guarded so a Lua-side error can't escape into the engine.
    internal static string BuildChunk(int typeValue) =>
        "pcall(function()\n" +
        "  local t = ((Z.VMMgr).GetVM)(\"team\")\n" +
        "  if not t then return end\n" +
        "  local tid = 0\n" +
        "  local td = ((Z.DataMgr).Get)(\"team_data\")\n" +
        "  if td and td.TeamInfo and td.TeamInfo.baseInfo then tid = (td.TeamInfo.baseInfo).targetId or 0 end\n" +
        "  (t.AsyncChangeTeamMemberType)(" + typeValue + ", tid)\n" +
        "end)";

    private bool TryResolve()
    {
        if (_resolved) return true;
        var luaStateType = _types.FindType("ZLuaFramework.LuaState") ?? _types.FindType("LuaInterface.LuaState");
        if (luaStateType is null) return false;   // hot-update not loaded yet — retry on next call

        _mainStateGetter = luaStateType.GetProperty("mainState", AnyStatic)?.GetGetMethod(nonPublic: true);
        _doString = FindDoString(luaStateType);
        if (_mainStateGetter is null || _doString is null)
        {
            WarnOnce("LuaState.mainState / DoString(string,string) not found");
            return false;
        }

        _resolved = true;
        _log.Info("[TeamControl] resolved: Z.VMMgr.GetVM(\"team\").AsyncChangeTeamMemberType via LuaState.DoString");
        return true;
    }

    private static MethodInfo? FindDoString(Type luaStateType)
    {
        foreach (var m in luaStateType.GetMethods(AnyInstance))
        {
            if (m.Name != "DoString" || m.IsGenericMethodDefinition || m.ReturnType != typeof(void)) continue;
            var ps = m.GetParameters();
            if (ps.Length == 2 && ps[0].ParameterType == typeof(string) && ps[1].ParameterType == typeof(string))
                return m;
        }
        return null;
    }

    private void WarnOnce(string msg)
    {
        if (_failLogged) return;
        _failLogged = true;
        _log.Warning($"[TeamControl] {msg}");
    }
}
