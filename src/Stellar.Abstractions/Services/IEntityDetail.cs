using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>One equipped item resolved for display: which item id occupies which slot.</summary>
public readonly record struct EquippedItem(int Slot, int ItemId);

/// <summary>
/// Per-entity detail captured from the combat wire's <c>SyncNearEntities</c> attribute stream — the
/// full broadcast attribute map and equipment for any in-AOI entity. Identity/skills come from
/// <see cref="ICombatLookup"/>; derived self-only stats from <see cref="IPlayerStats"/>.
/// </summary>
public interface IEntityDetail
{
    /// <summary>The entity's full broadcast numeric attribute map (attr id → value); empty if unknown.</summary>
    IReadOnlyDictionary<int, long> GetAttributes(EntityId entity);

    /// <summary>The entity's equipped items (slot + item id); empty if not broadcast.</summary>
    IReadOnlyList<EquippedItem> GetEquipment(EntityId entity);

    /// <summary>Worn cosmetics for an entity from the broadcast <c>AttrFashionData</c> attribute,
    /// with actual dye colours. Empty for entities that never reported fashion / out of AOI.</summary>
    IReadOnlyList<FashionEntry> GetFashion(EntityId entity);

    /// <summary>The player's last on-demand social-data reply (identity + ability score + profession +
    /// gear + wardrobe), or null if none received. Available for any player; the inspector's fallback
    /// when the AOI broadcast is absent (far/never-seen players). See <see cref="SocialSnapshot"/>.</summary>
    SocialSnapshot? GetSocialSnapshot(EntityId entity);
}
