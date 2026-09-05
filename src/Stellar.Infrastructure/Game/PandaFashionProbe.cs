using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Reflection-based <see cref="IWardrobeProbe"/>. Reads the local player's worn cosmetic outfit
/// (region→fashionId) plus the current class's weapon skin, and applies a saved outfit / weapon skin
/// through the game's own Lua bridge + <c>WorldProxy.FashionWear</c> / <c>UseProfessionSkin</c> RPCs
/// (never constructing packets). Mirror of <see cref="PandaLoadoutProbe"/>'s bridge shape.
///
/// <para><b>Capture</b> is a cheap LOCAL container read (no RPC), refreshed EVENT-DRIVEN — on first
/// resolve, on the container-merge event (<see cref="OnGearChanged"/>, Host wires it to
/// <c>IInventory.SelfGearChanged</c>), and after our own apply — never on a timer. The capture chunk
/// carries no yielding call, so its globals are set synchronously and read back the same tick.</para>
///
/// <para><b>Apply</b> fires a yielding RPC chunk (deferred to the Update tick — the Lua VM is
/// main-thread-only) and polls the result global across ticks for the bare game code. Outfit and
/// weapon-skin applies share ONE pending slot and result global (a newer request supersedes an
/// unfinished one).</para>
///
/// <para>SOLID partial layout: Lua-bridge reflection + outfit chunk builders live in
/// <c>PandaFashionProbe.Resolution.cs</c>; the weapon-skin read/apply in
/// <c>PandaFashionProbe.WeaponSkin.cs</c>; gated per-event logging in
/// <c>PandaFashionProbe.Diagnostics.cs</c>.</para>
/// </summary>
internal sealed partial class PandaFashionProbe : IWardrobeProbe
{
    private static readonly TimeSpan CompletionTimeout = TimeSpan.FromSeconds(8);

    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _typeRegistry;

    // Worn snapshot (region→fashionId), written on the Update tick (main thread). Null until the first
    // in-world capture parses — which is also the in-world signal.
    private IReadOnlyDictionary<int, int>? _worn;

    // Re-capture arming: set on first resolve, on a container merge, and after an apply. Consumed on tick.
    private bool _captureDirty = true;

    private readonly object _pendingLock = new();
    private PendingApply? _pending;
    private readonly ConcurrentQueue<PendingApply> _toDispatch = new();

    public PandaFashionProbe(IPluginLog log, IGameTypeRegistry typeRegistry)
    {
        _log = log;
        _typeRegistry = typeRegistry;
    }

    public bool IsResolved => _bridgeResolved;

    public bool IsInWorld => _worn is not null;

    public IReadOnlyDictionary<int, int>? ReadWorn() => _worn;

    /// <summary>The container-merge event (a fashion change funnels through the same field-agnostic
    /// CharSerialize merge). Only flips a flag — safe to call from the network thread.</summary>
    public void OnGearChanged() => _captureDirty = true;

    /// <summary>Logout reset — drop the worn snapshots so <see cref="IsInWorld"/> falls false and the
    /// next in-world capture rebuilds them.</summary>
    public void ClearSession()
    {
        _worn = null;
        _weaponSkin = null;
        _captureDirty = true;
    }

    public Task<int> CallApplyAsync(IReadOnlyDictionary<int, int> outfit, CancellationToken ct)
        => Dispatch(BuildApplyChunk(outfit), FormatOutfit(outfit), ct);

    // Queue one game-RPC chunk (outfit or weapon skin) for the Update tick. Both kinds share the single
    // pending slot — a newer request supersedes an unfinished one (-2) — and the ApplyGlobal result read.
    private Task<int> Dispatch(string chunk, string label, CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return Task.FromResult(-2);
        if (!EnsureBridgeResolved()) return Task.FromResult(-3);

        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingApply(chunk, label, tcs, Stopwatch.StartNew());

        PendingApply? superseded;
        lock (_pendingLock)
        {
            superseded = _pending;
            _pending = pending;
        }
        superseded?.Complete(-2, this);

        if (ct.CanBeCanceled) pending.AttachCancellation(ct, this);

        // Defer the Lua call to the Update tick (main thread only).
        _toDispatch.Enqueue(pending);
        return tcs.Task;
    }

    /// <summary>Called per Update tick from the Host service tick (Unity main thread, world-gated).
    /// Resolves the bridge, refreshes the worn snapshots when armed, dispatches a queued apply, then
    /// polls the apply result global for completion.</summary>
    public void Tick()
    {
        TryResolveBridgeIfDue();
        if (!_bridgeResolved) return;

        CaptureIfDirty();
        DrainPendingDispatches();

        PendingApply? pending;
        lock (_pendingLock) { pending = _pending; }
        if (pending is null) return;

        var outcome = Evaluate(pending);
        if (outcome is { } code) pending.Complete(code, this);
    }

    // Capture is a synchronous local read (no yielding RPC in the chunk): invoke, then read back the
    // globals the same tick. One chunk writes both the outfit and the weapon-skin global.
    private void CaptureIfDirty()
    {
        if (!_captureDirty) return;
        _captureDirty = false;
        if (!InvokeChunk(CaptureChunk)) return;
        ParseWorn(ReadLuaGlobalString(WornGlobal));
        CaptureWeaponSkin();
    }

    // Parse "R;701:5;702:0;…" into a COMPLETE 14-region map (0 for any region the wire omitted; regions
    // outside WardrobeRegions.All — e.g. WeaponSkin 731 — are dropped). A raw that doesn't start with "R"
    // means "not ready" — keep the last snapshot rather than blanking it.
    private void ParseWorn(string? raw)
    {
        if (string.IsNullOrEmpty(raw) || raw![0] != 'R') return;

        var map = new Dictionary<int, int>(WardrobeRegions.All.Count);
        foreach (var region in WardrobeRegions.All) map[region] = 0;

        var parts = raw.Split(';');
        for (var i = 1; i < parts.Length; i++)
        {
            var kv = parts[i].Split(':');
            if (kv.Length == 2
                && int.TryParse(kv[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var region)
                && int.TryParse(kv[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fid)
                && map.ContainsKey(region))
            {
                map[region] = fid;
            }
        }

        _worn = map;
        DiagCaptured(map);
    }

    private void DrainPendingDispatches()
    {
        while (_toDispatch.TryDequeue(out var pending))
        {
            if (pending.IsCompleted) continue;

            InvokeChunk(ClearApplyGlobalChunk);
            if (InvokeChunk(pending.Chunk))
            {
                DiagDispatched(pending.Label);
            }
            else
            {
                pending.Complete(-3, this);
            }
        }
    }

    // Decide an in-flight apply's outcome, or null to keep waiting. The apply global holds the bare
    // game code once the RPC replies (0 = ok, positive = a game EErrorCode); -1 on timeout.
    private int? Evaluate(PendingApply pending)
    {
        if (pending.IsCompleted) return null;

        var raw = ReadLuaGlobalString(ApplyGlobal);
        if (!string.IsNullOrEmpty(raw)
            && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code))
        {
            _captureDirty = true;   // re-read the worn set so the overlay reflects the applied outfit / skin
            return code;
        }

        if (pending.Elapsed >= CompletionTimeout) return -1;
        return null;
    }

    private void RemovePending(PendingApply pending)
    {
        lock (_pendingLock)
        {
            if (ReferenceEquals(_pending, pending)) _pending = null;
        }
    }

    // A single in-flight apply (outfit or weapon skin — the Lua chunk decides). Completion is idempotent
    // and clears the owning probe's pending slot.
    private sealed class PendingApply
    {
        private readonly TaskCompletionSource<int> _tcs;
        private readonly Stopwatch _stopwatch;
        private CancellationTokenRegistration _ctReg;
        private int _completed;

        public PendingApply(string chunk, string label, TaskCompletionSource<int> tcs, Stopwatch stopwatch)
        {
            Chunk = chunk;
            Label = label;
            _tcs = tcs;
            _stopwatch = stopwatch;
        }

        /// <summary>The Lua chunk to run on the Update tick.</summary>
        public string Chunk { get; }

        /// <summary>Human-readable description for the (diagnostics-gated) dispatch line.</summary>
        public string Label { get; }

        public bool IsCompleted => Volatile.Read(ref _completed) != 0;
        public TimeSpan Elapsed => _stopwatch.Elapsed;

        public void AttachCancellation(CancellationToken ct, PandaFashionProbe owner)
        {
            _ctReg = ct.Register(static state =>
            {
                var (self, probe) = ((PendingApply, PandaFashionProbe))state!;
                self.Complete(-2, probe);
            }, (this, owner));
        }

        public void Complete(int code, PandaFashionProbe owner)
        {
            if (Interlocked.Exchange(ref _completed, 1) != 0) return;
            _stopwatch.Stop();
            owner.RemovePending(this);
            owner.DiagResult(code, _stopwatch.ElapsedMilliseconds);
            _tcs.TrySetResult(code);
            try { _ctReg.Dispose(); } catch { /* registration already gone */ }
        }
    }
}
