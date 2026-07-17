using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Read-only view of the current dungeon run, decoded from the game's
/// <c>WorldNtf.SyncDungeonData</c> notification. Surfaces the per-run unique id
/// (<see cref="CurrentRunId"/>) that an upload/logging plugin needs to key a run
/// (the StellarLogs <c>level_uuid</c>), plus the clear-time / score once the run
/// settles (<see cref="LastSettlement"/>).
///
/// <para>
/// Populated on the network receive thread and read on the main thread;
/// implementations publish via volatile reads so consumers are lock-free.
/// <see cref="CurrentRunId"/> resets to 0 on leave-scene / logout.
/// </para>
/// </summary>
public interface IDungeonState
{
    /// <summary>
    /// The unique id of the dungeon run currently in progress — the
    /// server-assigned per-instance scene uuid, stable across the whole run and
    /// shared by every client in it. 0 when not in a dungeon / between runs. Use
    /// this as the run key (e.g. <c>level_uuid</c> for log uploads).
    /// </summary>
    long CurrentRunId { get; }

    /// <summary>
    /// The most recent settlement (clear/result) summary, or <see langword="null"/>
    /// if the current run has not reached its result screen yet. Carries the
    /// clear time and master-mode score.
    /// </summary>
    DungeonSettlementInfo? LastSettlement { get; }

    /// <summary>
    /// Raw value of <c>DungeonSceneInfo.difficulty</c> (<c>DungeonSyncData.dungeon_scene_info</c>,
    /// field 21) for the current run, or 0 when not yet seen / not applicable.
    /// <para>
    /// <b>Semantic UNCONFIRMED.</b> The lobby lets the player pick a "Master
    /// 1-20" challenge level; game tables only expose the tier
    /// (normal/hard/master). This value travels on the wire and is the
    /// candidate for the numeric level, but whether it carries the literal
    /// 1-20 level or a small tier enum has not been confirmed against a real
    /// Master-tier run. Treat it as diagnostic until confirmed.
    /// </para>
    /// </summary>
    int CurrentDifficulty { get; }

    /// <summary>
    /// Server epoch ms when the dungeon run-timer started
    /// (<c>DungeonSyncData.flow_info</c>, field 2 → <c>DungeonFlowInfo.play_time</c>,
    /// field 4 — the epoch when play officially begins after the Ready
    /// countdown), or 0 when not yet seen for the current run. Only non-zero
    /// wire values are latched; the value clears when a new run begins and
    /// survives the dungeon→town transition so it is readable at archive time.
    /// <para>
    /// <b>Semantic caveat</b> (see <see cref="CurrentDifficulty"/> for
    /// precedent) — <c>play_time</c> is assumed to be an epoch timestamp in
    /// SECONDS and is converted to ms here. The earlier
    /// <c>timer_info.start_time</c> source was falsified by a live run (arrived
    /// all-zero) and is now diagnostic-only.
    /// </para>
    /// </summary>
    long RunTimerStartMs { get; }

    /// <summary>Outcome of the current/just-finished run (from flow_info.result). None until resolved.</summary>
    Stellar.Abstractions.Domain.DungeonOutcome LastOutcome { get; }

    /// <summary>Enemies defeated this run (World attr AttrDeathCount=348); 0 if unknown.</summary>
    int LastDefeatedCount { get; }

    /// <summary>
    /// Current dungeon flow state (<c>DungeonFlowInfo.state</c>) for the live run —
    /// <see cref="Stellar.Abstractions.Domain.DungeonFlowState.None"/> when not in an instanced
    /// run or not yet observed. Sourced from BOTH dungeon delivery paths: the method-23 full sync
    /// (entry/rejoin) and the method-24 dirty delta (mid-run transitions). Cleared when a new run
    /// begins and on logout reset; like the settlement, it survives the run-id drop-to-0 on
    /// leaving to town (consumers gate on <see cref="CurrentRunId"/> for "in a run").
    /// </summary>
    Stellar.Abstractions.Domain.DungeonFlowState CurrentFlowState { get; }

    /// <summary>
    /// Monotonic transition counter for <see cref="CurrentFlowState"/> — increments once per
    /// observed state CHANGE, never for a same-value re-delivery. This is the change-notification
    /// mechanism: state is written on the network receive thread, so consumers POLL this counter
    /// (e.g. on their frame tick) instead of subscribing to a cross-thread event. Resets to 0
    /// when a new run begins and on logout reset — treat a DECREASE as "new run, adopt silently".
    /// </summary>
    int FlowStateVersion { get; }
}
