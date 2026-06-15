namespace Stellar.Abstractions.Domain;

/// <summary>
/// Damage-element enum, mirroring <c>EDamageProperty</c> in the BPSR proto
/// schema (see <c>e_damage_property.py</c> in the BPSR-Meter reference).
/// Surfaces on <c>CombatEvent.DamageDealt.Element</c> via
/// <c>SyncDamageInfo.Property</c> (field 18).
///
/// <para>
/// <see cref="Count"/> is the proto-defined sentinel (= 9); it is not a real
/// element. Treat any wire value outside <c>0..8</c> as
/// <see cref="General"/> in plugin code.
/// </para>
/// </summary>
public enum DamageElement
{
    /// <summary>Non-elemental / physical damage.</summary>
    General     = 0,
    /// <summary>Fire-element damage.</summary>
    Fire        = 1,
    /// <summary>Water-element damage.</summary>
    Water       = 2,
    /// <summary>Electricity-element damage.</summary>
    Electricity = 3,
    /// <summary>Wood-element damage.</summary>
    Wood        = 4,
    /// <summary>Wind-element damage.</summary>
    Wind        = 5,
    /// <summary>Rock-element damage.</summary>
    Rock        = 6,
    /// <summary>Light-element damage.</summary>
    Light       = 7,
    /// <summary>Dark-element damage.</summary>
    Dark        = 8,
    /// <summary>Proto-defined sentinel (= 9); not a real element — treat wire values outside 0..8 as <see cref="General"/>.</summary>
    Count       = 9,
}
