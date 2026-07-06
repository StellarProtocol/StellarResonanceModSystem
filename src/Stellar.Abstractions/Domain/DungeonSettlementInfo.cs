namespace Stellar.Abstractions.Domain;

/// <summary>
/// Settlement (clear/result) summary for a dungeon run, decoded from
/// <c>WorldNtf.SyncDungeonData.settlement</c>. Present only once a run reaches
/// its result screen; until then <see cref="Stellar.Abstractions.Services.IDungeonState.LastSettlement"/>
/// is <see langword="null"/>.
/// </summary>
/// <param name="PassTimeSeconds">Clear time in seconds (<c>DungeonSettlement.pass_time</c>).</param>
/// <param name="MasterModeScore">Master-mode MAX/PAR score (<c>DungeonSettlement.master_mode_score</c>, field 5); 0 when not a scored/master run.</param>
/// <param name="TotalScore">The ACHIEVED "Total Score" the settlement screen shows (<c>DungeonScore.total_score</c>, field 14); the numerator in the "686/700" pairing. 0 when not a scored run.</param>
public readonly record struct DungeonSettlementInfo(
    int PassTimeSeconds,
    int MasterModeScore,
    int TotalScore);
