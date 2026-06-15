namespace Stellar.Wire;

/// <summary>
/// Subset of <c>zproto.EAttrType</c> values consumed by Phase 3 readers/probes.
/// Source of truth: <c>(local reference)</c>.
/// </summary>
public static class AttrTypeIds
{
    public const int AttrName            = 1;
    public const int AttrId              = 10;
    public const int AttrPos             = 52;
    public const int AttrSkillLevelIdList = 116;  // repeated SkillLevelInfo {skill_id, current_level, remodel_level=Tier} — per-entity equipped loadout (incl. Battle Imagines)
    public const int AttrTeamId          = 194;   // BPSR-Meter e_attr_type.py — entity's team membership; key for in-AOI party identification
    public const int AttrTeamMemberNums  = 195;   // accompanying member count; informational
    public const int AttrEquipData       = 200;   // repeated EquipNine (gear) — decoded by AttrEquipDataReader
    public const int AttrFashionData     = 201;   // FashionData{ repeated FashionInfo } (worn cosmetics + dye colours) — AttrFashionDataReader
    public const int AttrProfessionId    = 220;   // entity's profession/class id
    public const int AttrFightPoint      = 10030;  // ability/combat score ("Ability Score" in ZDPS); present per-entity in SyncNearEntities for EntChar
    public const int AttrSeasonLevel     = 10070;  // season/battle-pass level
    public const int AttrHp              = 11310;
    public const int AttrMaxHp           = 11320;
    public const int AttrLevel           = 10000;  // character level
}
