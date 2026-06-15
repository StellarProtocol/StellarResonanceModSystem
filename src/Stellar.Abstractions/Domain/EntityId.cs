namespace Stellar.Abstractions.Domain;

/// <summary>
/// Strongly-typed wrapper around the int64 entity UUID used by the BPSR
/// world server. The low 16 bits encode the entity type (640 = player,
/// 64 / 32832 = monster); the upper bits encode the numeric uid.
/// </summary>
public readonly record struct EntityId(long Value)
{
    /// <summary>Sentinel representing an absent or unknown entity.</summary>
    public static readonly EntityId None = new(0);

    /// <summary>True when this id represents no entity (value zero).</summary>
    public bool IsNone   => Value == 0;
    /// <summary>True when the low 16 bits equal 640 (player entity-type marker).</summary>
    public bool IsPlayer => (Value & 0xFFFF) == 640;
    /// <summary>True when the low 16 bits equal 64 or 32832 (monster entity-type markers).</summary>
    public bool IsMonster
    {
        get
        {
            var low = Value & 0xFFFF;
            return low == 64 || low == 32832;
        }
    }
    /// <summary>Numeric uid extracted from the upper 48 bits of the full entity id.</summary>
    public int Uid => (int)(Value >> 16);
}
