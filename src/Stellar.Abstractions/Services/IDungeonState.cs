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
    /// (<c>DungeonSyncData.timer_info</c>, field 15 → <c>DungeonTimerInfo.start_time</c>,
    /// field 2), or 0 when absent / not yet seen.
    /// <para>
    /// <b>Semantic UNCONFIRMED</b> (see <see cref="CurrentDifficulty"/> for
    /// precedent) — <c>start_time</c> is assumed to be an epoch timestamp in
    /// SECONDS and is converted to ms here. Treat as diagnostic until confirmed
    /// against a real run.
    /// </para>
    /// </summary>
    long RunTimerStartMs { get; }
}
