namespace Stellar.Application.Abstractions;

/// <summary>
/// Inbound sink the Infrastructure probes push into, off the network receive
/// thread (via the shared WorldNtf stub dispatcher). The run id is latched
/// DIRECTLY from the ENTER-SCENE path (<c>EnterSceneInfo.SceneAttrs →
/// AttrSceneUuid</c>, the stable per-instance scene uuid) by the combat probe —
/// but only for DUNGEON instances. The combat probe magnitude-gates the call:
/// dungeon-instance scene uuids are server snowflakes &gt; 2^53, while
/// town/home/open-world scenes carry small ids, so only the dungeon enter-scene
/// reaches <see cref="SetCurrentRun"/>. The SETTLEMENT is sourced separately
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
    /// Set the live run id to the just-entered scene's server-assigned
    /// per-instance scene uuid (<c>AttrSceneUuid</c>) decoded from the
    /// enter-scene payload. The caller (combat probe) is responsible for only
    /// invoking this for actual dungeon instances — it magnitude-gates on the
    /// uuid so the small town/home scene id the player returns to (after a clear,
    /// before the plugin uploads) never reaches here and clobbers the dungeon
    /// run id. Changing the id clears any prior run's settlement.
    /// </summary>
    void SetCurrentRun(long sceneUuid);

    /// <summary>
    /// Record the settlement (clear/result) summary for the current run.
    /// </summary>
    void SetSettlement(int passTimeSeconds, int masterModeScore);

    /// <summary>
    /// Clear the active run and any settlement — invoked on logout.
    /// </summary>
    void Reset();
}
