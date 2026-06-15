namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a damage-attribute definition from the game table.</summary>
/// <param name="Id">Game-table damage attribute id.</param>
/// <param name="Name">Localised damage attribute name.</param>
/// <param name="ElementKind">Elemental kind integer corresponding to <see cref="Domain.DamageElement"/>.</param>
/// <param name="BaseValue">Base damage value for this attribute.</param>
public readonly record struct DamageAttrInfo(int Id, string Name, int ElementKind, int BaseValue);
