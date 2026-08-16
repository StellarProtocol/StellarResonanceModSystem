using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Actively refreshes the party id after a mid-dungeon reconnect by invoking the game's own
/// <c>WorldProxy.GetTeamInfo({})</c> through the tolua# Lua bridge — the same mechanism
/// <see cref="PandaLoadoutProbe"/> uses to drive <c>WorldProxy.SwitchProject</c>.
///
/// <para><b>Why:</b> on a reconnect the game does NOT push the party roster back during the run — the
/// full <c>GetTeamInfo_Ret</c> arrives only lazily (opening the party panel / some late trigger; measured
/// AFTER a whole dungeon on run <c>sea/kqCsvtAMx3</c>), so <see cref="IPartySnapshot.PartyId"/> reads 0
/// for the entire reconnected run and uploads split by party. Firing <c>GetTeamInfo</c> ourselves makes
/// the server send the reply NOW; the framework's existing <see cref="PandaPartyStubProbe"/> decodes the
/// returning <c>GetTeamInfo_Ret</c> and fills in <c>PartyId</c> — no new decode path.</para>
///
/// <para><b>Scope:</b> a READ-ONLY RPC (empty <c>GetTeamInfoRequest</c>; the game builds + validates it)
/// invoked through the game's own dispatcher — the Dalamud line, identical in kind to
/// <c>WorldProxy.InstallMod</c>. It is the first RPC Stellar sends WITHOUT a user command, so it is
/// hard-bounded: only when in a dungeon with the party id still unknown, throttled, and capped per run.</para>
///
/// <para><b>Threading:</b> the Lua VM is Unity-main-thread-only, so <see cref="Tick"/> is called from the
/// Host world-gated Update tick (<c>DrainEquipAndLoadout</c>), never off-thread.</para>
/// </summary>
internal sealed partial class PandaTeamInfoRefreshProbe
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private const string ChunkName = "stellar_getteaminfo";

    // The party page's own path is TeamVM.AsyncGetTeamInfo -> WorldProxy.GetTeamInfo({}, token). We call
    // the proxy directly with an empty request — we don't consume the Lua return value; the reply rides the
    // wire to the framework's party decoder (PandaPartyStubProbe → IPartySnapshot.PartyId). Structure mirrors
    // PandaSocialRefresh exactly (the proven refresh idiom): outer pcall belt, coroutine-wrapped because
    // GetTeamInfo uses coro_util.async_to_sync (it yields until the RPC replies), NeverCancelToken so the
    // call is never treated as cancelled.
    private const string Chunk =
        "pcall(function()\n" +
        "  local coroFn = ((Z.CoroUtil).create_coro_xpcall)(function()\n" +
        "    (require('zproxy.world_proxy').GetTeamInfo)({}, (ZUtil.ZCancelSource).NeverCancelToken)\n" +
        "  end)\n" +
        "  coroFn()\n" +
        "end)";

    private const long ThrottleMs = 2500;   // at most one request per this interval
    private const int MaxPerRun = 3;        // hard cap of requests per dungeon-run (reset on run change)

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;
    private readonly Func<long> _readCurrentRunId;
    private readonly Func<long> _readPartyId;

    private long _lastRequestMs;
    private long _lastRunId;
    private int _sentThisRun;

    // Lua bridge (resolved lazily post-HybridCLR) — mirror of PandaLoadoutProbe.Resolution.
    private MethodInfo? _mainStateGetter;
    private MethodInfo? _doString;
    private bool _bridgeResolved;
    private int _resolveTickCounter;
    private const int ResolveAttemptEveryTicks = 30;

    public PandaTeamInfoRefreshProbe(
        IPluginLog log, IGameTypeRegistry typeRegistry, Func<long> readCurrentRunId, Func<long> readPartyId)
    {
        _log = log;
        _typeRegistry = typeRegistry;
        _readCurrentRunId = readCurrentRunId;
        _readPartyId = readPartyId;
    }

    /// <summary>Called each world-gated Update tick (main thread). Fires <c>GetTeamInfo</c> when we are in
    /// a dungeon with no party id yet — i.e. a reconnect that lost the roster. Self-limiting: stops the
    /// instant <c>PartyId</c> becomes non-zero, and caps at <see cref="MaxPerRun"/> per run so a genuine
    /// solo dungeon (party stays 0) sends only a few harmless requests, never a stream.</summary>
    public void Tick()
    {
        long runId = _readCurrentRunId();
        if (runId != _lastRunId) { _lastRunId = runId; _sentThisRun = 0; }   // new run → reset the per-run cap
        if (runId == 0) return;                       // not in a dungeon (open-world uses a different key)
        if (_readPartyId() != 0) return;              // party already known — nothing to fetch
        if (_sentThisRun >= MaxPerRun) return;        // bounded

        long now = Environment.TickCount64;
        if (now - _lastRequestMs < ThrottleMs) return;

        TryResolveBridgeIfDue();
        if (!_bridgeResolved) return;

        if (InvokeChunk(Chunk))
        {
            _lastRequestMs = now;
            _sentThisRun++;
            LogRequested(runId, _sentThisRun);
        }
    }

    private void TryResolveBridgeIfDue()
    {
        if (_bridgeResolved) return;
        if (_resolveTickCounter++ % ResolveAttemptEveryTicks != 0) return;
        try { TryResolveBridge(); }
        catch (Exception ex) { _log.Warning($"[Stellar][TeamRefresh] bridge resolution threw: {ex.GetType().Name}: {ex.Message}"); }
    }

    private bool TryResolveBridge()
    {
        var luaStateType = _typeRegistry.FindType("ZLuaFramework.LuaState")
            ?? _typeRegistry.FindType("LuaInterface.LuaState");
        if (luaStateType is null) return false;

        _mainStateGetter = luaStateType.GetProperty("mainState", AnyStatic)?.GetGetMethod(nonPublic: true);
        if (_mainStateGetter is null) return false;

        _doString = FindDoString(luaStateType);
        if (_doString is null) return false;

        _bridgeResolved = true;
        LogBridgeResolved();
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

    private bool InvokeChunk(string chunk)
    {
        object? state;
        try { state = _mainStateGetter!.Invoke(null, Array.Empty<object>()); }
        catch { return false; }
        if (state is null || _doString is null) return false;

        try
        {
            _doString.Invoke(state, new object[] { chunk, ChunkName });
            return true;
        }
        catch (Exception ex)
        {
            var inner = ex;
            while (inner.InnerException is not null) inner = inner.InnerException;
            _log.Warning($"[Stellar][TeamRefresh] Lua dispatch threw: {inner.GetType().Name}: {inner.Message}");
            return false;
        }
    }
}
