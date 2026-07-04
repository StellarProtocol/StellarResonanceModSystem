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
    /// Set the live run id from the just-entered scene. The combat probe routes
    /// every enter-scene's <c>AttrSceneUuid</c> through the magnitude gate: an
    /// instanced-content snowflake (dungeon / instanced world-boss / raid) is
    /// passed through as the run id; a town/home/open-world FIELD scene resolves
    /// to <c>0</c>. Passing 0 on a non-instanced scene deliberately clears the run
    /// id so the previous dungeon's id cannot linger onto a later open-world run.
    /// <para>
    /// Beginning a genuinely different run (a new non-zero id) clears the prior
    /// run's settlement. Transitioning to 0 (leaving a dungeon to town) does NOT
    /// — the upload plugin reads <c>LastSettlement</c> at archive time on that
    /// very transition, and latches its own run id at combat start, so the
    /// post-clear archive still uploads correctly under the dungeon id.
    /// </para>
    /// </summary>
    void SetCurrentRun(long sceneUuid);

    /// <summary>
    /// Record the settlement (clear/result) summary for the current run.
    /// </summary>
    void SetSettlement(int passTimeSeconds, int masterModeScore);

    /// <summary>
    /// Record the raw <c>DungeonSceneInfo.difficulty</c> value (field 1) decoded
    /// from <c>DungeonSyncData.dungeon_scene_info</c> (field 21) for the current
    /// run. Semantic UNCONFIRMED — see
    /// <see cref="Stellar.Abstractions.Services.IDungeonState.CurrentDifficulty"/>.
    /// </summary>
    void SetDifficulty(int difficulty);

    /// <summary>
    /// Record the run-timer start time (server epoch ms) for the current run.
    /// The probe feeds this from two sources in HUD priority order — PRIMARY
    /// <c>DungeonSyncData.timer_info.start_time</c> (the game's own dungeon
    /// clock derives from it, per <c>dungeon_timer_vm.lua getEndTimeStamp</c>),
    /// FALLBACK <c>flow_info.play_time</c>. Callers should only push non-zero
    /// values (zero = "run not started yet"); the implementation ignores zero
    /// writes AND is first-non-zero-wins — once latched, a later non-zero write
    /// (e.g. from the other source) cannot shift the clock. The latch clears on
    /// a new run (<see cref="SetCurrentRun"/> with a new non-zero id) or
    /// <see cref="Reset"/>. See
    /// <see cref="Stellar.Abstractions.Services.IDungeonState.RunTimerStartMs"/>.
    /// </summary>
    void SetRunTimerStart(long startMs);

    /// <summary>
    /// Clear the active run and any settlement — invoked on logout.
    /// </summary>
    void Reset();
}
