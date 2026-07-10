namespace Stellar.Wire;

/// <summary>
/// Subset of <c>zproto.EAttrType</c> values consumed by Phase 3 readers/probes.
/// Source of truth: <c>(local reference)</c>.
/// </summary>
public static class AttrTypeIds
{
    public const int AttrName            = 1;
    public const int AttrId              = 10;
    public const int AttrSummonerId      = 90;    // true caster for a summon/pet entity's owner, when TopSummonerId is absent (Stellar.Application AttrCatalog.g.cs id 90)
    public const int AttrTopSummonerId   = 91;    // preferred over AttrSummonerId — the ROOT caster for chained summons (AttrCatalog.g.cs id 91; same field family as SyncDamageInfo.TopSummonerId)
    public const int AttrPos             = 52;
    public const int AttrSkillLevelIdList = 116;  // repeated SkillLevelInfo {skill_id, current_level, remodel_level=Tier} — per-entity equipped loadout (incl. Battle Imagines)
    public const int AttrTeamId          = 194;   // BPSR-Meter e_attr_type.py — entity's team membership; key for in-AOI party identification
    public const int AttrTeamMemberNums  = 195;   // accompanying member count; informational
    public const int AttrEquipData       = 200;   // repeated EquipNine (gear) — decoded by AttrEquipDataReader
    public const int AttrFashionData     = 201;   // FashionData{ repeated FashionInfo } (worn cosmetics + dye colours) — AttrFashionDataReader
    public const int AttrProfessionId    = 220;   // entity's profession/class id
    public const int AttrSceneName       = 340;   // scene-level attr (EnterSceneInfo.SceneAttrs): scene display name (string)
    public const int AttrSceneBasicId    = 341;   // scene-level attr: scene TEMPLATE id (same across runs of one dungeon) — NOT a run id
    public const int AttrSceneUuid       = 342;   // scene-level attr: server-assigned per-INSTANCE scene uuid (int64) — the stable per-run id shared by everyone in the run
    public const int AttrSceneLevelId    = 345;   // scene-level attr: level/dungeon level id
    public const int AttrFightPoint      = 10030;  // ability/combat score ("Ability Score" in ZDPS); present per-entity in SyncNearEntities for EntChar
    public const int AttrSeasonLevel     = 10070;  // season/battle-pass level
    public const int AttrHp              = 11310;
    public const int AttrMaxHp           = 11320;
    public const int AttrLevel           = 10000;  // character level
    public const int AttrDeathCount      = 348;    // scene/World-level per-run "Defeated" counter (Z.World:GetWorldLuaAttr(AttrDeathCount); EN label from Lang("DeadCount")) — delivery path not yet traced, diagnostic-only (see PandaCombatStubProbe.Diagnostics.cs DiagDeathCountAttr)
}
