namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a single monster type from the game table.</summary>
/// <param name="Id">Game-table monster id.</param>
/// <param name="Name">Localised monster display name.</param>
/// <param name="Level">Base level of this monster type.</param>
/// <param name="FactionId">Faction id this monster belongs to.</param>
/// <param name="IconPath">Addressable path for the monster's icon sprite.</param>
/// <param name="MonsterType">Numeric monster classification (0=Monster, 1=Elite, 2=Boss).
/// Mirrors <c>EMonsterType</c> from the zproto enum. <c>MonsterRank</c> is empty for all
/// table rows and must not be used for classification — use this field instead.</param>
/// <param name="IsBoss">
/// <c>true</c> when <see cref="MonsterType"/> equals <see cref="MonsterTypeBoss"/> (2).
/// Derived at load time from the table row. Confirmed by recon on the Ancient Purifier
/// run: attr-10 → MonsterTable[33301].MonsterType == 2.
/// </param>
public readonly record struct MonsterInfo(
    int Id,
    string Name,
    int Level,
    int FactionId,
    string IconPath,
    int MonsterType = 0,
    bool IsBoss = false)
{
    /// <summary>
    /// The <c>MonsterType</c> value that identifies a boss —
    /// <c>EMonsterType.Boss = 2</c> (confirmed by recon 2026-07-02).
    /// </summary>
    public const int MonsterTypeBoss = 2;
}
