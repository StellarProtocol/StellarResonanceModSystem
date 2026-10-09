using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>One equipped item from social data: which item-config id occupies which slot
/// (no instance rolls — social data carries slot+id only).</summary>
/// <param name="Slot">Equipment slot code (wire <c>EquipNine.slot</c>).</param>
/// <param name="EquipId">Item-config id occupying the slot (resolves name/quality/icon via the item table).</param>
public readonly record struct GearSlotRef(int Slot, int EquipId);

/// <summary>Affiliation/prestige extras from a full-mask social reply (the ID-card fetch requests
/// mask 0 = all sections). Thin-mask replies (nameplate/avatar queries carry identity only) leave
/// these at defaults — values are best-effort, last-reply-wins via the cache.</summary>
/// <param name="Guild">Guild name from <c>union_data.name</c>; empty when guildless or section absent.</param>
/// <param name="PartySize">Member count of the player's party from <c>team_data.team_num</c>; 0 when solo or absent.</param>
/// <param name="MasterScore">Master-mode season score from <c>master_mode_dungeon_data.season_score</c>;
/// 0 when absent or player-hidden (the wire's <c>is_show</c> flag is inverted — truthy renders "Hidden"
/// on the game's own card, and we follow the native privacy behaviour).</param>
/// <param name="TitleId">Equipped title id from <c>personal_zone.title_id</c>; 0 when none. Resolving the
/// display name requires the dungeon-title game table (deferred).</param>
/// <param name="FashionCollect">Fashion collection-point count from <c>personal_zone.fashion_collect_point</c>;
/// 0 when absent. Candidate source for the ID-card "collection points" badge (unconfirmed).</param>
/// <param name="RideCollect">Ride collection-point count from <c>personal_zone.ride_collect_point</c>; 0 when absent.</param>
/// <param name="WeaponSkinCollect">Weapon-skin collection-point count from
/// <c>personal_zone.weapon_skin_collect_point</c>; 0 when absent.</param>
public readonly record struct SocialIdentity(
    string Guild, int PartySize, int MasterScore, int TitleId,
    int FashionCollect = 0, int RideCollect = 0, int WeaponSkinCollect = 0)
{
    /// <summary>Empty identity — no guild/party/score/title/collect-point data parsed.</summary>
    public static SocialIdentity None => new(string.Empty, 0, 0, 0);
}

/// <summary>
/// A player's on-demand social-data reply (the game's own <c>Social.GetSocialData</c> RPC, which the
/// inspector triggers via the portrait path). Available for ANY player regardless of AOI proximity —
/// the fallback source the inspector uses when the proximity broadcast is absent. Carries identity,
/// ability score (fight point), profession, gear-by-slot, worn cosmetics and affiliation extras; NOT
/// the secondary-stat breakdown or skills (those are AOI-broadcast only — see the design spec §2).
/// </summary>
/// <param name="CharId">The player's character id (wire <c>SocialData.char_id</c>).</param>
/// <param name="Name">Display name from <c>basic_data</c>; empty if absent.</param>
/// <param name="Level">Character level from <c>basic_data</c>.</param>
/// <param name="FightPoint">Ability score (int64) from <c>user_attr_data.fight_point</c>.</param>
/// <param name="ProfessionId">Profession id from <c>profession_data</c>.</param>
/// <param name="Gear">Equipped items by slot from <c>equip_data.equip_infos</c>; never null.</param>
/// <param name="Fashion">Worn cosmetics from <c>fashion_data</c>; never null.</param>
/// <param name="Identity">Guild/party/master-score/title extras; <see cref="SocialIdentity.None"/> when
/// the reply's mask excluded those sections.</param>
/// <param name="ProfileUrl">HTTPS URL of the player's square profile picture on the game's CDN
/// (<c>avatar_info.profile.url</c>); empty when the player has none or the section was absent.</param>
/// <param name="HalfBodyUrl">HTTPS URL of the player's half-body ID-card picture on the game's CDN
/// (<c>avatar_info.half_body.url</c>); empty when the player has none or the section was absent.</param>
public sealed record SocialSnapshot(
    long CharId,
    string Name,
    int Level,
    long FightPoint,
    int ProfessionId,
    IReadOnlyList<GearSlotRef> Gear,
    IReadOnlyList<FashionEntry> Fashion,
    SocialIdentity Identity,
    string ProfileUrl = "",
    string HalfBodyUrl = "")
{
    /// <summary>The player's scene location from <c>SocialData.scene_data</c> (field 10); null when the reply
    /// omitted that section entirely. A non-null value may still be all-zero — the server can send the section
    /// but blank its contents for privacy, and that present-vs-absent distinction is deliberately preserved.
    /// Only FULL-mask replies (the ID-card fetch, mask 0) carry the section; the framework cache therefore keeps
    /// the last known location when a later thin (nameplate/avatar) reply for the same player arrives without
    /// one — check <see cref="SocialLocation.ReceivedAtMs"/> for its age.
    /// Init-only (not a positional parameter) so existing compiled callers of the constructor stay binary-compatible.</summary>
    public SocialLocation? Location { get; init; }
}

/// <summary>A player's scene location as carried by <c>SocialData.scene_data</c> (<c>zproto.SceneData</c>).
/// Every member defaults to 0 when its wire field is absent.
/// <para>In-game (release_3.7) the <c>level_*</c> fields were the LIVE ones — <c>level_map_id</c>/<c>level_pos</c>
/// tracked the player while <c>map_id</c>/<c>pos</c> stayed stale (apparently the last world/return position;
/// a player inside a dungeon reported <c>map_id</c> = the town and <c>level_map_id</c> = the dungeon). Prefer the
/// <c>Current*</c> members, which pick the level fields when set and fall back to the map fields.</para></summary>
/// <param name="MapId">Scene/map id from <c>scene_data.map_id</c> (observed stale — see remarks).</param>
/// <param name="SceneLayer">Scene layer from <c>scene_data.scene_layer</c>.</param>
/// <param name="LineId">Server line (channel shard) from <c>scene_data.line_id</c> — the in-game "Line N".</param>
/// <param name="Pos">World-space position from <c>scene_data.pos</c> (x/y/z; observed stale — see remarks).</param>
/// <param name="Dir">Facing from <c>scene_data.pos.dir</c> (observed always 0 — no facing for far players).</param>
/// <param name="SceneAreaId">Scene area id from <c>scene_data.scene_area_id</c> (pairs with <paramref name="MapId"/>).</param>
/// <param name="LevelMapId">Level (instance) map id from <c>scene_data.level_map_id</c>; 0 outside a level.</param>
/// <param name="LevelPos">Position inside the level from <c>scene_data.level_pos</c> (x/y/z).</param>
/// <param name="LevelAreaId">Area id inside the level from <c>scene_data.level_area_id</c> (field 19; pairs with
/// <paramref name="LevelMapId"/>).</param>
/// <param name="ReceivedAtMs">Framework server clock (<see cref="ICombatSnapshot.ServerNowMs"/> domain, Unix ms)
/// when the reply carrying this location arrived; 0 when it arrived before the first server-time sync.
/// Stamped by the framework cache, not the wire.</param>
public readonly record struct SocialLocation(
    int MapId, int SceneLayer, int LineId, Position3D Pos, float Dir,
    int SceneAreaId, int LevelMapId, Position3D LevelPos,
    int LevelAreaId = 0, long ReceivedAtMs = 0)
{
    /// <summary>The scene the player is in now: <see cref="LevelMapId"/> when set, else <see cref="MapId"/>.</summary>
    public int CurrentSceneId => LevelMapId != 0 ? LevelMapId : MapId;

    /// <summary>The player's position in <see cref="CurrentSceneId"/>: <see cref="LevelPos"/> when in a level, else <see cref="Pos"/>.</summary>
    public Position3D CurrentPos => LevelMapId != 0 ? LevelPos : Pos;

    /// <summary>The area id in <see cref="CurrentSceneId"/>: <see cref="LevelAreaId"/> when in a level, else <see cref="SceneAreaId"/>.</summary>
    public int CurrentAreaId => LevelMapId != 0 ? LevelAreaId : SceneAreaId;
}
