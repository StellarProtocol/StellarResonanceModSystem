using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

// ---------------------------------------------------------------------------
// Sub-interfaces (facade-inheritance; each has a single cohesive concern)
// ---------------------------------------------------------------------------

/// <summary>
/// Identity and availability facet of the local player's state.
/// </summary>
/// <remarks>
/// <see cref="IsAvailable"/> gates all other properties: when it is
/// <c>false</c> (title / character select / loading screens) the remaining
/// properties return defaults (empty string, zero).
/// </remarks>
public interface IPlayerIdentity
{
    /// <summary>True when a character is loaded and the snapshot fields are meaningful.</summary>
    bool IsAvailable { get; }

    /// <summary>Character display name; null when not yet loaded.</summary>
    string? Name { get; }
    /// <summary>Character level; zero when not yet loaded.</summary>
    int Level { get; }
    /// <summary>Primary profession id; zero when not yet loaded.</summary>
    int Profession { get; }
}

/// <summary>
/// Vitals (HP / Stamina) facet of the local player's state.
/// </summary>
/// <remarks>
/// Phase 9a supplement renamed Mana → Stamina to match Star Resonance's
/// own terminology. The underlying probe (<c>PandaPlayerStateProbe</c>)
/// already reads the game's <c>LuaOriginEnergy</c> / <c>LuaMaxOriEnergy</c>
/// fields, which are the stamina pool — the rename is only the C# identifier
/// on our side. See
/// <c>docs/superpowers/specs/2026-05-29-phase-9a-layout-primitives-design.md</c>.
/// </remarks>
public interface IPlayerVitals
{
    /// <summary>Current HP; zero before the player is in-world.</summary>
    int Health { get; }
    /// <summary>Maximum HP; zero before the player is in-world.</summary>
    int MaxHealth { get; }

    /// <summary>Current stamina (origin energy); zero before the player is in-world.</summary>
    int Stamina { get; }
    /// <summary>Maximum stamina; zero before the player is in-world.</summary>
    int MaxStamina { get; }
}

/// <summary>
/// Location facet of the local player's state.
/// </summary>
public interface IPlayerLocation
{
    /// <summary>World-space position of the local player; <see cref="Position3D.Zero"/> before the player is in-world.</summary>
    Position3D Position { get; }
}

// ---------------------------------------------------------------------------
// Facade — zero declared members; all members come from the sub-interfaces.
// Existing consumers and implementors are unaffected.
// ---------------------------------------------------------------------------

/// <summary>
/// Read-only view of the local player's basic state. All properties are
/// safe to read at any time; when <see cref="IPlayerIdentity.IsAvailable"/> is <c>false</c>
/// (e.g. on title / character select / loading screens) the other
/// properties return defaults (empty string, zero, <see cref="Position3D.Zero"/>).
/// </summary>
/// <remarks>
/// The service is polled — the framework refreshes the snapshot once per
/// game tick (via <c>Panda.Core.Game.Update</c>). Plugins typically read
/// the values from their own <c>IFramework.Update</c> handler.
///
/// v1 surface: Name, Level, HP, Stamina, Position. XP / buffs / target / class
/// are deferred to a later phase.
/// </remarks>
public interface IPlayerState : IPlayerIdentity, IPlayerVitals, IPlayerLocation { }
