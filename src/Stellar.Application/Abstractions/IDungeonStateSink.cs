namespace Stellar.Application.Abstractions;

/// <summary>
/// Inbound sink the Infrastructure probes push into, off the network receive
/// thread (via the shared WorldNtf stub dispatcher). The run id is latched from
/// the ENTER-SCENE path (<c>EnterSceneInfo.SceneAttrs → AttrSceneUuid</c>, the
/// stable per-instance scene uuid) by the combat probe — but only PENDING until a
/// <c>WorldNtf.SyncDungeonData</c> packet (which flows only while inside a dungeon)
/// CONFIRMS it as a real dungeon run. The SETTLEMENT is also sourced from
/// <c>WorldNtf.SyncDungeonData</c> by the dungeon probe. The implementing
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
    /// Record the PENDING scene id for the just-entered scene — the
    /// server-assigned per-instance scene uuid (<c>AttrSceneUuid</c>) decoded from
    /// the enter-scene payload. Every enter-scene (dungeon OR town) updates the
    /// pending id; it does NOT touch <c>CurrentRunId</c>. The pending id is only
    /// promoted to the live run id by <see cref="ConfirmDungeonRun"/> when a
    /// dungeon-only SyncDungeonData packet proves the scene is a dungeon. This
    /// prevents the town scene the player returns to (after a clear, before the
    /// plugin uploads) from clobbering the dungeon run id.
    /// </summary>
    void SetPendingScene(long sceneUuid);

    /// <summary>
    /// Promote the latched pending scene id to <c>CurrentRunId</c>. Invoked from
    /// the dungeon probe on a <c>WorldNtf.SyncDungeonData</c> packet, which flows
    /// ONLY while inside a dungeon — so at this moment the pending id is the
    /// dungeon's enter-scene uuid. Idempotent (re-confirming the same pending id is
    /// a no-op write of the same value) and a no-op when pending is 0/uninitialised.
    /// </summary>
    void ConfirmDungeonRun();

    /// <summary>
    /// Record the settlement (clear/result) summary for the current run.
    /// </summary>
    void SetSettlement(int passTimeSeconds, int masterModeScore);

    /// <summary>
    /// Clear the active run and any settlement — invoked on leave-scene / logout.
    /// </summary>
    void Reset();
}
