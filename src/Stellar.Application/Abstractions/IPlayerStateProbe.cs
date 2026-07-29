using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound interface — produces a fresh snapshot of the local player's state
/// from whatever game-internal source is available. Implemented in
/// <c>Stellar.Infrastructure</c> by walking the live Panda hot-update objects.
/// </summary>
internal interface IPlayerStateProbe
{
    /// <summary>
    /// Attempts to sample the local player. Returns <c>true</c> with a populated
    /// <paramref name="snapshot"/> when a character is loaded and at least the
    /// minimum useful state could be read; <c>false</c> otherwise.
    /// </summary>
    bool TrySample(out PlayerStateSnapshot snapshot);

    /// <summary>
    /// Attempts to read the local player's STABLE identity (name / level /
    /// profession) from a source that does NOT depend on the live world
    /// entity's attribute bag.
    ///
    /// <para>This exists because <see cref="TrySample"/> reads everything off
    /// the world entity, whose attribute bag can be empty even while the client
    /// plainly knows who the player is — reported after relaunching while
    /// mounted, where the entity yields <c>hp=0 lvl=0 name=''</c> and every
    /// <c>IPlayerState</c> consumer degrades at once. Identity read through this
    /// path survives that blackout; vitals and position legitimately do not,
    /// and stay gated on <see cref="TrySample"/>. See
    /// <c>docs/recon/playerstate-probe-mounted-blackout.md</c>.</para>
    ///
    /// <para>Returns <c>false</c> until the underlying record is readable, so a
    /// caller must treat failure as "not known yet", never as "empty".</para>
    /// </summary>
    bool TryReadIdentity(out PlayerIdentitySnapshot identity);
}

/// <summary>
/// Plain DTO returned by <see cref="IPlayerStateProbe.TryReadIdentity"/> — the
/// slow-moving identity fields only, deliberately carrying no vitals/position.
/// </summary>
internal readonly struct PlayerIdentitySnapshot
{
    /// <summary>
    /// Character id the identity belongs to; zero when unknown. Used to drop
    /// cached identity when the record switches to a different character.
    /// </summary>
    public long CharId { get; init; }

    /// <summary>Character display name; null/empty when unknown.</summary>
    public string? Name { get; init; }

    /// <summary>Character level; zero when unknown.</summary>
    public int Level { get; init; }

    /// <summary>CURRENT profession id (not the initial one); zero when unknown.</summary>
    public int Profession { get; init; }
}

/// <summary>Plain DTO returned by <see cref="IPlayerStateProbe.TrySample"/>.</summary>
internal readonly struct PlayerStateSnapshot
{
    public string? Name { get; init; }
    public int Level { get; init; }
    public int Profession { get; init; }
    public int Health { get; init; }
    public int MaxHealth { get; init; }
    public int Stamina { get; init; }
    public int MaxStamina { get; init; }
    public Position3D Position { get; init; }
}
