using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Detects <c>WorldNtf.SyncDungeonData</c> packets and feeds the decoded run id
/// + settlement into the Application <see cref="IDungeonStateSink"/>. The
/// WorldNtf method id for SyncDungeonData (23) is confirmed
/// (<c>lua/zservice/world_ntf_gen.lua</c>), so this probe registers directly by
/// method id on the shared <see cref="WorldNtfStubDispatcher"/> — same as the
/// combat and inventory probes — and additionally verifies the payload shape via
/// <see cref="DungeonSyncReader"/>, accepting only when it carries a non-zero
/// <c>scene_uuid</c> (field 1).
///
/// <para>
/// The probe ALSO registers method 23 on the <see cref="WorldNtfLuaStubDispatcher"/>
/// (ZLuaStub path): the game's own <c>world_ntf_gen.lua</c> is what decodes
/// SyncDungeonData into the live dungeon container (<c>ContainerMgr.DungeonSyncData
/// ResetData</c>), and BPSR routes a WorldNtf method to EITHER the C# stub OR the
/// Lua stub. A second live run falsified the C#-only tap: during a real Master-6
/// run NO method-23 delivery with non-zero flow fields ever reached the C# stub —
/// the only C#-side traffic was a login-time bulk sync of PAST dungeon states.
/// The Lua registration tests the hypothesis that the LIVE run's dungeon sync
/// flows through ZLuaStub instead.
/// </para>
///
/// <para>
/// Runs on the network receive thread; never throws across the IL2CPP boundary
/// (the dispatchers wrap the handler call in a try/catch, and the reader is
/// fully defensive). First-seen + broad-delivery diagnostics live in
/// <c>PandaDungeonProbe.Diagnostics.cs</c>.
/// </para>
/// </summary>
internal sealed partial class PandaDungeonProbe
{
    private readonly IDungeonStateSink _sink;
    private readonly IDungeonState _state;
    private readonly IPluginLog _log;

    public PandaDungeonProbe(IDungeonStateSink sink, IDungeonState state, IPluginLog log)
    {
        _sink  = sink  ?? throw new ArgumentNullException(nameof(sink));
        _state = state ?? throw new ArgumentNullException(nameof(state));
        _log   = log   ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Register for the SyncDungeonData method (id 23, confirmed from the game's
    /// lua WorldNtf dispatcher) on the C# stub path. Must be called before
    /// <see cref="WorldNtfStubDispatcher.Install"/>.
    /// </summary>
    public void RegisterWith(WorldNtfStubDispatcher dispatcher)
        => dispatcher.Register(WorldNtfMethodIds.SyncDungeonData, OnWorldNtf);

    /// <summary>
    /// Register for the SyncDungeonData method (id 23) on the LUA stub path
    /// (ZLuaStub) — the path the game's own <c>world_ntf_gen.lua</c> container
    /// decode uses. Must be called before
    /// <see cref="WorldNtfLuaStubDispatcher.Install"/>.
    /// </summary>
    public void RegisterWithLua(WorldNtfLuaStubDispatcher dispatcher)
        => dispatcher.Register(WorldNtfMethodIds.SyncDungeonData, OnWorldNtfLua);

    /// <summary>
    /// Clear the active run id + settlement. Wired to logout so a stale run id
    /// doesn't linger between sessions. NOT wired to leave-scene — the run id is
    /// deliberately held through the return-to-town transition so the upload
    /// plugin can archive against the dungeon id after the player has left.
    /// </summary>
    public void OnLeaveOrLogout() => _sink.Reset();

    private void OnWorldNtf(uint methodId, byte[] payload) => HandleDelivery("cs", methodId, payload);

    private void OnWorldNtfLua(uint methodId, byte[] payload) => HandleDelivery("lua", methodId, payload);

    // SyncDungeonData (WorldNtf method 23) handler, shared by the C# and Lua stub
    // registrations. Structural match → update settlement.
    //
    // This probe NO LONGER touches the run id. SyncDungeonData (method 23) was
    // assumed to fire only inside a dungeon, but in-game it ALSO fires in town —
    // so a town method-23 would promote the town scene as the run id. The run id
    // is now latched directly (and magnitude-gated to dungeon instances) by
    // PandaCombatStubProbe.OnEnterScene; this probe only reads the settlement.
    //
    // We deliberately do NOT read scene_uuid from this payload: DungeonSyncData's
    // field 1 arrives through the game's dirty-mask container encoding and the
    // bare-varint read returns a shifting value within one run (1771, 579, 1) —
    // unreliable as a stable run key. The settlement sub-message IS a full
    // snapshot, so the clear time / master-mode score read here are correct.
    private void HandleDelivery(string source, uint methodId, byte[] payload)
    {
        if (!DungeonSyncReader.TryRead(payload, out var result))
            return;

        if (result.HasSettlement)
            _sink.SetSettlement(result.PassTimeSeconds, result.MasterModeScore);

        if (result.HasDungeonSceneInfo)
            _sink.SetDifficulty(result.DungeonDifficulty);

        // flow_info.play_time is the AUTHORITATIVE run-timer start (a live run
        // falsified timer_info.start_time: the first delivery was a hub scene
        // with all timer fields zero). Never latch zeros — a zero play_time is
        // the pre-start state and must not overwrite a previously latched value
        // (the service additionally guards against zero writes).
        if (result.HasFlowInfo && result.FlowInfo.PlayTime != 0)
            _sink.SetRunTimerStart(result.FlowInfo.PlayTimeMs);

        DiagDungeonDelivery(source, methodId, result);
        DiagDungeonSync(methodId, result);
        DiagDungeonDifficulty(methodId, result);
        DiagDungeonFlow(methodId, result);
        DiagDungeonTimer(methodId, result);
    }
}
