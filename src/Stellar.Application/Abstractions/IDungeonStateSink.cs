namespace Stellar.Application.Abstractions;

/// <summary>
/// Inbound sink the Infrastructure probes push into, off the network receive
/// thread (via the shared WorldNtf stub dispatcher). The run id is sourced from
/// the ENTER-SCENE path (<c>EnterSceneInfo.SceneAttrs → AttrSceneUuid</c>, the
/// stable per-instance scene uuid) by the combat probe; the SETTLEMENT is sourced
/// from <c>WorldNtf.SyncDungeonData</c> by the dungeon probe. The implementing
/// <c>DungeonStateService</c> publishes a lock-free snapshot read by plugins
/// through <c>IDungeonState</c>.
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
    /// Set the current run's unique id — the server-assigned per-instance scene
    /// uuid (<c>AttrSceneUuid</c>) decoded from the enter-scene payload. Stable
    /// across the whole run and shared by every client in it. Passing 0 (e.g. on
    /// leave-scene / logout) clears the active run.
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
