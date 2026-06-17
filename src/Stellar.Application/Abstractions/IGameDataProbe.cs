using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound interface — Application asks Infrastructure to load Panda.Table
/// rows on demand. Implemented in Stellar.Infrastructure by walking the live
/// hot-update assemblies and resolving MLString via LocalizationMgr.
/// </summary>
internal interface IGameDataProbe
{
    /// <summary>
    /// Synchronously loads the eager batch (Skill, Buff, Profession+NameLanguage,
    /// Attribute, Item). Returns false on full failure (e.g. LocalizationMgr
    /// missing); the per-table caches inside the snapshot may individually be
    /// empty on partial failure.
    /// </summary>
    bool TryLoadEager(out GameDataEagerSnapshot snapshot);

    /// <summary>
    /// Loads one deferred table. Returns false if the table failed (type missing,
    /// reflection threw, etc.) — caller skips this table and continues with the
    /// next on the queue.
    /// </summary>
    bool TryLoadOne(GameDataTableKind kind, out object cache);
}

/// <summary>
/// Snapshot returned by <see cref="IGameDataProbe.TryLoadEager"/>. Each dictionary
/// is keyed by the row's Id. Any of these may be empty if the corresponding table
/// type was not found.
/// </summary>
internal readonly struct GameDataEagerSnapshot
{
    public IReadOnlyDictionary<int, SkillInfo>             Skills            { get; init; }

    /// <summary>
    /// Leveled-skill-id → base-skill-id map, built from
    /// <c>Bokura.SkillFightLevelTableBase</c> (row key → its <c>SkillId</c> column).
    /// Damage events sometimes carry a leveled <c>skill_level_id</c>
    /// (<c>baseSkillId*100 + level</c>, e.g. 2031104) that is not a key in
    /// <see cref="Skills"/>; this map resolves it to the base skill the SkillTable
    /// keys on. May be empty if the table type was not found.
    /// </summary>
    public IReadOnlyDictionary<int, int>                   SkillLevelToBase  { get; init; }

    public IReadOnlyDictionary<int, BuffInfo>              Buffs             { get; init; }
    public IReadOnlyDictionary<int, ProfessionInfo>        Professions       { get; init; }
    public IReadOnlyDictionary<int, AttributeInfo>         Attributes        { get; init; }
    public IReadOnlyDictionary<int, AttributeProfileInfo>  AttributeProfiles { get; init; }
    public IReadOnlyDictionary<int, ItemInfo>              Items             { get; init; }
}

/// <summary>
/// Discriminator for the deferred-load queue. Application enqueues kinds in
/// the order listed; Infrastructure receives one kind at a time via TryLoadOne.
/// </summary>
internal enum GameDataTableKind
{
    Skill,
    Buff,
    Profession,
    Talent,
    Attribute,
    DamageAttr,
    Item,
    Equip,          // EquipEnchantItemTableBase (enchant consumables) — distinct from EquipRow (EquipTableBase, gear pieces)
    Weapon,
    Monster,
    Npc,
    Scene,
    Map,
    Quest,
    Dungeon,
    Activity,
    Achievement,
    Title,
    Award,
    EquipRow,
    EquipAttrLib,
    EquipSchoolAttrLib,   // EquipAttrSchoolLibTableBase — v2 spec/school advanced rolls (talent-school-filtered)
    EquipEnchantItem,     // EquipEnchantItemTableBase — socketed-gem rows (by type id + level)
    EquipPart,
}
