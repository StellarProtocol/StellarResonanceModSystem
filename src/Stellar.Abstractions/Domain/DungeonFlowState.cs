namespace Stellar.Abstractions.Domain;

/// <summary>
/// Dungeon flow state machine (<c>zproto.EDungeonState</c>), carried on
/// <c>DungeonFlowInfo.state</c> (field 1 of <c>DungeonSyncData.flow_info</c>). Values mirror the
/// game's <c>enum_e_dungeon_state.proto</c> exactly; unknown future wire values are surfaced
/// as-is (cast), so consumers must tolerate values outside the named set.
/// </summary>
public enum DungeonFlowState
{
    /// <summary>No flow observed yet / not in an instanced run (wire <c>DungeonStateNull</c>).</summary>
    None = 0,
    /// <summary>Instance activated — players entering, pre-ready (wire <c>DungeonStateActive</c>).</summary>
    Active = 1,
    /// <summary>Ready countdown running (wire <c>DungeonStateReady</c>).</summary>
    Ready = 2,
    /// <summary>Run in progress (wire <c>DungeonStatePlaying</c>).</summary>
    Playing = 3,
    /// <summary>Run ended, pre-settlement (wire <c>DungeonStateEnd</c>).</summary>
    End = 4,
    /// <summary>Settlement / result-screen phase (wire <c>DungeonStateSettlement</c>).</summary>
    Settlement = 5,
    /// <summary>Post-run vote phase (wire <c>DungeonStateVote</c>).</summary>
    Vote = 6,
}
