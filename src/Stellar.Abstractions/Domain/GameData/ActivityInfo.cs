namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a timed in-game activity from the game table.</summary>
/// <param name="Id">Game-table activity id.</param>
/// <param name="Name">Localised activity display name.</param>
/// <param name="Description">Localised activity description.</param>
/// <param name="StartUnixMs">Activity start time as Unix epoch milliseconds.</param>
/// <param name="EndUnixMs">Activity end time as Unix epoch milliseconds.</param>
public readonly record struct ActivityInfo(int Id, string Name, string Description, long StartUnixMs, long EndUnixMs);
