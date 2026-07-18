namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Static data for a single Unity scene entry from the game table.</summary>
/// <param name="Id">Game-table scene id (also used as <see cref="Services.IClientState.CurrentSceneName"/>).</param>
/// <param name="Name">Localised scene display name.</param>
/// <param name="MapId">Map id this scene belongs to.</param>
/// <param name="SceneKind">The scene table's <c>SceneType</c> value (e.g. world/town/field = 1, instanced dungeon/raid content = 2).</param>
public readonly record struct SceneInfo(int Id, string Name, int MapId, int SceneKind);
