using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Pure boss-classification rule derived from <c>MonsterTableBase.MonsterType</c>.
/// Rule pinned by <c>recon/replay-boss-identification-notes.md</c> (2026-07-02):
/// <c>EMonsterType.Boss == 2</c>; <c>MonsterRank</c> is empty for all rows and
/// must not be used.
/// </summary>
internal static class MonsterBossRule
{
    /// <summary>
    /// Returns <c>true</c> when <paramref name="monsterType"/> equals the boss
    /// value (<c>EMonsterType.Boss = 2</c>).
    /// </summary>
    public static bool IsBoss(int monsterType)
        => monsterType == MonsterInfo.MonsterTypeBoss;
}
