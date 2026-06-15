using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaGameDataProbe
{
    // Raw-int → typed-enum mappings for each eager projection. The exact
    // int↔enum mapping is empirical — verified by STELLAR_DIAGNOSTICS=1 first-row
    // dumps and adjusted as new buckets surface in live data. Unknown ints
    // always map to the corresponding <Enum>.Unknown so plugins see a
    // deterministic fallback rather than throwing.

    /// <summary>
    /// Map <c>Bokura.SkillTableBase.SkillType</c> int → <see cref="SkillKind"/>.
    /// Spec defines five named buckets (Active/Passive/Aoyi/Whack/Sky).
    /// </summary>
    private static SkillKind MapSkillKind(int raw)
    {
        return raw switch
        {
            1 => SkillKind.Active,
            2 => SkillKind.Passive,
            3 => SkillKind.Aoyi,
            4 => SkillKind.Whack,
            5 => SkillKind.Sky,
            _ => SkillKind.Unknown,
        };
    }

    /// <summary>
    /// Map <c>Bokura.BuffTableBase.BuffType</c> int → <see cref="BuffCategory"/>.
    /// </summary>
    private static BuffCategory MapBuffCategory(int raw)
    {
        return raw switch
        {
            1 => BuffCategory.Offensive,
            2 => BuffCategory.Defensive,
            3 => BuffCategory.Support,
            4 => BuffCategory.Control,
            5 => BuffCategory.Elemental,
            _ => BuffCategory.Unknown,
        };
    }

    /// <summary>
    /// Map <c>Bokura.ItemTableBase.Type</c> int → <see cref="ItemKind"/>. Also
    /// special-cases the 5500xxx Id range as <see cref="ItemKind.Module"/> per
    /// spec §4 (modules are categorised by ID range, not by Type).
    /// </summary>
    private static ItemKind MapItemKind(int rawType, int id)
    {
        if (id >= 5_500_000 && id < 5_510_000)
        {
            return ItemKind.Module;
        }

        return rawType switch
        {
            1 => ItemKind.Consumable,
            2 => ItemKind.Equip,
            3 => ItemKind.Material,
            4 => ItemKind.Currency,
            5 => ItemKind.Quest,
            6 => ItemKind.Cosmetic,
            7 => ItemKind.Other,
            _ => ItemKind.Unknown,
        };
    }

    /// <summary>
    /// Map <c>AttrDescriptionBase.Category</c> int → <see cref="AttributeGroup"/>.
    /// </summary>
    private static AttributeGroup MapAttributeGroup(int raw)
    {
        return raw switch
        {
            1 => AttributeGroup.Offensive,
            2 => AttributeGroup.Defensive,
            3 => AttributeGroup.Support,
            4 => AttributeGroup.ElementalAttack,
            5 => AttributeGroup.ElementalBonus,
            _ => AttributeGroup.Unknown,
        };
    }

    /// <summary>
    /// Map <c>WeaponTableBase.WeaponType</c> int → <see cref="WeaponKind"/>.
    /// Empirical — unknown buckets fall through to <see cref="WeaponKind.Unknown"/>.
    /// </summary>
    private static WeaponKind MapWeaponKind(int raw)
    {
        return raw switch
        {
            1 => WeaponKind.Sword,
            2 => WeaponKind.Greatsword,
            3 => WeaponKind.Bow,
            4 => WeaponKind.Staff,
            5 => WeaponKind.Wand,
            6 => WeaponKind.Dagger,
            7 => WeaponKind.Shield,
            8 => WeaponKind.Other,
            _ => WeaponKind.Unknown,
        };
    }
}
