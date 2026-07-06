namespace Stellar.Wire;

/// <summary>
/// Fields lifted out of a <c>WorldNtf.SyncDungeonDirtyData</c> delta blob by
/// <see cref="DungeonDirtyDataReader"/> — only the <c>timer_info</c> (container
/// field 15) slice, which is the delta path the game's own dungeon-timer HUD is
/// fed by (a run's true <c>start_time</c> arrives HERE, not on the method-23
/// entry sync whose timer_info is all-zero).
/// </summary>
public readonly struct DungeonDirtyTimerResult
{
    /// <summary>True when the delta carried a <c>timer_info</c> container (field 15), even an empty one.</summary>
    public bool HasTimerInfo { get; init; }

    /// <summary>
    /// <c>DungeonTimerInfo.start_time</c> (container field 2) — epoch seconds
    /// (the HUD multiplies by 1000, <c>dungeon_timer_vm.lua getEndTimeStamp</c>).
    /// 0 when the delta didn't touch it.
    /// </summary>
    public int StartTimeSeconds { get; init; }

    /// <summary><c>dungeon_times</c> (field 3) — 0 when untouched; diagnostics only.</summary>
    public int DungeonTimes { get; init; }

    /// <summary><c>direction</c> (field 4) — 0 when untouched; diagnostics only.</summary>
    public int Direction { get; init; }

    /// <summary><c>pause_total_time</c> (field 9) — 0 when untouched; diagnostics only.</summary>
    public int PauseTotalTime { get; init; }

    /// <summary>True when the delta carried a flow_info container (field 2) with a result.</summary>
    public bool HasFlowResult { get; init; }
    /// <summary><c>DungeonFlowInfo.result</c> (field 8): 0=Null, 1=Success, 2=Failed. 0 when untouched.</summary>
    public int FlowResult { get; init; }

    /// <summary>True when the delta carried a settlement container (field 7).</summary>
    public bool HasSettlement { get; init; }
    /// <summary><c>DungeonSettlement.pass_time</c> (field 1) — authoritative clear time seconds.</summary>
    public int PassTimeSeconds { get; init; }
    /// <summary><c>DungeonSettlement.master_mode_score</c> (field 5) — the max/par score.</summary>
    public int MasterModeScore { get; init; }

    /// <summary>True when the delta carried a <c>dungeon_score</c> container (field 14).</summary>
    public bool HasScore { get; init; }
    /// <summary><c>DungeonScore.total_score</c> (field 14 → 1) — the achieved score the settlement screen shows.</summary>
    public int TotalScore { get; init; }

    /// <summary>True when the delta carried a <c>dungeon_scene_info</c> container (field 21), even an empty one.</summary>
    public bool HasSceneInfo { get; init; }

    /// <summary><c>DungeonSceneInfo.difficulty</c> (field 1) — the Master level (1-20). 0 when untouched.</summary>
    public int Difficulty { get; init; }

    /// <summary><see cref="StartTimeSeconds"/> as epoch milliseconds (the framework's run-timer unit).</summary>
    public long RunTimerStartMs => StartTimeSeconds * 1000L;
}
