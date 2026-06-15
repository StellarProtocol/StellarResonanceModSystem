// src/Stellar.Abstractions/Domain/GameData/Enums.cs
namespace Stellar.Abstractions.Domain.GameData;

/// <summary>Skill classification. Projected from <c>SkillTableBase.SkillType</c> (int).</summary>
public enum SkillKind
{
    /// <summary>Skill type not recognised by this version of Stellar.</summary>
    Unknown = 0,
    /// <summary>Actively activated skill triggered by the player.</summary>
    Active,
    /// <summary>Passively applied skill with no player activation.</summary>
    Passive,
    /// <summary>BPSR-specific "secret art" (Aoyi) skill class.</summary>
    Aoyi,
    /// <summary>BPSR-specific finisher skill class.</summary>
    Whack,
    /// <summary>Air / sky-skill variant.</summary>
    Sky,
}

/// <summary>Buff classification. Projected from the buff row's category int field.</summary>
public enum BuffCategory
{
    /// <summary>Category not recognised by this version of Stellar.</summary>
    Unknown = 0,
    /// <summary>Offensive damage-enhancing buff.</summary>
    Offensive,
    /// <summary>Defensive damage-reducing buff.</summary>
    Defensive,
    /// <summary>Support / utility buff benefiting allies.</summary>
    Support,
    /// <summary>Crowd-control effect.</summary>
    Control,
    /// <summary>Elemental enhancement buff.</summary>
    Elemental,
}

/// <summary>Item category. Projected from <c>ItemTableBase.Type</c> (int).</summary>
public enum ItemKind
{
    /// <summary>Item type not recognised by this version of Stellar.</summary>
    Unknown = 0,
    /// <summary>Consumable / potion item.</summary>
    Consumable,
    /// <summary>Equipment piece (armour, accessory, etc.).</summary>
    Equip,
    /// <summary>Equipment-augment module (5500xxx id range) used by ModuleOptimizer.</summary>
    Module,
    /// <summary>Crafting / upgrade material.</summary>
    Material,
    /// <summary>In-game currency item.</summary>
    Currency,
    /// <summary>Quest-related key item.</summary>
    Quest,
    /// <summary>Cosmetic / appearance item.</summary>
    Cosmetic,
    /// <summary>Uncategorised item.</summary>
    Other,
}

/// <summary>Attribute screen group. <c>Unknown = 0</c>; the first five named members mirror the
/// in-game profile grouping (Offensive … ElementalBonus); the remaining members are catalog
/// groups (Core … Deprecated) assigned by the generated attribute catalog.</summary>
public enum AttributeGroup
{
    /// <summary>Group not recognised by this version of Stellar.</summary>
    Unknown = 0,
    /// <summary>Offensive attack-stat group.</summary>
    Offensive,
    /// <summary>Defensive mitigation-stat group.</summary>
    Defensive,
    /// <summary>Support / utility stat group.</summary>
    Support,
    /// <summary>Elemental attack damage stat group.</summary>
    ElementalAttack,
    /// <summary>Elemental damage bonus percentage stat group.</summary>
    ElementalBonus,
    /// <summary>Core combat scalars (HP / ATK / MATK / Armor / Resistance).</summary>
    Core,
    /// <summary>Primary stats (Strength / Intellect / Agility / Endurance).</summary>
    Primary,
    /// <summary>Secondary rating stats (Crit / Haste / Luck / Mastery / Versatility / Block).</summary>
    Secondary,
    /// <summary>Healing and shield throughput stats.</summary>
    Healing,
    /// <summary>Elemental resistance / damage-reduction stats.</summary>
    ElementalResist,
    /// <summary>Movement / stamina / revive utility stats.</summary>
    Utility,
    /// <summary>Progression scalars (level, ability score, season strength).</summary>
    Progression,
    /// <summary>Identity / bookkeeping attributes (name, team id, profession id, equip blob…).</summary>
    Identity,
    /// <summary>Deprecated attribute ids the game no longer uses — never display.</summary>
    Deprecated,
}

/// <summary>Weapon classification. Projected from the weapon row's weapon-class int.</summary>
public enum WeaponKind
{
    /// <summary>Weapon type not recognised by this version of Stellar.</summary>
    Unknown = 0,
    /// <summary>One-handed sword.</summary>
    Sword,
    /// <summary>Two-handed greatsword.</summary>
    Greatsword,
    /// <summary>Bow / ranged weapon.</summary>
    Bow,
    /// <summary>Staff weapon.</summary>
    Staff,
    /// <summary>Wand weapon.</summary>
    Wand,
    /// <summary>Dagger weapon.</summary>
    Dagger,
    /// <summary>Shield (off-hand).</summary>
    Shield,
    /// <summary>Unclassified weapon type.</summary>
    Other,
}
