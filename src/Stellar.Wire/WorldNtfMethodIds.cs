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
    public const uint SyncServerTime       = 43;
    public const uint SyncNearDeltaInfo    = 45;
    public const uint SyncToMeDeltaInfo    = 46;
}

/// <summary>BPSR service UUIDs (low subset).</summary>
public static class BPSRServiceIds
{
    public const ulong WorldNtf    = 1664308034UL;
    public const ulong ChitChatNtf = 164931432UL;
    public const ulong ChitChat    = 1321197368UL;
    public const ulong GrpcTeamNtf = 966773353UL;
}
