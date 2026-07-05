namespace Stellar.Abstractions.Domain;

/// <summary>Outcome of a dungeon run, from <c>DungeonFlowInfo.result</c> (field 8).
/// Mirrors the game's <c>DungeonResult</c> enum.</summary>
public enum DungeonOutcome
{
    /// <summary>Run not resolved (in progress / left before result).</summary>
    None = 0,
    /// <summary>Cleared successfully.</summary>
    Success = 1,
    /// <summary>Failed / wiped.</summary>
    Failed = 2,
}
