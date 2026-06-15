namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a player title from the game table.</summary>
/// <param name="Id">Game-table title id.</param>
/// <param name="Name">Localised title display name.</param>
/// <param name="Description">Localised title description text.</param>
/// <param name="ColorRgba">Packed RGBA colour for the title text (format matches <see cref="Domain.ColorRgba.FromHex"/>).</param>
public readonly record struct TitleInfo(int Id, string Name, string Description, uint ColorRgba);
