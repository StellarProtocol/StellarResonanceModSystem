using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.PluginContracts;

/// <summary>
/// A frozen, session-time snapshot of one entity's inspectable data — IDs only, exactly the shape the live
/// <see cref="IEntityDetail"/>/<c>ICombatLookup</c> services return, so a viewer can swap its data source from
/// live to frozen with no other change. Names / icons / quality / gear-score re-resolve LIVE from the static
/// tables at render time (this never freezes a display string). Passed across the plugin boundary via
/// <see cref="IFrozenEntityViewer"/> + <see cref="IPluginExchange"/>.
/// </summary>
/// <param name="Id">The snapshotted entity id.</param>
/// <param name="SessionLabel">Human-readable label of the session this snapshot came from (e.g. "9:02 PM · Asteria Plains").</param>
/// <param name="Name">The entity's display name captured at archive time.</param>
/// <param name="FightPoint">Gear/fight score at capture.</param>
/// <param name="Hp">HP at capture (0 if unknown).</param>
/// <param name="MaxHp">Max HP at capture (0 if unknown).</param>
/// <param name="TeamId">Team id at capture.</param>
/// <param name="Attributes">Non-zero broadcast attributes (attrId → value).</param>
/// <param name="Gear">Equipped gear (slot → item id).</param>
/// <param name="Skills">Equipped skill loadout (skill id + level + tier).</param>
/// <param name="Fashion">Worn cosmetics (slot + fashion id + dyes).</param>
public sealed record FrozenEntity(
    EntityId Id,
    string SessionLabel,
    string Name,
    long FightPoint,
    long Hp,
    long MaxHp,
    long TeamId,
    IReadOnlyDictionary<int, long> Attributes,
    IReadOnlyList<EquippedItem> Gear,
    IReadOnlyList<SkillLevel> Skills,
    IReadOnlyList<FashionEntry> Fashion);
