using Stellar.Abstractions.Diagnostics;
using Stellar.Application.Abstractions;
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
    /// Always-on, fires once per actual latch (empty slot → value): records
    /// WHICH source latched <c>IDungeonState.RunTimerStartMs</c> —
    /// <c>src=method55.edge</c> / <c>src=timer_info</c> /
    /// <c>src=flow.play_time</c> / <c>src=flow.active_time (approx,
    /// pre-countdown)</c>. Driven by the service's write result, so it fires
    /// exactly when the write performed the latch.
    /// </summary>
    private void DiagRunTimerLatched(string source, long startMs, long sceneUuid)
        => _log.Info(
            $"[Dungeon] run-timer latched src={source} start_s={startMs / 1000} start_ms={startMs} " +
            $"scene={sceneUuid} runId={_state.CurrentRunId}");

    /// <summary>
    /// Always-on: a strictly better-ranked source OVERWROTE an earlier
    /// (approximate) latch — e.g. the method-55 arrival edge upgrading the
    /// entry sync's flow.active_time approximation. This line is the live
    /// validation signal for the rank design: seeing it on a real run proves
    /// both the approximate pre-latch AND the precise edge fired in order.
    /// </summary>
    private void DiagRunTimerUpgraded(string source, long startMs, long previousMs, long sceneUuid)
        => _log.Info(
            $"[Dungeon] run-timer UPGRADED src={source} start_s={startMs / 1000} start_ms={startMs} " +
            $"(was start_ms={previousMs}, delta_ms={startMs - previousMs}) " +
            $"scene={sceneUuid} runId={_state.CurrentRunId}");

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

    // Session caps for the deferred-path diagnostics (all emitted at DRAIN time
    // on the framework tick — never inside the Lua callback).
    private const int StartPlayingCap = 8;
    private int _startPlayingLogged;
    private int _deferredDropsLogged;
    private int _deferredStaleLogged;
    private bool _deferredThrewLogged;

    /// <summary>
    /// Always-on, session-capped (<see cref="StartPlayingCap"/> lines): one per
    /// drained WorldNtf method-55 (<c>NotifyStartPlayingDungeon</c>) delivery —
    /// the play-start EDGE. Logs the arrival stamp (captured at enqueue, so
    /// drain latency does not skew it), which clock supplied it (interpolated
    /// server clock vs client-UTC fallback with its skew caveat), the parsed
    /// char_id (the ntf also fires for other members' starts), and what the
    /// rank-guarded write did (latched / UPGRADED an approximate latch /
    /// ignored duplicate).
    /// </summary>
    private void DiagStartPlayingDungeon(in DeferredLuaDelivery item, RunTimerWrite write, long previousMs)
    {
        if (_startPlayingLogged >= StartPlayingCap) return;
        _startPlayingLogged++;

        bool hasCharId = StartPlayingDungeonReader.TryReadCharId(item.Payload, out long charId);
        _log.Info(
            $"[Dungeon] NotifyStartPlayingDungeon (method 55) src=method55.edge " +
            $"arrival_ms={item.ArrivalMs} clock={(item.ArrivalIsServerClock ? "server" : "client-utc(skew caveat)")} " +
            $"char_id={(hasCharId ? charId.ToString() : "unparsed")} write={write} " +
            $"(was start_ms={previousMs}) runId={_state.CurrentRunId} " +
            $"({_startPlayingLogged}/{StartPlayingCap})");
    }

    private bool _firstDirtyTimerLogged;

    /// <summary>
    /// Method-24 (SyncDungeonDirtyData) timer_info slice. One-shot always-on
    /// the first time a dirty delta carries a NON-ZERO <c>start_time</c> (the
    /// live-confirmation line for the traced HUD delta path — zero deliveries
    /// do not consume it); per-event repeats gated on
    /// <c>STELLAR_DIAGNOSTICS=1</c>. Emitted at DRAIN time on the framework
    /// tick, never inside the enqueue callback.
    /// </summary>
    private void DiagDungeonDirtyTimer(in DungeonDirtyTimerResult dirty)
    {
        bool oneShot = dirty.StartTimeSeconds != 0 && !_firstDirtyTimerLogged;
        if (oneShot) _firstDirtyTimerLogged = true;
        else if (!StellarDiagnostics.IsEnabled) return;

        _log.Info(
            $"[Dungeon] dirty-delta timer_info (method 24) src=timer_info.delta " +
            $"start_time_s={dirty.StartTimeSeconds} dungeon_times={dirty.DungeonTimes} " +
            $"direction={dirty.Direction} pause_total_time={dirty.PauseTotalTime} " +
            $"runId={_state.CurrentRunId}{(oneShot ? " (first non-zero start_time — HUD delta path confirmed)" : "")}");
    }

    // Drain-side visibility for the bounded queue's drop-new overflow policy.
    // Logs only when the drop counter has grown since the last drain, capped to
    // one line per new plateau to keep the log quiet under a pathological burst.
    private void DiagDeferredDrops()
    {
        int dropped = System.Threading.Volatile.Read(ref _deferredDropped);
        if (dropped <= _deferredDropsLogged) return;
        _deferredDropsLogged = dropped;
        _log.Warning($"[Dungeon] deferred lua-path queue overflow — {dropped} deliveries dropped so far (cap {DeferredCap})");
    }

    // Drain-side visibility for the run-id scope guard: items enqueued under
    // one run id and drained under another are skipped (no sink writes). Same
    // plateau pattern as the drop diag — one line per new plateau.
    private void DiagDeferredStaleSkips()
    {
        int stale = _deferredStaleSkipped;
        if (stale <= _deferredStaleLogged) return;
        _deferredStaleLogged = stale;
        _log.Info($"[Dungeon] deferred lua-path items skipped as stale — run id changed between enqueue and drain ({stale} so far)");
    }

    // One-shot: a deferred handler threw on the tick. The exception is already
    // contained (the drain wraps each item) — this line only surfaces it.
    private void DiagDeferredHandlerThrew(uint methodId, System.Exception ex)
    {
        if (_deferredThrewLogged) return;
        _deferredThrewLogged = true;
        _log.Warning($"[Dungeon] deferred handler threw (method={methodId}): {ex.GetType().Name}: {ex.Message}");
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
