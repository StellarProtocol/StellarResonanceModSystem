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
/// The probe ALSO registers on the <see cref="WorldNtfLuaStubDispatcher"/>
/// (ZLuaStub path) for method 23 AND method 55
/// (<c>NotifyStartPlayingDungeon</c> — the play-start EDGE; its consumer is the
/// Lua stub per <c>world_ntf_gen.lua</c> → <c>world_ntf_impl.lua</c>). A live
/// instrumented run confirmed the dungeon's entry sync is delivered on BOTH
/// paths but carries a usable clock only in <c>flow_info.active_time</c> —
/// which a third live falsification proved is the SCENE-ENTRY time, not play
/// start (staging-area imagine casts predated our 0:00), and method 55 was
/// never observed on that run.
/// </para>
///
/// <para>
/// The game's own dungeon-timer HUD nevertheless shows the true clock: it reads
/// <c>ContainerMgr.DungeonSyncData.timerInfo.startTime</c>
/// (<c>dungeon_timer_vm.lua getEndTimeStamp</c>), which is fed by the dungeon
/// container's dirty-DELTA path — WorldNtf <b>method 24</b>
/// (<c>SyncDungeonDirtyData</c>, C#-routed: <c>Zservice.WorldNtfStub</c>
/// publisher → <c>Panda.ZGame.DungeonSyncService.OnSync</c> →
/// <c>lua/sync/dungeon_sync.lua</c> → <c>dungeon_sync_data.lua MergeData</c>).
/// The AUTHORITATIVE tap for that delta is
/// <see cref="PandaDungeonSyncSubscription"/> — our own MessagePipe
/// subscription to the same event (no patching), which captures the BARE merge
/// blob downstream of whatever wire method delivers it and enqueues via
/// <see cref="OnDungeonSyncDeltaDeferred"/>. The method-24 registration below
/// is the INFERRED wire-origin tap, kept as corroborating diagnostics. Both
/// shapes drain through the same handler and parse with
/// <see cref="DungeonDirtyDataReader"/>; a non-zero
/// <c>timer_info.start_time</c> latches at rank 2 (TimerInfo), UPGRADING an
/// earlier rank-4 active_time latch mid-run (duplicates are harmless — the
/// rank guard ignores equal-rank rewrites).
/// </para>
///
/// <para>
/// CRASH SAFETY — the Lua-path callback does NO work: a live run with an
/// inline Lua-path handler crashed the client during the scene load right
/// after leaving the dungeon (the identical build minus the Lua tap passed the
/// same in/out test), so Lua-path processing during scene teardown is the
/// prime suspect. The Lua callback now only copies the (already-owned) payload
/// + method id + an arrival timestamp into a small bounded queue; ALL parsing,
/// sink writes and logging happen on the framework's main-thread tick, which
/// is gated off during scene transitions — queued items simply wait for the
/// gate to clear. See <c>PandaDungeonProbe.Deferred.cs</c>.
/// </para>
///
/// <para>
/// The C# stub path keeps its inline handling (network receive thread) — that
/// path has never crashed. Never throws across the IL2CPP boundary (the
/// dispatchers wrap the handler call in a try/catch, and the readers are fully
/// defensive). First-seen + broad-delivery diagnostics live in
/// <c>PandaDungeonProbe.Diagnostics.cs</c>.
/// </para>
/// </summary>
internal sealed partial class PandaDungeonProbe
{
    private readonly IDungeonStateSink _sink;
    private readonly IDungeonState _state;
    private readonly ICombatSnapshot _combat;
    private readonly IPluginLog _log;

    public PandaDungeonProbe(IDungeonStateSink sink, IDungeonState state, ICombatSnapshot combat, IPluginLog log)
    {
        _sink   = sink   ?? throw new ArgumentNullException(nameof(sink));
        _state  = state  ?? throw new ArgumentNullException(nameof(state));
        _combat = combat ?? throw new ArgumentNullException(nameof(combat));
        _log    = log    ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Register on the C# stub path: SyncDungeonData (id 23, confirmed from the
    /// game's lua WorldNtf dispatcher) inline, plus SyncDungeonDirtyData (id 24
    /// — the dungeon container's dirty-DELTA, the path the true
    /// <c>timer_info.start_time</c> arrives on; C#-routed, its consumer is
    /// <c>Panda.ZGame.DungeonSyncService</c> → <c>lua/sync/dungeon_sync.lua</c>)
    /// via the crash-safe deferred queue. Must be called before
    /// <see cref="WorldNtfStubDispatcher.Install"/>.
    /// </summary>
    public void RegisterWith(WorldNtfStubDispatcher dispatcher)
    {
        dispatcher.Register(WorldNtfMethodIds.SyncDungeonData, OnWorldNtf);
        dispatcher.Register(WorldNtfMethodIds.SyncDungeonDirtyData, OnWorldNtfDeferred);
    }

    /// <summary>
    /// Register on the LUA stub path (ZLuaStub) for SyncDungeonData (id 23; the
    /// path the game's own <c>world_ntf_gen.lua</c> container decode uses),
    /// NotifyStartPlayingDungeon (id 55; Lua-only — its arrival is the precise
    /// play-start edge, rank-1 run-timer source) and SyncDungeonDirtyData (id
    /// 24; expected on the C# stub, registered here too in case delivery is
    /// dual-path like method 23 — the rank guard makes duplicates harmless).
    /// All route through the crash-safe deferred queue
    /// (<c>PandaDungeonProbe.Deferred.cs</c>). Must be called before
    /// <see cref="WorldNtfLuaStubDispatcher.Install"/>.
    /// </summary>
    public void RegisterWithLua(WorldNtfLuaStubDispatcher dispatcher)
    {
        dispatcher.Register(WorldNtfMethodIds.SyncDungeonData, OnWorldNtfDeferred);
        dispatcher.Register(WorldNtfMethodIds.NotifyStartPlayingDungeon, OnWorldNtfDeferred);
        dispatcher.Register(WorldNtfMethodIds.SyncDungeonDirtyData, OnWorldNtfDeferred);
    }

    /// <summary>
    /// Clear the active run id + settlement. Wired to logout so a stale run id
    /// doesn't linger between sessions. NOT wired to leave-scene — the run id is
    /// deliberately held through the return-to-town transition so the upload
    /// plugin can archive against the dungeon id after the player has left.
    /// </summary>
    public void OnLeaveOrLogout() => _sink.Reset();

    private void OnWorldNtf(uint methodId, byte[] payload) => HandleDelivery("cs", methodId, payload);

    // SyncDungeonData (WorldNtf method 23) handler — inline on the C# stub path
    // (network receive thread, crash-safe by precedent); on the Lua path it runs
    // DEFERRED on the framework tick (PandaDungeonProbe.Deferred.cs).
    // Structural match → update settlement.
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

        MaybeLatchRunTimer(result);

        DiagDungeonDelivery(source, methodId, result);
        DiagDungeonSync(methodId, result);
        DiagDungeonDifficulty(methodId, result);
        DiagDungeonFlow(methodId, result);
        DiagDungeonTimer(methodId, result);
    }

    // Run-timer start latch from a SyncDungeonData payload. The payload's best
    // candidate (timer_info.start_time > flow.play_time > flow.active_time,
    // never zero) is pushed with its cross-delivery RANK: the service latches
    // when the slot is empty and lets a strictly better-ranked source UPGRADE a
    // worse one (the method-55 arrival edge, rank 1, is fed separately from the
    // deferred path). Live evidence made this necessary: the entry sync's only
    // non-zero clock is flow.active_time (approximate, rank 4) and it arrives
    // BEFORE method 55 — a plain first-wins guard would have frozen the
    // approximation in.
    private void MaybeLatchRunTimer(in DungeonSyncResult result)
    {
        if (!result.TryGetRunTimerStart(out long startMs, out DungeonRunTimerSource wireSource))
            return;

        var source = MapSource(wireSource);
        long previousMs = _state.RunTimerStartMs;
        var write = _sink.SetRunTimerStart(startMs, source);
        if (write == RunTimerWrite.Latched)
            DiagRunTimerLatched(SourceLabel(source), startMs, result.SceneUuid);
        else if (write == RunTimerWrite.Upgraded)
            DiagRunTimerUpgraded(SourceLabel(source), startMs, previousMs, result.SceneUuid);
    }

    // Wire-layer per-payload source → Application cross-delivery latch rank.
    private static RunTimerSource MapSource(DungeonRunTimerSource source) => source switch
    {
        DungeonRunTimerSource.TimerInfo    => RunTimerSource.TimerInfo,
        DungeonRunTimerSource.FlowPlayTime => RunTimerSource.FlowPlayTime,
        _                                  => RunTimerSource.FlowActiveTime,
    };

    // Diagnostic tag for the latch/upgrade lines — the approximate source is
    // labelled explicitly so a log reader never mistakes it for the exact edge.
    private static string SourceLabel(RunTimerSource source) => source switch
    {
        RunTimerSource.Method55Edge => "method55.edge",
        RunTimerSource.TimerInfo    => "timer_info",
        RunTimerSource.FlowPlayTime => "flow.play_time",
        _                           => "flow.active_time (approx, pre-countdown)",
    };
}
