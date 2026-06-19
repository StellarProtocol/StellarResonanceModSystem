namespace Stellar.Abstractions.Domain.Loadout;

/// <summary>Outcome of a loadout switch requested through <see cref="Stellar.Abstractions.Services.ILoadout"/>.</summary>
public enum LoadoutResult
{
    /// <summary>The game accepted and applied the loadout switch.</summary>
    Success,

    /// <summary>The game-side loadout API was not resolvable (e.g. before hot-update load).</summary>
    GameApiUnavailable,

    /// <summary>The player is not in a state where loadouts can be switched (not in world).</summary>
    PlayerNotInWorld,

    /// <summary>The game refused the switch because the player is in combat.</summary>
    InCombat,

    /// <summary>The requested loadout index does not correspond to a saved loadout.</summary>
    NoSuchLoadout,

    /// <summary>The game rejected the switch for another reason (e.g. profession/weapon mismatch).</summary>
    Rejected,

    /// <summary>The switch did not complete in time.</summary>
    Timeout,

    /// <summary>The request was cancelled before dispatch.</summary>
    Cancelled,
}
