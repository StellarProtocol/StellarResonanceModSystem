namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a weapon type from the game table.</summary>
/// <param name="Id">Game-table weapon id.</param>
/// <param name="Name">Localised weapon display name.</param>
/// <param name="Kind">Weapon classification.</param>
/// <param name="BaseDamage">Base damage value for this weapon type.</param>
public readonly record struct WeaponInfo(int Id, string Name, WeaponKind Kind, int BaseDamage);
