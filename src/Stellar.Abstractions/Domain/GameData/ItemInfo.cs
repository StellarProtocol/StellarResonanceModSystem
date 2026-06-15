namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a single item from the game table.</summary>
/// <param name="Id">Game-table item id.</param>
/// <param name="Name">Localised item display name.</param>
/// <param name="Description">Localised item description text.</param>
/// <param name="IconPath">Addressable path for the item's icon sprite.</param>
/// <param name="Quality">Quality tier (higher = rarer).</param>
/// <param name="Kind">Item category.</param>
/// <param name="GroupId">Item group id used to cluster stackable / related items.</param>
public readonly record struct ItemInfo(int Id, string Name, string Description, string IconPath, int Quality, ItemKind Kind, int GroupId);
