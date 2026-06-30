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
    public const uint SyncContainerData    = 21; // full inventory sync (CharSerialize)
    public const uint SyncContainerDirtyData = 22; // incremental container update
    public const uint SyncDungeonData      = 23; // DungeonSyncData (scene_uuid + settlement). Confirmed: lua/zservice/world_ntf_gen.lua OnCallStub GetMethodId()==23
    public const uint SyncServerTime       = 43;
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
