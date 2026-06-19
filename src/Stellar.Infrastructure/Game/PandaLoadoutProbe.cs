using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-based <see cref="ILoadoutProbe"/>. Reads + switches the player's active
/// loadout — internally the game's <b>Role Plan</b> system on the <c>weapon</c> Lua VM —
/// through the game's own Lua bridge + <c>WorldProxy</c> RPCs rather than constructing
/// packets (mirror of <see cref="PandaModuleEquipProbe"/>).
///
/// <para><b>Mechanism (CONFIRMED — <c>recon/loadout-switch-findings.md</c> § CONFIRMED
/// MECHANISM):</b> the plan list + current id come from a <c>SyncProjectList</c> RPC
/// cached in the <c>weapon_data</c> model; the switch is
/// <c>require("zproxy.world_proxy").SwitchProject({oldProjectId,newProjectId}, token)</c>,
/// returning <c>ret.errCode</c> (<c>EErrorCode</c>; 0 = success). The server runs every
/// validation (combat-lock 5018, no-such-plan 5022, profession-change 5026, …) — we
/// never bypass it.</para>
///
/// <para><b>Read path:</b> a throttled refresh chunk (every ~5s, plus once on resolve
/// and after a switch) fires the RPC and serializes <c>CurPlanId</c> + each plan's
/// id/name into the <c>_StellarLoadoutData</c> Lua global; each tick C# reads + parses
/// it into the cache that <see cref="ReadLoadouts"/> / <see cref="ReadCurrentIndex"/>
/// return. The async RPC writes the global a frame+ after firing, so reading the
/// previous result each tick is correct.</para>
///
/// <para>SOLID partial layout — Lua-bridge reflection + chunk builders + Lua-global
/// reads live in <c>PandaLoadoutProbe.Resolution.cs</c>; gated per-event logging in
/// <c>PandaLoadoutProbe.Diagnostics.cs</c>.</para>
/// </summary>
internal sealed partial class PandaLoadoutProbe : ILoadoutProbe
{
    // Loadout switch is a server RPC; allow headroom (profession/spec/gear apply is
    // heavier than module-equip). Poll the current id + the errCode global until the
    // switch resolves or this elapses.
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(8);

    // Refresh cadence — fire SyncProjectList at most this often (plus once on first
    // resolve and immediately after a successful switch). 60 ticks/s ⇒ ~5s.
    private const int RefreshEveryTicks = 300;

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    // Parsed read cache (written on the Update tick, read by the Application layer
    // tick — both on the main thread, so plain fields are fine).
    private IReadOnlyList<LoadoutEntry> _loadouts = Array.Empty<LoadoutEntry>();
    private int? _currentId;
    private string? _lastDataRaw;

    private int _refreshTickCounter;
    private bool _refreshedOnce;

    // Single in-flight switch. The whole loadout is one server-side id, so only one
    // switch can be outstanding at a time; a new dispatch supersedes the old.
    private readonly object _pendingLock = new();
    private PendingSwitch? _pending;

    // Dispatches enqueued by CallApplyAsync (any thread) and drained on the Update
    // tick — the game's Lua VM is main-thread-only (see PandaModuleEquipProbe).
    private readonly ConcurrentQueue<PendingSwitch> _toDispatch = new();

    public PandaLoadoutProbe(IPluginLog log, IGameTypeRegistry typeRegistry)
    {
        _log = log;
        _typeRegistry = typeRegistry;
    }

    public bool IsResolved => _bridgeResolved;

    public IReadOnlyList<LoadoutEntry> ReadLoadouts() => _loadouts;

    public int? ReadCurrentIndex() => _currentId;

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

        // Already on the requested loadout → immediate success (no-op switch).
        if (_currentId == index)
        {
            return Task.FromResult(LoadoutResult.Success);
        }

        var tcs = new TaskCompletionSource<LoadoutResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingSwitch(index, tcs, Stopwatch.StartNew());

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

    /// <summary>
    /// Called per Update tick from the Host service tick (the Unity main thread).
    /// Resolves the bridge (throttled), fires the throttled refresh, reads back the
    /// cached loadout data, fires any deferred switch dispatch, then polls the switch
    /// result global + current id for completion of the in-flight switch.
    /// </summary>
    public void DrainPendingCompletions()
    {
        TryResolveBridgeIfDue();
        if (!_bridgeResolved) return;

        RefreshIfDue();
        ParseLoadoutData();
        DrainPendingDispatches();

        PendingSwitch? pending;
        lock (_pendingLock) { pending = _pending; }
        if (pending is null) return;

        var outcome = Evaluate(pending);
        if (outcome is { } result)
        {
            pending.Complete(result, this);
        }
    }

    // Fire the SyncProjectList refresh chunk on first resolve, then every
    // RefreshEveryTicks. (Also fired immediately after a successful switch, below.)
    private void RefreshIfDue()
    {
        if (!_refreshedOnce)
        {
            _refreshedOnce = true;
            InvokeChunk(RefreshChunk);
            return;
        }
        if (_refreshTickCounter++ % RefreshEveryTicks == 0)
        {
            InvokeChunk(RefreshChunk);
        }
    }

    // Read + parse the data global written by the refresh chunk. Skips reparse when
    // the raw string is unchanged. First line is "CUR=<int>"; each subsequent
    // "<planId>\t<name>" line is a LoadoutEntry.
    private void ParseLoadoutData()
    {
        var raw = ReadLuaGlobalString(DataGlobal);
        if (string.IsNullOrEmpty(raw) || raw == _lastDataRaw) return;
        _lastDataRaw = raw;

        int? current = null;
        var entries = new List<LoadoutEntry>();
        foreach (var line in raw!.Split('\n'))
        {
            if (line.StartsWith("CUR=", StringComparison.Ordinal))
            {
                if (int.TryParse(line.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c))
                {
                    current = c;
                }
                continue;
            }
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            if (!int.TryParse(line.AsSpan(0, tab), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) continue;
            var name = line.Substring(tab + 1);
            entries.Add(new LoadoutEntry(id, name.Length == 0 ? $"Loadout {id}" : name));
        }

        // Sort by planId so hotkey N → a deterministic loadout. PlanDataDict is a Lua
        // map (pairs order is unspecified, and planIds go sparse after delete/recreate),
        // so without this the hotkey→loadout mapping is unstable across sessions.
        entries.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        _currentId = current;
        _loadouts = entries;
    }

    private void DrainPendingDispatches()
    {
        while (_toDispatch.TryDequeue(out var pending))
        {
            if (pending.IsCompleted) continue;

            // Clear the stale result before dispatching so the poll only sees this
            // switch's errCode.
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

    // Decide an in-flight switch's outcome, or null to keep waiting. Positive
    // completion: the current id flips to the target (authoritative). Otherwise map
    // the cached errCode; on timeout return Timeout.
    private LoadoutResult? Evaluate(PendingSwitch pending)
    {
        if (pending.IsCompleted) return null;

        if (_currentId == pending.TargetId)
        {
            TriggerRefreshAfterSwitch();
            return LoadoutResult.Success;
        }

        var err = ReadLuaGlobalString(SwitchGlobal);
        if (!string.IsNullOrEmpty(err))
        {
            var mapped = MapErrCode(err!);
            if (mapped == LoadoutResult.Success) TriggerRefreshAfterSwitch();
            return mapped;
        }

        if (pending.Elapsed >= CompletionTimeout)
        {
            return LoadoutResult.Timeout;
        }

        return null;
    }

    // Maps the WorldProxy.SwitchProject errCode (EErrorCode) string to a LoadoutResult.
    // EErrorCode success == 0 ("Success"); treat 0 / "Success" / "nil" (no error set on
    // ok) as Success. Codes: 5018 in-combat, 5022 no-such-plan, 5026 profession-change
    // rejected; any other non-zero numeric → Rejected.
    private static LoadoutResult MapErrCode(string err)
    {
        if (string.Equals(err, "nil", StringComparison.Ordinal) ||
            string.Equals(err, "Success", StringComparison.OrdinalIgnoreCase))
        {
            return LoadoutResult.Success;
        }
        if (!int.TryParse(err, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            // Non-numeric, non-"Success" enum name → treat as a refusal.
            return LoadoutResult.Rejected;
        }
        return code switch
        {
            0 => LoadoutResult.Success,
            5018 => LoadoutResult.InCombat,
            5019 => LoadoutResult.InCombat,
            5022 => LoadoutResult.NoSuchLoadout,
            5026 => LoadoutResult.Rejected,
            _ => LoadoutResult.Rejected,
        };
    }

    // Force the next tick to re-fire SyncProjectList so the list + current id reflect
    // the switch promptly (rather than waiting up to RefreshEveryTicks).
    private void TriggerRefreshAfterSwitch() => _refreshTickCounter = 0;

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

        public PendingSwitch(int targetId, TaskCompletionSource<LoadoutResult> tcs, Stopwatch stopwatch)
        {
            TargetId = targetId;
            _tcs = tcs;
            _stopwatch = stopwatch;
        }

        public int TargetId { get; }
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
