using Stellar.Abstractions.Diagnostics;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Diagnostics for <see cref="PandaDungeonProbe"/>. The per-event repeat log is
/// gated behind <c>STELLAR_DIAGNOSTICS=1</c> so the steady-state dispatch path
/// pays zero log cost. ALWAYS-ON (toggle-independent): the one-shot first-seen
/// lines (detection / difficulty / flow / timer), and the session-capped
/// broad-delivery log (<see cref="DiagDungeonDelivery"/>, max
/// 2×<see cref="BroadDeliveryCap"/> lines) used to locate which stub path the
/// LIVE run's dungeon clock arrives on.
/// </summary>
internal sealed partial class PandaDungeonProbe
{
    // Broad-delivery throttles: two independent budgets so a login-time bulk
    // sync of PAST dungeon states (settlement=True pass_time=0 lines observed
    // live) cannot starve the interesting lines out of the session.
    private const int BroadDeliveryCap = 20;
    private int _broadAnyLogged;
    private int _broadNonZeroLogged;

    private bool _firstDetectionLogged;
    private bool _firstDifficultyLogged;
    private bool _firstFlowLogged;
    private bool _firstTimerLogged;

    /// <summary>
    /// Broad, ALWAYS-ON (not gated on <c>StellarDiagnostics</c>) delivery log
    /// used to locate where the LIVE run's dungeon clock arrives. Two throttled
    /// budgets per session, <see cref="BroadDeliveryCap"/> lines each:
    /// <list type="bullet">
    /// <item>"any": every structurally-valid SyncDungeonData delivery (either
    /// stub path) — the second live falsification showed we cannot trust
    /// scene_uuid matching (dirty-mask field-1 reads shift within one run), so
    /// every delivery is a candidate;</item>
    /// <item>"non-zero": deliveries carrying ANY non-zero flow_info or
    /// timer_info field, or a scene_uuid equal to the latched run id — the
    /// money lines, budgeted separately so login bulk syncs can't consume
    /// them.</item>
    /// </list>
    /// One in-game session (enter dungeon, idle 20s, archive, leave) is enough
    /// to pinpoint the source: src=cs vs src=lua, plus the full field dump.
    /// </summary>
    private void DiagDungeonDelivery(string source, uint methodId, DungeonSyncResult result)
    {
        bool nonZero = HasNonZeroFlowOrTimer(result);
        bool logAsAny = _broadAnyLogged < BroadDeliveryCap;
        bool logAsNonZero = nonZero && _broadNonZeroLogged < BroadDeliveryCap;
        if (!logAsAny && !logAsNonZero) return;

        if (logAsAny) _broadAnyLogged++;
        if (logAsNonZero) _broadNonZeroLogged++;

        var f = result.FlowInfo;
        _log.Info(
            $"[Dungeon] delivery src={source} method={methodId} scene_uuid={result.SceneUuid} " +
            $"runId={_state.CurrentRunId} " +
            $"flow(has={result.HasFlowInfo} state={f.State} active_time={f.ActiveTime} " +
            $"ready_time={f.ReadyTime} play_time={f.PlayTime} end_time={f.EndTime} " +
            $"settlement_time={f.SettlementTime} dungeon_times={f.DungeonTimes} result={f.Result}) " +
            $"timer(has={result.HasTimerInfo} type={result.TimerType} start_time_s={result.RunTimerStartMs / 1000} " +
            $"dungeon_times={result.TimerDungeonTimes} direction={result.TimerDirection} " +
            $"pause_time={result.TimerPauseTime} pause_total={result.TimerPauseTotalTime} " +
            $"cur_pause_ts={result.TimerCurPauseTimestamp}) " +
            $"settlement={result.HasSettlement} pass_time={result.PassTimeSeconds}s " +
            $"(any {_broadAnyLogged}/{BroadDeliveryCap}, nonzero {_broadNonZeroLogged}/{BroadDeliveryCap})");
    }

    // True when the delivery carries ANY non-zero flow_info / timer_info field,
    // or its scene_uuid matches the latched run id (best-effort — the dirty-mask
    // field-1 read is known-unreliable, so this is never the only trigger).
    private bool HasNonZeroFlowOrTimer(in DungeonSyncResult result)
    {
        long runId = _state.CurrentRunId;
        if (runId != 0 && result.SceneUuid == runId) return true;

        var f = result.FlowInfo;
        if (result.HasFlowInfo &&
            (f.State | f.ActiveTime | f.ReadyTime | f.PlayTime |
             f.EndTime | f.SettlementTime | f.DungeonTimes | f.Result) != 0)
        {
            return true;
        }

        if (result.HasTimerInfo &&
            (result.RunTimerStartMs != 0 ||
             (result.TimerType | result.TimerDungeonTimes | result.TimerDirection |
              result.TimerPauseTime | result.TimerPauseTotalTime |
              result.TimerCurPauseTimestamp) != 0))
        {
            return true;
        }

        return false;
    }

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
    /// Always-on, fires once per actual latch (at most once per run): records
    /// WHICH source latched <c>IDungeonState.RunTimerStartMs</c> —
    /// <c>src=timer_info</c> (PRIMARY, HUD-authoritative) vs
    /// <c>src=flow.play_time</c> (fallback). Called by
    /// <c>MaybeLatchRunTimer</c> only when the pre-write value was zero, i.e.
    /// when this delivery actually performed the first-non-zero-wins latch.
    /// </summary>
    private void DiagRunTimerLatched(string source, long startMs, in DungeonSyncResult result)
        => _log.Info(
            $"[Dungeon] run-timer latched src={source} start_s={startMs / 1000} start_ms={startMs} " +
            $"scene={result.SceneUuid} runId={_state.CurrentRunId}");

    /// <summary>
    /// One-shot, always-on: log the raw <c>DungeonTimerInfo</c> fields the first
    /// time field 15 arrives with a NON-ZERO <c>start_time</c> (zero deliveries
    /// do not consume the one-shot — early hub packets arrive all-zero).
    /// <c>timer_info.start_time</c> is the PRIMARY <c>RunTimerStartMs</c> source
    /// (HUD-authoritative, <c>dungeon_timer_vm.lua</c>); this line confirms the
    /// raw fields on the first real delivery.
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
