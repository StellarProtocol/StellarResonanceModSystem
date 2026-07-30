using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

// ---------------------------------------------------------------------------
// Sub-interfaces (facade-inheritance; each has a single cohesive concern)
// ---------------------------------------------------------------------------

/// <summary>
/// Identity and availability facet of the local player's state.
/// </summary>
/// <remarks>
/// <para><see cref="IsAvailable"/> reflects whether the live world entity is
/// readable, and it gates the <see cref="IPlayerVitals"/> and
/// <see cref="IPlayerLocation"/> facets: when it is <c>false</c> (title /
/// character select / loading screens) those return defaults (zero,
/// <see cref="Position3D.Zero"/>).</para>
///
/// <para><b>The three identity properties below are deliberately NOT gated by
/// <see cref="IsAvailable"/>.</b> They fall back to the character record, so
/// they can be populated while <see cref="IsAvailable"/> is <c>false</c> — the
/// client knows who the player is even in states where the world entity's
/// attribute bag reads empty (e.g. after relaunching while mounted). Check the
/// individual property for null/zero rather than gating identity reads on
/// <see cref="IsAvailable"/>.</para>
/// </remarks>
public interface IPlayerIdentity
{
    /// <summary>
    /// True when the live world entity is readable and the
    /// <see cref="IPlayerVitals"/> / <see cref="IPlayerLocation"/> fields are
    /// meaningful. Identity (<see cref="Name"/> / <see cref="Level"/> /
    /// <see cref="Profession"/>) may be available even when this is <c>false</c>.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>Character display name; null when not yet known. May be set while <see cref="IsAvailable"/> is <c>false</c>.</summary>
    string? Name { get; }
    /// <summary>Character level; zero when not yet known. May be set while <see cref="IsAvailable"/> is <c>false</c>.</summary>
    int Level { get; }
    /// <summary>Current profession id; zero when not yet known. May be set while <see cref="IsAvailable"/> is <c>false</c>.</summary>
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
/// (e.g. on title / character select / loading screens) the vitals and position
/// return defaults (zero, <see cref="Position3D.Zero"/>). Identity — name,
/// level, profession — is served from the character record and can be populated
/// independently of <see cref="IPlayerIdentity.IsAvailable"/>.
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
