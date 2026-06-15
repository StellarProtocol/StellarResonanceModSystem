// src/Stellar.Abstractions/Domain/GameData/AttributeProfileInfo.cs
namespace Stellar.Abstractions.Domain.GameData;

/// <summary>
/// UI panel classification for an attribute, sourced from
/// <c>Bokura.ProfileAttrTableBase</c>. <see cref="Type"/> is the raw classifier
/// int (1=Offensive, 2=Defensive, 3=Support, 4=ElementalAttack, 5=ElementalBonus
/// per observed game data — verify in iteration 1);
/// <see cref="TypeDisplayName"/> is the localized group label.
/// </summary>
public readonly record struct AttributeProfileInfo(
    int AttrId,
    string Name,
    int Type,
    string TypeDisplayName);
