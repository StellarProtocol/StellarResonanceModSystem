using Stellar.Abstractions.Domain.GameData;

namespace Stellar.Abstractions.Services;

/// <summary>Static-data lookups for combat-related rows.</summary>
public interface IGameDataCombat
{
    /// <summary>Returns the skill row for <paramref name="id"/>, or null if unknown.</summary>
    SkillInfo? GetSkill(int id);

    /// <summary>Returns the buff/debuff row for <paramref name="id"/>, or null if unknown.</summary>
    BuffInfo? GetBuff(int id);

    /// <summary>Returns the profession row for <paramref name="id"/>, or null if unknown.</summary>
    ProfessionInfo? GetProfession(int id);

    /// <summary>Returns the talent row for <paramref name="id"/>, or null if unknown.</summary>
    TalentInfo? GetTalent(int id);

    /// <summary>Returns the attribute row for <paramref name="id"/>, or null if unknown. Sparse live-table
    /// rows are backfilled from the built-in <c>EAttrType</c> catalog: the localized live values win where
    /// present; <see cref="Stellar.Abstractions.Domain.GameData.AttributeInfo.EnumName"/> is always
    /// catalog-supplied.</summary>
    AttributeInfo? GetAttribute(int id);

    /// <summary>Returns the attribute-profile (UI panel classification) row for <paramref name="id"/>, or null if unknown.</summary>
    AttributeProfileInfo? GetAttributeProfile(int id);

    /// <summary>Returns the damage-attribute row for <paramref name="id"/>, or null if unknown.</summary>
    DamageAttrInfo? GetDamageAttr(int id);
}
