using Stellar.Abstractions.Diagnostics;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Opt-in diagnostics for <see cref="PandaDungeonProbe"/>, gated behind
/// <c>STELLAR_DIAGNOSTICS=1</c> so the steady-state dispatch path pays zero log
/// cost. The first detected dungeon-sync is ALWAYS logged once (regardless of
/// the toggle) as a boot sanity check confirming the direct method-id
/// registration (23) is receiving and structurally matching packets.
/// Subsequent detections only log when diagnostics is on.
/// </summary>
internal sealed partial class PandaDungeonProbe
{
    private bool _firstDetectionLogged;
    private bool _firstDifficultyLogged;
    private bool _firstTimerLogged;

    /// <summary>
    /// One-shot, always-on: log the raw <c>DungeonSceneInfo.difficulty</c> value
    /// the first time field 21 is seen. The semantic (1-20 challenge level vs. a
    /// small tier enum) is UNCONFIRMED — this line is what the user's next
    /// Master-N run is used to confirm against.
    /// </summary>
    private void DiagDungeonDifficulty(uint methodId, DungeonSyncResult result)
    {
        if (!result.HasDungeonSceneInfo || _firstDifficultyLogged) return;

        _firstDifficultyLogged = true;
        _log.Info(
            $"[Dungeon] dungeon difficulty raw={result.DungeonDifficulty} scene={result.SceneUuid} " +
            $"method={methodId} (semantic unconfirmed: 1-20 level vs. tier enum)");
    }

    /// <summary>
    /// One-shot, always-on: log the raw <c>DungeonTimerInfo</c> fields the first
    /// time field 15 is seen. The semantic of <c>start_time</c> (assumed epoch
    /// SECONDS, converted to ms) is UNCONFIRMED — this line is what the user's
    /// next real dungeon run is used to confirm against.
    /// </summary>
    private void DiagDungeonTimer(uint methodId, DungeonSyncResult result)
    {
        if (!result.HasTimerInfo || _firstTimerLogged) return;

        _firstTimerLogged = true;
        _log.Info(
            $"[Dungeon] dungeon timer_info raw type={result.TimerType} " +
            $"dungeon_times={result.TimerDungeonTimes} direction={result.TimerDirection} " +
            $"pause_time={result.TimerPauseTime} pause_total_time={result.TimerPauseTotalTime} " +
            $"cur_pause_timestamp={result.TimerCurPauseTimestamp} -> RunTimerStartMs={result.RunTimerStartMs} " +
            $"scene={result.SceneUuid} method={methodId} (semantic unconfirmed: start_time assumed epoch seconds)");
    }

    private void DiagDungeonSync(uint methodId, DungeonSyncResult result)
    {
        // One-shot, always-on: confirm the registered method id is delivering
        // structurally-valid SyncDungeonData payloads.
        if (!_firstDetectionLogged)
        {
            _firstDetectionLogged = true;
            _log.Info(
                $"[Dungeon] first SyncDungeonData detected on WorldNtf method={methodId} " +
                $"scene_uuid={result.SceneUuid} settlement={result.HasSettlement} " +
                $"pass_time={result.PassTimeSeconds}s mastermode_score={result.MasterModeScore}");
            return;
        }

        if (!StellarDiagnostics.IsEnabled) return;

        _log.Info(
            $"[Dungeon] SyncDungeonData method={methodId} scene_uuid={result.SceneUuid} " +
            $"settlement={result.HasSettlement} pass_time={result.PassTimeSeconds}s " +
            $"mastermode_score={result.MasterModeScore}");
    }
}
