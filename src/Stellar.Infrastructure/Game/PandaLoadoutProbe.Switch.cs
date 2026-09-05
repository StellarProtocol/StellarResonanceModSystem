using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Loadout;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// The loadout SWITCH (write) path for <see cref="PandaLoadoutProbe"/> — split out of
/// <c>PandaLoadoutProbe.cs</c> (2026-09-05) so the pre-dispatch gates fit under the 500-LoC standards
/// gate. Flow: <see cref="CallApplyAsync"/> (any thread) decides via <see cref="DecideSwitch"/>, enqueues,
/// the Update tick dispatches the Lua chunk, and <c>Evaluate</c> polls the wrapper's bool for completion.
///
/// <para><b>Why the pre-dispatch gates exist (owner report 2026-09-05 — 3,261 ms frametime spike).</b>
/// Switching to the loadout ALREADY worn 2-3 times in a row froze the game and then stuttered; the log
/// showed <c>[LoadoutSwitcher] Switched to Beam</c> eight times consecutively. The framework used to
/// dispatch every request unconditionally, on the assumption that the game "cheaply no-ops a switch to
/// the already-active loadout". That assumption is REFUTED by the game's own Lua:
/// <c>weapon_vm.lua:509-514</c> <c>AsyncSwitchRolePlan</c> builds
/// <c>{oldProjectId = CurPlanId, newProjectId = planId}</c> and fires <c>WorldProxy.SwitchProject</c>
/// UNCONDITIONALLY, then (<c>:536-542</c>) stamps <c>SwitchRolePlanTime</c>, saves
/// <c>currentProjectSyncData</c> and dispatches <c>OnRolePlanChange</c> — a full server re-equip, whose
/// <c>CharSerialize</c> delta burst then drives our own container-merge work. The in-game dropdown never
/// lets that happen: <c>role_plan_loop_item.lua:66</c> ignores the click when
/// <c>CurPlanId == PlanId</c>, <c>:118-121</c> refuses inside the 3 s cooldown
/// (<c>Global.lua:1703</c> <c>CombatStrategySswitchCd = 3</c>, tip 150208) and <c>:122-125</c> refuses in
/// battle (tip 150206). We mirror the first two here; the battle case stays where it already is (the
/// server refuses it and the game's own wrapper toasts the reason — <c>Evaluate</c> maps the wrapper's
/// <c>false</c> to <see cref="LoadoutResult.Rejected"/>).</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe
{
    // Loadout switch goes through the game's weapon-VM wrapper (AsyncSwitchRolePlan).
    // Poll CurPlanId == target (authoritative success) + the wrapper's bool result
    // global until the switch resolves or this elapses.
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(8);

    // The game's own switch cooldown: Global.lua:1703 `CombatStrategySswitchCd = 3` (seconds), enforced
    // by the dropdown at role_plan_loop_item.lua:118-121 against weaponData.SwitchRolePlanTime.
    internal const long SwitchCooldownMs = 3_000;

    // Sentinel for "we do not know the current plan id yet" (no parse since login / logout reset). It is
    // deliberately NOT -1: LiveCurrentIndex (-1) is the synthesized "Current" entry a caller can actually
    // pass as a target, and treating that as "already active" would swallow the request.
    internal const int UnknownPlanId = int.MinValue;

    // Cross-thread mirror of _currentId (an int? — not torn-read safe across threads). Written on the
    // main thread by ParseLoadoutData, read by CallApplyAsync, which the contract allows on any thread.
    // int reads/writes are atomic; volatile gives the visibility. This is the LIVE current plan: every
    // container merge (including an in-game dropdown switch) re-arms _refreshPending
    // (PandaLoadoutProbe.LiveState.cs), which re-fires SyncProjectList and re-parses CurPlanId — so it is
    // no longer the stale-only-after-a-plugin-switch value the removed fast-path comment warned about.
    // ClearSession blanks it to UnknownPlanId (the next character's worn plan is unknown until its first
    // parse), so on logout it deliberately does NOT track _currentId — unknown always dispatches.
    private volatile int _liveCurrentPlanId = UnknownPlanId;

    // Our own dispatch clock for the 3 s cooldown gate. The game's authority is
    // weaponData.SwitchRolePlanTime (stamped inside AsyncSwitchRolePlan on success, weapon_vm.lua:536) but
    // reading it means a Lua round-trip, and CallApplyAsync may run off the main thread where touching the
    // Lua VM is forbidden — so we time our OWN dispatches instead. Consequences, both accepted: a switch
    // made from the game's own dropdown does not arm ours (the dropdown enforces the cooldown itself), and
    // we arm at dispatch rather than at success, which is strictly more conservative than the game.
    // 0 = never dispatched.
    private long _lastSwitchDispatchMs;

    // Single in-flight switch. The whole loadout is one server-side id, so only one
    // switch can be outstanding at a time; a new dispatch supersedes the old.
    private readonly object _pendingLock = new();
    private PendingSwitch? _pending;

    // Dispatches enqueued by CallApplyAsync (any thread) and drained on the Update
    // tick — the game's Lua VM is main-thread-only (see PandaModuleEquipProbe).
    private readonly ConcurrentQueue<PendingSwitch> _toDispatch = new();

    // The tip id the game's OWN dropdown shows for a too-soon switch
    // (role_plan_loop_item.lua:119, inside the CombatStrategySswitchCd test at :118).
    internal const int SwitchCooldownTipId = 150208;

    // Tips queued for the main-thread drain, beside _toDispatch and emptied by the same
    // DrainPendingDispatches pass — Z.TipsVM lives in the Lua VM and CallApplyAsync may run off the main
    // thread, so the refusal cannot show the tip inline. Exactly ONE entry per refused press (nothing on
    // this path enqueues per tick), and BOUNDED: if the drain ever stalls, a mashing player must not grow
    // the queue without limit — the point of the gate is one refusal, not a backlog of them.
    private readonly ConcurrentQueue<int> _toTip = new();
    private const int MaxQueuedTips = 3;

    /// <summary>The game's own "you switched too recently" tip, as the exact call the dropdown makes:
    /// <c>role_plan_loop_item.lua:119</c> is <c>((Z.TipsVM).ShowTips)(150208)</c>, i.e. a DOT call
    /// <c>Z.TipsVM.ShowTips(&lt;id&gt;)</c> — not <c>ShowTipsLang</c> and not a colon call. Wrapped in
    /// <c>pcall</c> because the game calls it from inside <c>create_coro_xpcall</c> (a protected context)
    /// and we invoke it bare; without the guard a nil <c>Z.TipsVM</c> would surface as a Lua error under
    /// our <c>ChunkName</c>. The id is an int interpolated invariantly — no injection surface.</summary>
    internal static string BuildShowTipsChunk(int tipId)
        => string.Format(CultureInfo.InvariantCulture, "pcall(function() Z.TipsVM.ShowTips({0}) end)", tipId);

    // Enqueue a tip for the next main-thread drain. Silently drops past the cap (see _toTip).
    private void QueueTip(int tipId)
    {
        if (_toTip.Count >= MaxQueuedTips) return;
        _toTip.Enqueue(tipId);
    }

    /// <summary>Outcome of the pre-dispatch gates — see <see cref="DecideSwitch"/>.</summary>
    internal enum SwitchDecision
    {
        /// <summary>Send it: a different plan, outside the game's switch cooldown.</summary>
        Dispatch,

        /// <summary>The requested plan is the one already worn — the game's dropdown does not even
        /// offer Switch here (<c>role_plan_loop_item.lua:66</c>). Complete Success, send nothing.</summary>
        AlreadyActive,

        /// <summary>Inside the game's 3 s switch cooldown (<c>CombatStrategySswitchCd</c>). The dropdown
        /// refuses with tip 150208; we refuse with <see cref="LoadoutResult.Rejected"/>.</summary>
        CooldownActive,
    }

    /// <summary>Pure pre-dispatch decision for a loadout switch, mirroring the two client-side checks the
    /// game's own dropdown applies before it calls <c>AsyncSwitchRolePlan</c> (pinned by
    /// <c>PandaLoadoutProbeSwitchGateTests</c>; origin = owner report 2026-09-05, same-loadout re-press
    /// froze the client for 3,261 ms). Order matches the game's: already-active is checked first
    /// (<c>role_plan_loop_item.lua:66</c> returns before the cooldown test at <c>:118</c>).</summary>
    /// <param name="liveCurrentPlanId">The plan currently worn, or <see cref="UnknownPlanId"/>.</param>
    /// <param name="targetId">The requested plan id.</param>
    /// <param name="lastSwitchMs">Timestamp of our last dispatched switch; 0 = never.</param>
    /// <param name="nowMs">Now, same clock as <paramref name="lastSwitchMs"/>.</param>
    internal static SwitchDecision DecideSwitch(int liveCurrentPlanId, int targetId, long lastSwitchMs, long nowMs)
    {
        if (liveCurrentPlanId != UnknownPlanId && liveCurrentPlanId == targetId)
        {
            return SwitchDecision.AlreadyActive;
        }
        if (lastSwitchMs > 0 && nowMs - lastSwitchMs < SwitchCooldownMs)
        {
            return SwitchDecision.CooldownActive;
        }
        return SwitchDecision.Dispatch;
    }

    public Task<LoadoutResult> CallApplyAsync(int index, CancellationToken ct)
    {
        if (ct.IsCancellationRequested)
        {
            return Task.FromResult(LoadoutResult.Cancelled);
        }

        if (!EnsureBridgeResolved())
        {
            return Task.FromResult(LoadoutResult.GameApiUnavailable);
        }

        var currentAtDispatch = _liveCurrentPlanId;
        var now = Environment.TickCount64;
        switch (DecideSwitch(currentAtDispatch, index, Interlocked.Read(ref _lastSwitchDispatchMs), now))
        {
            case SwitchDecision.AlreadyActive:
                // A user action, so this line is always-on: it is the only explanation the owner gets for
                // a press that intentionally sent nothing.
                _log.Info($"[Stellar][Loadout] switch to {index} skipped: already active");
                return Task.FromResult(LoadoutResult.Success);
            case SwitchDecision.CooldownActive:
                _log.Info($"[Stellar][Loadout] switch to {index} refused: within the game's {SwitchCooldownMs / 1000}s switch cooldown");
                // Show the refusal the way the game does — its own tip 150208, in the player's language.
                // The plugin's Report() only toasts on Success, so without this the refusal is log-only.
                QueueTip(SwitchCooldownTipId);
                return Task.FromResult(LoadoutResult.Rejected);
        }
        Interlocked.Exchange(ref _lastSwitchDispatchMs, now);

        var tcs = new TaskCompletionSource<LoadoutResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingSwitch(index, tcs, Stopwatch.StartNew(), currentAtDispatch == index);

        PendingSwitch? superseded;
        lock (_pendingLock)
        {
            superseded = _pending;
            _pending = pending;
        }
        superseded?.Complete(LoadoutResult.Cancelled, this);

        if (ct.CanBeCanceled)
        {
            pending.AttachCancellation(ct, this);
        }

        // Defer the actual Lua call to the Update tick (main thread). Touching the
        // Lua VM off the Unity main thread corrupts IL2CPP/Lua state.
        _toDispatch.Enqueue(pending);
        return tcs.Task;
    }

    private void DrainPendingDispatches()
    {
        // Refusal tips first: instant feedback, and it still fires if a dispatch chunk below fails. Same
        // drain, same tick, no extra thread hop — the Lua VM is only touched from here.
        while (_toTip.TryDequeue(out var tipId))
        {
            InvokeChunk(BuildShowTipsChunk(tipId));
        }

        while (_toDispatch.TryDequeue(out var pending))
        {
            if (pending.IsCompleted) continue;

            // Clear the stale result before dispatching so the poll only sees this
            // switch's wrapper bool result.
            InvokeChunk(ClearSwitchGlobalChunk);
            if (InvokeChunk(BuildSwitchChunk(pending.TargetId)))
            {
                DiagDispatched(pending.TargetId);
            }
            else
            {
                pending.Complete(LoadoutResult.GameApiUnavailable, this);
            }
        }
    }

    // Decide an in-flight switch's outcome, or null to keep waiting. The weapon-VM
    // wrapper (AsyncSwitchRolePlan) returns a bool (true = success), not an errCode —
    // the game itself toasts the refusal reason (combat lock etc.), so we just need a
    // coarse success/rejected/timeout outcome:
    //   • the wrapper bool global is "true" → Success; "false" → Rejected.
    //   • else CurPlanId flips to the target → Success (only when it did NOT already match at dispatch).
    //   • else after the timeout → Timeout.
    private LoadoutResult? Evaluate(PendingSwitch pending)
    {
        if (pending.IsCompleted) return null;

        // The wrapper bool is the AUTHORITATIVE completion signal — it's written when
        // AsyncSwitchRolePlan returns. Check it FIRST: relying on _currentId flipping
        // would deadlock (it only refreshes AFTER a success → it could never become the
        // target). "true" → success + refresh the cache so _currentId catches up.
        var ok = ReadLuaGlobalString(SwitchGlobal);
        if (string.Equals(ok, "true", StringComparison.OrdinalIgnoreCase))
        {
            TriggerRefreshAfterSwitch();
            return LoadoutResult.Success;
        }
        if (string.Equals(ok, "false", StringComparison.OrdinalIgnoreCase))
        {
            return LoadoutResult.Rejected;
        }

        // Fallback: the server-synced current id already matches (e.g. switch landed
        // before we read the bool). SKIPPED when it already matched at dispatch — then this test is
        // vacuous and would complete the switch on the FIRST poll (~33 ms), long before the server
        // round-trip, so the caller's single-flight guard clears and the next press starts an
        // OVERLAPPING re-equip burst (owner report 2026-09-05: eight consecutive "Switched to Beam").
        // Such a request no longer dispatches at all (DecideSwitch), so this is the belt to that braces:
        // when it matched at dispatch, only the wrapper bool or the timeout may finish the switch.
        if (!pending.CurrentMatchedAtDispatch && _currentId == pending.TargetId)
        {
            TriggerRefreshAfterSwitch();
            return LoadoutResult.Success;
        }

        if (pending.Elapsed >= CompletionTimeout)
        {
            return LoadoutResult.Timeout;
        }

        return null;
    }

    // Flag the next tick to re-fire SyncProjectList so the list + current id reflect
    // the switch promptly. This is the only re-fetch besides the first-resolve one.
    private void TriggerRefreshAfterSwitch() => _refreshPending = true;

    private void RemovePending(PendingSwitch pending)
    {
        lock (_pendingLock)
        {
            if (ReferenceEquals(_pending, pending))
            {
                _pending = null;
            }
        }
    }

    // A single in-flight switch. Completion is idempotent and clears the owning
    // probe's pending slot; the cancellation registration is disposed on completion.
    private sealed class PendingSwitch
    {
        private readonly TaskCompletionSource<LoadoutResult> _tcs;
        private readonly Stopwatch _stopwatch;
        private CancellationTokenRegistration _ctReg;
        private int _completed;

        public PendingSwitch(int targetId, TaskCompletionSource<LoadoutResult> tcs, Stopwatch stopwatch,
            bool currentMatchedAtDispatch)
        {
            TargetId = targetId;
            _tcs = tcs;
            _stopwatch = stopwatch;
            CurrentMatchedAtDispatch = currentMatchedAtDispatch;
        }

        public int TargetId { get; }

        /// <summary>The live current plan already equalled <see cref="TargetId"/> when this switch was
        /// created — so <c>_currentId == TargetId</c> proves nothing about THIS switch. See
        /// <see cref="Evaluate"/>.</summary>
        public bool CurrentMatchedAtDispatch { get; }

        public bool IsCompleted => Volatile.Read(ref _completed) != 0;
        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public void AttachCancellation(CancellationToken ct, PandaLoadoutProbe owner)
        {
            _ctReg = ct.Register(static state =>
            {
                var (self, probe) = ((PendingSwitch, PandaLoadoutProbe))state!;
                self.Complete(LoadoutResult.Cancelled, probe);
            }, (this, owner));
        }

        public void Complete(LoadoutResult result, PandaLoadoutProbe owner)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _stopwatch.Stop();
            owner.RemovePending(this);
            owner.DiagResult(TargetId, result, _stopwatch.ElapsedMilliseconds);
            _tcs.TrySetResult(result);
            try { _ctReg.Dispose(); } catch { /* registration already gone */ }
        }
    }
}
