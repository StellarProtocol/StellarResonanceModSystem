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
/// Run-timer note: the parsed <c>flow_info.active_time</c> latches an
/// APPROXIMATE run clock (scene-entry time, ~countdown early);
/// <c>timer_info.start_time</c> latches the true clock when the full sync
/// carries it (observed on mid-run rejoin). Capturing the true clock on FRESH
/// entry requires the dungeon container's dirty-DELTA (WorldNtf method 24,
/// <c>SyncDungeonDirtyData</c>), confirmed reachable via
/// <c>WorldNtfStub.OnCallStub</c> (census 2026-07-05) and now tapped by
/// <see cref="OnDungeonDirty"/> below. The full evidence trail and every
/// falsified route that preceded this live in
/// <c>docs/recon/dungeon-clock-recon.md</c> (devkit).
/// </para>
///
/// <para>
/// The C# stub path handles inline on the network receive thread — this path
/// has run crash-free for weeks. Never throws across the IL2CPP boundary (the
/// dispatcher wraps handler calls, and the readers are fully defensive).
/// Diagnostics live in <c>PandaDungeonProbe.Diagnostics.cs</c>.
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
    /// Register on the C# stub path for SyncDungeonData (id 23, confirmed from
    /// the game's lua WorldNtf dispatcher) and SyncDungeonDirtyData (id 24,
    /// confirmed via <c>OnCallStub</c> census 2026-07-05 — the true
    /// <c>timer_info.start_time</c> carrier on fresh entry). The earlier
    /// clock-hunt-era taps (lua stub 23/55, method 24 on every transport,
    /// MessagePipe subscription, OnSync delegate wrap) were all removed
    /// 2026-07-05 after live falsification; see docs/recon/dungeon-clock-recon.md.
    /// Must be called before <see cref="WorldNtfStubDispatcher.Install"/>.
    /// </summary>
    public void RegisterWith(WorldNtfStubDispatcher dispatcher)
    {
        dispatcher.Register(WorldNtfMethodIds.SyncDungeonData, OnWorldNtf);
        dispatcher.Register(WorldNtfMethodIds.SyncDungeonDirtyData, OnDungeonDirty); // true start_time
    }

    /// <summary>
    /// Clear the active run id + settlement. Wired to logout so a stale run id
    /// doesn't linger between sessions. NOT wired to leave-scene — the run id is
    /// deliberately held through the return-to-town transition so the upload
    /// plugin can archive against the dungeon id after the player has left.
    /// </summary>
    public void OnLeaveOrLogout() => _sink.Reset();

    private void OnWorldNtf(uint methodId, byte[] payload) => HandleDelivery("cs", methodId, payload);

    // SyncDungeonDirtyData (WorldNtf method 24, dirty-delta) — the ONLY carrier
    // of the true timer_info.start_time on fresh entry (the method-23 full
    // sync's timer_info is all-zero on entry; see class doc). Inline on the
    // network receive thread, crash-safe: the reader is fully defensive and the
    // dispatcher wraps this in try/catch. Feeds the SAME rank-latch as the full
    // sync, so a valid delta UPGRADES the approximate flow.active_time latch.
    private void OnDungeonDirty(uint methodId, byte[] payload)
    {
        if (!DungeonDirtyDataReader.TryReadTimerStart(payload, out var d))
            return;

        if (d.StartTimeSeconds > 0)
            _sink.SetRunTimerStart(d.RunTimerStartMs, RunTimerSource.TimerInfo);

        if (d.HasFlowResult && d.FlowResult > 0)
            _sink.SetOutcome(d.FlowResult);

        if ((d.HasSettlement || d.HasScore) && (d.PassTimeSeconds > 0 || d.MasterModeScore > 0 || d.TotalScore > 0))
            _sink.SetSettlement(d.PassTimeSeconds, d.MasterModeScore, d.TotalScore);

        if (d.HasSceneInfo && d.Difficulty > 0)
            _sink.SetDifficulty(d.Difficulty);

        DiagDungeonDirtyDelta(methodId, d);
    }

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

        if (result.HasSettlement || result.HasScore)
            _sink.SetSettlement(result.PassTimeSeconds, result.MasterModeScore, result.TotalScore);

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
