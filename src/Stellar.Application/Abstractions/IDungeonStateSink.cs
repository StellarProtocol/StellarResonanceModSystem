namespace Stellar.Application.Abstractions;

/// <summary>
/// Inbound sink the Infrastructure dungeon probe pushes into. The probe sources
/// <c>WorldNtf.SyncDungeonData</c> off the network receive thread (via the
/// shared WorldNtf stub dispatcher) and forwards the decoded run id / settlement
/// here; the implementing <c>DungeonStateService</c> publishes a lock-free
/// snapshot read by plugins through <c>IDungeonState</c>.
///
/// <para>
/// This is the write side of the dungeon-state split (mirrors the
/// combat/party service ↔ event-sink shape): Application declares the contract,
/// Infrastructure calls it, and the read side lives on the Abstractions
/// <c>IDungeonState</c> interface.
/// </para>
/// </summary>
internal interface IDungeonStateSink
{
    /// <summary>
    /// Set the current run's unique id (<c>scene_uuid</c>). Called whenever a
    /// dungeon-sync packet is recognised. Passing 0 (e.g. on leave-scene /
    /// logout) clears the active run.
    /// </summary>
    void SetCurrentRun(long sceneUuid);

    /// <summary>
    /// Record the settlement (clear/result) summary for the current run.
    /// </summary>
    void SetSettlement(int passTimeSeconds, int masterModeScore);

    /// <summary>
    /// Clear the active run and any settlement — invoked on leave-scene / logout.
    /// </summary>
    void Reset();
}
