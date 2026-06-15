namespace Stellar.Abstractions.Domain;

/// <summary>
/// Damage-source enum mirroring <c>EDamageSource</c> on the wire (see
/// <c>e_damage_source.py</c> in the BPSR-Meter reference). Values must
/// match the wire int because <c>SyncDamageInfo.DamageSource</c> is cast
/// straight into this enum. Note <c>Other</c> is 100 (not 5) per the
/// canonical proto — keep this in sync with the live game.
/// </summary>
public enum DamageSourceKind
{
    /// <summary>Direct skill hit.</summary>
    Skill      = 0,
    /// <summary>Projectile / bullet hit.</summary>
    Bullet     = 1,
    /// <summary>Buff / DoT tick damage.</summary>
    Buff       = 2,
    /// <summary>Fall / environmental damage.</summary>
    Fall       = 3,
    /// <summary>Fake / non-real projectile hit.</summary>
    FakeBullet = 4,
    /// <summary>Unclassified damage source (wire value 100).</summary>
    Other      = 100,
}
