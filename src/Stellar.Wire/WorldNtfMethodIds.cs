namespace Stellar.Wire;

/// <summary>
/// Method IDs for the <c>WorldNtf</c> service (UUID 1664308034). Sourced
/// from <c>(local reference)</c>.
/// Only the IDs Phase 3 consumes are listed here; add more as needed.
/// </summary>
public static class WorldNtfMethodIds
{
    public const uint EnterScene           = 3;  // EnterSceneInfo.PlayerEnt.Attrs = self's full attr set (incl. skill loadout 116)
    public const uint Teleport             = 5;
    public const uint SyncNearEntities     = 6;
    // SyncSceneAttrs{ AttrCollection attrs = 1 } — the SCENE/World attr sync: the carrier that keeps
    // Panda.ZGame.ZWorld's world attr collection fresh (ZWorld.ParseAttrProto(AttrCollection) fills the
    // very collection Z.World:GetWorldLuaAttr(id) reads). Same attr band the framework already consumes
    // off EnterSceneInfo.SceneAttrs (340 SceneName / 341 SceneBasicId / 342 SceneUuid / 345 SceneLevelId);
    // AttrDeathCount(348) — the settlement "Defeated" counter — rides here too.
    // Id confirmed two independent ways: (a) reconstructing the WorldNtf method table by merging the
    // Cpp2IL Zproto.WorldNtf.Types declaration order with the lua id map in
    // StarResonanceData/lua/zservice/world_ntf_gen.lua — the merge yields 46 consecutive ids and every
    // known anchor (3/5/6/21/22/23/24/43/45/46) lands exactly right; (b) the independent third-party
    // enum data/BPSR-ZDPS/BPSR-ZDPSLib/ServiceMethods/WorldNtf.cs — `SyncSceneAttrs = 0x7`.
    public const uint SyncSceneAttrs       = 7;
    public const uint SyncContainerData    = 21; // full inventory sync (CharSerialize)
    public const uint SyncContainerDirtyData = 22; // incremental container update
    public const uint SyncDungeonData      = 23; // DungeonSyncData (scene_uuid + settlement). Confirmed: lua/zservice/world_ntf_gen.lua OnCallStub GetMethodId()==23
    public const uint SyncDungeonDirtyData = 24; // dungeon container dirty-DELTA: SyncDungeonDirtyData{ BufferStream v_data=1 { bytes buffer=1 } }; the blob is the game's int32-framed container-merge format consumed by ContainerMgr.DungeonSyncData:MergeData (lua/zcontainer/dungeon_sync_data.lua) via DungeonSyncService.OnSync (lua/sync/dungeon_sync.lua). C#-routed (Zservice.WorldNtfStub publisher — absent from world_ntf_gen.lua). Id confirmed via OnCallStub census 2026-07-05: the live payload observed at WorldNtfStub.OnCallStub method 24 hex-matched the expected shape (0ada05 0ad705 feffffff efbeadde…) — protobuf v_data(1)->buffer(1) followed by the int32-LE container framing DungeonDirtyDataReader parses.
    public const uint SyncServerTime       = 43;
    public const uint NotifyStartPlayingDungeon = 55; // NotifyStartPlayingDungeon{ StartPlayingDungeonParam v_param=1 { int64 char_id=1; bool is_use_key=2 } } — Lua-routed (world_ntf_gen.lua GetMethodId()==55); its ARRIVAL is the play-start edge
    public const uint SyncNearDeltaInfo    = 45;
    public const uint SyncToMeDeltaInfo    = 46;
    public const uint NotifyAllMemberReady = 70; // ready-check open/close: NotifyAllMemberReady{ bool v_open_or_close=1 }
    public const uint NotifyCaptainReady   = 71; // per-member response: NotifyCaptainReady{ string v_member_name=1; int64 v_char_id=2; DungeonReadyInfo v_ready_info=3 (is_ready=1) }
}

/// <summary>BPSR service UUIDs (low subset).</summary>
public static class BPSRServiceIds
{
    public const ulong WorldNtf    = 1664308034UL;
    public const ulong ChitChatNtf = 164931432UL;
    public const ulong ChitChat    = 1321197368UL;
    public const ulong GrpcTeamNtf = 966773353UL;
}
