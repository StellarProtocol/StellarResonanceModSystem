using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// The loadout SAVE (write) path for <see cref="PandaLoadoutProbe"/> — "copy the worn setup into
/// another loadout" (spec <c>docs/superpowers/specs/2026-09-26-loadout-copy-design.md</c>, owner-approved
/// 2026-09-26). Mirrors the switch path in <c>PandaLoadoutProbe.Switch.cs</c>: <see cref="CallSaveAsync"/>
/// (any thread) decides via <see cref="DecideSave"/>, enqueues, the Update tick dispatches the Lua chunk,
/// and <see cref="EvaluatePendingSave"/> reads the wrapper's bool for completion.
///
/// <para><b>Mechanism (measured on the owner's MAIN client, 2026-09-26, probe
/// <c>Stellar.LoadoutSaveProbe</c>).</b> The game's own wrapper
/// <c>weapon_vm.lua:595</c> <c>WeaponVM.AsyncSaveRolePlan(planId, cancelToken)</c> sends
/// <c>SaveProject{projectId = planId}</c> and the server stores the LIVE setup (class, gear, modules,
/// skills incl. the Battle Imagine slots 7/8, talents) into that plan — a NON-worn plan id is accepted.
/// On success the wrapper updates <c>weapon_data</c> (<c>SaveDifferentRolePlanServerData</c>), shows tip
/// 150209, dispatches <c>OnRolePlanChange</c> and returns <c>true</c>; on refusal it shows the server's
/// reason (<c>showError</c>) and returns <c>false</c>. We drive the WRAPPER (Approach B), never the raw
/// RPC, so the game's own client-side bookkeeping runs.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe : ILoadoutSaveProbe
{
    // Result global the save chunk writes (tostring of the wrapper's bool).
    private const string SaveGlobal = "_StellarLoadoutSave";

    // Same budget as a switch: one server round-trip, answered inline by the wrapper.
    private static readonly TimeSpan SaveTimeout = TimeSpan.FromSeconds(8);

    // Real (server-side) plan ids from the last parse, for the "is the target a saved loadout" gate.
    // Replaced wholesale on the main thread; CallSaveAsync may read it from any thread (reference read).
    private volatile int[] _knownPlanIds = Array.Empty<int>();

    // Single in-flight save. Written under _pendingLock (shared with the switch slot, so the
    // "switch or save in flight" gate reads both atomically).
    private PendingSave? _pendingSave;

    /// <summary>Outcome of the pre-dispatch save gates — see <see cref="DecideSave"/>.</summary>
    internal enum SaveDecision
    {
        /// <summary>Send it.</summary>
        Dispatch,

        /// <summary>No worn plan parsed yet (just logged in / after logout) — we cannot prove the target
        /// is not the worn loadout, so nothing is sent.</summary>
        CurrentUnknown,

        /// <summary>The target IS the worn loadout. "Copy the worn loadout onto itself" is the plain Save
        /// button; the copy feature never sends it.</summary>
        TargetIsWorn,

        /// <summary>The target id is not one of the saved loadouts (includes the synthesized
        /// <c>LiveCurrentIndex</c> entry, which has no server-side plan).</summary>
        NoSuchTarget,

        /// <summary>A loadout switch or another save is still in flight — saving mid-switch would store
        /// a half-applied setup.</summary>
        Busy,
    }

    /// <summary>Pure pre-dispatch decision for a save (pinned by <c>PandaLoadoutProbeSaveTests</c>).
    /// Order: worn-plan knowledge first (without it no other answer is safe), then the target itself,
    /// then the in-flight gate.</summary>
    internal static SaveDecision DecideSave(int liveCurrentPlanId, int targetId, bool targetKnown,
        bool switchInFlight, bool saveInFlight)
    {
        if (liveCurrentPlanId == UnknownPlanId) return SaveDecision.CurrentUnknown;
        if (liveCurrentPlanId == targetId) return SaveDecision.TargetIsWorn;
        if (!targetKnown) return SaveDecision.NoSuchTarget;
        if (switchInFlight || saveInFlight) return SaveDecision.Busy;
        return SaveDecision.Dispatch;
    }

    /// <summary>The <see cref="LoadoutResult"/> a refused <see cref="SaveDecision"/> completes with.</summary>
    internal static LoadoutResult RefusalResult(SaveDecision decision) => decision switch
    {
        SaveDecision.CurrentUnknown => LoadoutResult.GameApiUnavailable,
        SaveDecision.NoSuchTarget   => LoadoutResult.NoSuchLoadout,
        _                           => LoadoutResult.Rejected,   // TargetIsWorn, Busy
    };

    /// <summary>Pure parse of the save result global: the wrapper's <c>tostring(bool)</c>.
    /// <c>"true"</c> → Success, <c>"false"</c> → Rejected (the game already toasted why), anything else
    /// (unset / not yet answered) → null = keep waiting.</summary>
    internal static LoadoutResult? ParseSaveResult(string? raw)
    {
        if (string.Equals(raw, "true", StringComparison.OrdinalIgnoreCase)) return LoadoutResult.Success;
        if (string.Equals(raw, "false", StringComparison.OrdinalIgnoreCase)) return LoadoutResult.Rejected;
        return null;
    }

    /// <summary>The save chunk: the game's OWN wrapper inside the coroutine wrapper (the RPC yields; a nil
    /// token never resumes), bool written to <see cref="SaveGlobal"/>. <paramref name="planId"/> is an int
    /// interpolated invariantly — no injection surface.</summary>
    internal static string BuildSaveChunk(int planId)
        => string.Format(
            CultureInfo.InvariantCulture,
            "(Z.CoroUtil.create_coro_xpcall(function()" +
            " local ok=Z.VMMgr.GetVM(\"weapon\").AsyncSaveRolePlan({0}, {1})" +
            " rawset(_G,\"{2}\", tostring(ok))" +
            " end))()",
            planId, NeverCancelToken, SaveGlobal);

    private const string ClearSaveGlobalChunk = "rawset(_G,\"" + SaveGlobal + "\", nil)";

    public Task<LoadoutResult> CallSaveAsync(int index, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromResult(LoadoutResult.Cancelled);
        if (!EnsureBridgeResolved()) return Task.FromResult(LoadoutResult.GameApiUnavailable);

        var tcs = new TaskCompletionSource<LoadoutResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        SaveDecision decision;
        lock (_pendingLock)
        {
            decision = DecideSave(_liveCurrentPlanId, index, Array.IndexOf(_knownPlanIds, index) >= 0,
                _pending is not null, _pendingSave is not null);
            if (decision == SaveDecision.Dispatch)
                _pendingSave = new PendingSave(index, tcs, ct);
        }

        if (decision != SaveDecision.Dispatch)
        {
            // A user action, so this line is always-on — the only explanation for a click that sent nothing.
            _log.Info($"[Stellar][Loadout] save to {index} refused: {decision}");
            return Task.FromResult(RefusalResult(decision));
        }
        return tcs.Task;   // dispatched on the next main-thread drain (the Lua VM is main-thread-only)
    }

    // Main thread, from DrainPendingCompletions: dispatch a queued save, or evaluate the one in flight.
    private void DrainPendingSave()
    {
        PendingSave? save;
        lock (_pendingLock) { save = _pendingSave; }
        if (save is null) return;

        if (!save.Dispatched)
        {
            // Cancellation is honoured only BEFORE the request leaves: a sent save cannot be recalled, and
            // reporting Cancelled for a save that landed would skip the caller's post-success work.
            if (save.Token.IsCancellationRequested) { CompleteSave(save, LoadoutResult.Cancelled); return; }
            InvokeChunk(ClearSaveGlobalChunk);
            if (!InvokeChunk(BuildSaveChunk(save.TargetId))) { CompleteSave(save, LoadoutResult.GameApiUnavailable); return; }
            save.MarkDispatched();
            DiagSaveDispatched(save.TargetId);
            return;
        }

        if (EvaluatePendingSave(save) is { } result) CompleteSave(save, result);
    }

    private LoadoutResult? EvaluatePendingSave(PendingSave save)
    {
        var result = ParseSaveResult(ReadLuaGlobalString(SaveGlobal));
        if (result is null && save.Elapsed >= SaveTimeout) return LoadoutResult.Timeout;
        return result;
    }

    private void CompleteSave(PendingSave save, LoadoutResult result)
    {
        lock (_pendingLock)
        {
            if (!ReferenceEquals(_pendingSave, save)) return;
            _pendingSave = null;
        }
        if (result == LoadoutResult.Success)
        {
            // The target's saved data changed server-side: re-fire SyncProjectList so GetSlots() and
            // LoadoutsChanged reflect it (the same on-demand refresh a switch arms). That dump carries the
            // UNSAVED row too, so the unsaved flag is re-evaluated against the fresh plan data for free.
            _refreshPending = true;
        }
        _log.Info($"[Stellar][Loadout] save to {save.TargetId} -> {result} after {save.Elapsed.TotalMilliseconds:F0}ms");
        save.Tcs.TrySetResult(result);
    }

    // Logout: a save owed to the previous character never reports into the next one.
    private void CancelPendingSaveForLogout()
    {
        PendingSave? save;
        lock (_pendingLock) { save = _pendingSave; _pendingSave = null; }
        save?.Tcs.TrySetResult(LoadoutResult.Cancelled);
        _knownPlanIds = Array.Empty<int>();
    }

    // A single in-flight save. Main-thread state except Tcs/Token (thread-safe types).
    private sealed class PendingSave
    {
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();

        public PendingSave(int targetId, TaskCompletionSource<LoadoutResult> tcs, CancellationToken token)
        {
            TargetId = targetId;
            Tcs = tcs;
            Token = token;
        }

        public int TargetId { get; }
        public TaskCompletionSource<LoadoutResult> Tcs { get; }
        public CancellationToken Token { get; }
        public bool Dispatched { get; private set; }
        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public void MarkDispatched()
        {
            Dispatched = true;
            _stopwatch.Restart();   // the timeout measures the server round-trip, not the queue wait
        }
    }
}
