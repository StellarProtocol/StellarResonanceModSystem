namespace Stellar.Abstractions.Domain;

/// <summary>
/// Settlement (clear/result) summary for a dungeon run, decoded from
/// <c>WorldNtf.SyncDungeonData.settlement</c>. Present only once a run reaches
/// its result screen; until then <see cref="Stellar.Abstractions.Services.IDungeonState.LastSettlement"/>
/// is <see langword="null"/>.
/// </summary>
/// <param name="PassTimeSeconds">Clear time in seconds (<c>DungeonSettlement.pass_time</c>).</param>
/// <param name="MasterModeScore">Master-mode score (<c>DungeonSettlement.master_mode_score</c>); 0 when not a scored/master run.</param>
public readonly record struct DungeonSettlementInfo(
    int PassTimeSeconds,
    int MasterModeScore);
