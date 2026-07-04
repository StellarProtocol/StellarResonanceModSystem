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
    private bool _firstFlowLogged;
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
    /// One-shot, always-on: log the FULL <c>DungeonFlowInfo</c> the first time a
    /// delivery carries a NON-ZERO <c>play_time</c>. Zero-valued deliveries (hub
    /// scene / pre-start states) do NOT consume the one-shot — the earlier
    /// timer_info diagnostic burned itself on exactly such a delivery, which is
    /// what falsified it. play_time's epoch-SECONDS assumption is what the next
    /// real run confirms against this line.
    /// </summary>
    private void DiagDungeonFlow(uint methodId, DungeonSyncResult result)
    {
        if (!result.HasFlowInfo || result.FlowInfo.PlayTime == 0 || _firstFlowLogged) return;

        _firstFlowLogged = true;
        var f = result.FlowInfo;
        _log.Info(
            $"[Dungeon] dungeon flow_info raw state={f.State} active_time={f.ActiveTime} " +
            $"ready_time={f.ReadyTime} play_time={f.PlayTime} end_time={f.EndTime} " +
            $"settlement_time={f.SettlementTime} dungeon_times={f.DungeonTimes} result={f.Result} " +
            $"-> RunTimerStartMs={f.PlayTimeMs} scene={result.SceneUuid} method={methodId} " +
            $"(semantic unconfirmed: play_time assumed epoch seconds)");
    }

    /// <summary>
    /// One-shot, always-on: log the raw <c>DungeonTimerInfo</c> fields the first
    /// time field 15 arrives with a NON-ZERO <c>start_time</c> (zero deliveries
    /// no longer consume the one-shot — the live falsification showed the first
    /// delivery is an all-zero hub packet). Retained for diagnostics only;
    /// <c>flow_info.play_time</c> now drives <c>RunTimerStartMs</c>.
    /// </summary>
    private void DiagDungeonTimer(uint methodId, DungeonSyncResult result)
    {
        if (!result.HasTimerInfo || result.RunTimerStartMs == 0 || _firstTimerLogged) return;

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
