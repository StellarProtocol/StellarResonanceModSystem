using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Read-only view of the current dungeon run, decoded from the game's
/// <c>WorldNtf.SyncDungeonData</c> notification. Surfaces the per-run unique id
/// (<see cref="CurrentRunId"/>) that an upload/logging plugin needs to key a run
/// (the StellarLogs <c>level_uuid</c>), plus the clear-time / score once the run
/// settles (<see cref="LastSettlement"/>).
///
/// <para>
/// Populated on the network receive thread and read on the main thread;
/// implementations publish via volatile reads so consumers are lock-free.
/// <see cref="CurrentRunId"/> resets to 0 on leave-scene / logout.
/// </para>
/// </summary>
public interface IDungeonState
{
    /// <summary>
    /// The unique id of the dungeon run currently in progress
    /// (<c>DungeonSyncData.scene_uuid</c>). 0 when not in a dungeon / between
    /// runs. Use this as the run key (e.g. <c>level_uuid</c> for log uploads).
    /// </summary>
    long CurrentRunId { get; }

    /// <summary>
    /// The most recent settlement (clear/result) summary, or <see langword="null"/>
    /// if the current run has not reached its result screen yet. Carries the
    /// clear time and master-mode score.
    /// </summary>
    DungeonSettlementInfo? LastSettlement { get; }
}
