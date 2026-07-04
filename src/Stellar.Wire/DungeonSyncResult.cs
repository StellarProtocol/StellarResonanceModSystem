namespace Stellar.Wire;

/// <summary>
/// Decoded result of a <c>WorldNtf.SyncDungeonData</c> structural parse. A
/// value with <see cref="SceneUuid"/> != 0 is the per-run unique id that the
/// StellarLogs upload plugin consumes as <c>level_uuid</c>. When
/// <see cref="HasSettlement"/> is <see langword="true"/> the run reached a
/// settlement (clear/result screen) and <see cref="PassTimeSeconds"/> /
/// <see cref="MasterModeScore"/> are populated.
/// </summary>
public readonly struct DungeonSyncResult
{
    /// <summary>The per-run unique scene id (<c>DungeonSyncData.scene_uuid</c>, field 1). Non-zero on a valid parse.</summary>
    public long SceneUuid { get; init; }

    /// <summary>True when a <c>DungeonSettlement</c> (field 7) was present — i.e. the run reached its clear/result screen.</summary>
    public bool HasSettlement { get; init; }

    /// <summary>Clear time in seconds (<c>DungeonSettlement.pass_time</c>, field 1). Only meaningful when <see cref="HasSettlement"/>.</summary>
    public int PassTimeSeconds { get; init; }

    /// <summary>Master-mode score (<c>DungeonSettlement.master_mode_score</c>, field 5). Only meaningful when <see cref="HasSettlement"/>.</summary>
    public int MasterModeScore { get; init; }

    /// <summary>
    /// True when a <c>DungeonSceneInfo</c> (field 21) was present on this
    /// payload — i.e. <see cref="DungeonDifficulty"/> is meaningful.
    /// </summary>
    public bool HasDungeonSceneInfo { get; init; }

    /// <summary>
    /// Raw value of <c>DungeonSceneInfo.difficulty</c> (field 1, varint) inside
    /// <c>DungeonSyncData.dungeon_scene_info</c> (field 21). Only meaningful when
    /// <see cref="HasDungeonSceneInfo"/>.
    /// <para>
    /// <b>Semantic UNCONFIRMED</b>: this is the value the lobby's Master 1-20
    /// selector should land on, but whether it carries the raw 1-20 challenge
    /// level or a small tier enum (normal/hard/master) has not been verified
    /// against a real Master-tier run yet. Treat as a diagnostic value until
    /// confirmed; consumers should not assume it is the literal level number.
    /// </para>
    /// </summary>
    public int DungeonDifficulty { get; init; }

    /// <summary>
    /// True when a <c>DungeonFlowInfo</c> (field 2) was present on this payload —
    /// i.e. <see cref="FlowInfo"/> is meaningful. This is the PRIMARY source of
    /// the run-timer start (<see cref="DungeonFlowInfo.PlayTimeMs"/>): a live run
    /// falsified <c>timer_info.start_time</c> (it arrived all-zero on the first
    /// hub delivery and never carried the start).
    /// </summary>
    public bool HasFlowInfo { get; init; }

    /// <summary>
    /// Decoded <c>DungeonSyncData.flow_info</c> (field 2) — the dungeon
    /// state-machine snapshot. Only meaningful when <see cref="HasFlowInfo"/>.
    /// </summary>
    public DungeonFlowInfo FlowInfo { get; init; }

    /// <summary>
    /// Raw <c>DungeonTimerInfo</c> fields (<c>type</c>, <c>dungeon_times</c>,
    /// <c>direction</c>, <c>pause_time</c>, <c>pause_total_time</c>,
    /// <c>cur_pause_timestamp</c>) captured alongside <see cref="RunTimerStartMs"/>
    /// purely for the one-shot diagnostic log — not otherwise surfaced through
    /// <c>IDungeonState</c>. Only meaningful when <see cref="HasTimerInfo"/>.
    /// </summary>
    public int TimerType { get; init; }

    /// <summary>Raw <c>DungeonTimerInfo.dungeon_times</c> (field 3). Diagnostic only; see <see cref="TimerType"/>.</summary>
    public int TimerDungeonTimes { get; init; }

    /// <summary>Raw <c>DungeonTimerInfo.direction</c> (field 4). Diagnostic only; see <see cref="TimerType"/>.</summary>
    public int TimerDirection { get; init; }

    /// <summary>Raw <c>DungeonTimerInfo.pause_time</c> (field 8). Diagnostic only; see <see cref="TimerType"/>.</summary>
    public int TimerPauseTime { get; init; }

    /// <summary>Raw <c>DungeonTimerInfo.pause_total_time</c> (field 9). Diagnostic only; see <see cref="TimerType"/>.</summary>
    public int TimerPauseTotalTime { get; init; }

    /// <summary>Raw <c>DungeonTimerInfo.cur_pause_timestamp</c> (field 11). Diagnostic only; see <see cref="TimerType"/>.</summary>
    public int TimerCurPauseTimestamp { get; init; }

    /// <summary>
    /// True when a <c>DungeonTimerInfo</c> (field 15) was present on this
    /// payload — i.e. <see cref="RunTimerStartMs"/> is meaningful.
    /// </summary>
    public bool HasTimerInfo { get; init; }

    /// <summary>
    /// <c>DungeonTimerInfo.start_time</c> (field 2, varint) inside
    /// <c>DungeonSyncData.timer_info</c> (field 15), converted seconds → ms.
    /// Only meaningful when <see cref="HasTimerInfo"/>.
    /// <para>
    /// <b>DIAGNOSTIC ONLY</b> — a live run showed this field arriving all-zero
    /// and never carrying the run-timer start. The authoritative run-timer start
    /// is <c>flow_info.play_time</c> (<see cref="DungeonFlowInfo.PlayTimeMs"/>);
    /// this value is retained purely for the one-shot diagnostic log and no
    /// longer feeds <c>IDungeonState.RunTimerStartMs</c>.
    /// </para>
    /// </summary>
    public long RunTimerStartMs { get; init; }
}

/// <summary>
/// Decoded <c>DungeonFlowInfo</c> (per
/// <c>proto/zproto/stru_dungeon_flow_info.proto</c>) — the dungeon flow
/// state-machine snapshot carried on <c>DungeonSyncData.flow_info</c> (field 2).
/// <see cref="PlayTime"/> is the PRIMARY source of the run-timer start consumed
/// by <c>IDungeonState.RunTimerStartMs</c>.
/// </summary>
public readonly struct DungeonFlowInfo
{
    /// <summary>Raw <c>EDungeonState state</c> (field 1, varint enum) — the dungeon flow state.</summary>
    public int State { get; init; }

    /// <summary>Raw <c>active_time</c> (field 2, varint).</summary>
    public int ActiveTime { get; init; }

    /// <summary>Raw <c>ready_time</c> (field 3, varint) — epoch when the Ready countdown began.</summary>
    public int ReadyTime { get; init; }

    /// <summary>
    /// Raw <c>play_time</c> (field 4, varint) — epoch (assumed SECONDS, same
    /// caveat as the retired <c>timer_info.start_time</c>) when play officially
    /// begins after the Ready countdown. Zero until the run actually starts —
    /// consumers MUST NOT latch a zero value.
    /// </summary>
    public int PlayTime { get; init; }

    /// <summary>Raw <c>end_time</c> (field 5, varint).</summary>
    public int EndTime { get; init; }

    /// <summary>Raw <c>settlement_time</c> (field 6, varint).</summary>
    public int SettlementTime { get; init; }

    /// <summary>Raw <c>dungeon_times</c> (field 7, varint).</summary>
    public int DungeonTimes { get; init; }

    /// <summary>
    /// Raw <c>result</c> (field 8, varint). Future candidate for a wire-accurate
    /// kill/partial run verdict — parsed and logged (diagnostics) only, NOT wired
    /// into any consumer yet.
    /// </summary>
    public int Result { get; init; }

    /// <summary>
    /// <see cref="PlayTime"/> converted to epoch ms (<c>* 1000L</c>, assuming the
    /// raw value is epoch seconds). The PRIMARY driver of
    /// <c>IDungeonState.RunTimerStartMs</c>. Zero when the run has not started.
    /// </summary>
    public long PlayTimeMs => PlayTime * 1000L;
}
