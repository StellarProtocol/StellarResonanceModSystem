using System;
using System.Collections.Concurrent;
using System.Threading;
using Stellar.Application.Abstractions;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Crash-safe deferred processing for the LUA stub path. Live evidence: a build
/// whose Lua-path callback parsed + logged inline CRASHED the client during the
/// scene load right after leaving a dungeon (the identical build minus the Lua
/// tap passed the same in/out test), so no work may run inside
/// <c>ZLuaStub.OnCallStub</c> during scene transitions. The callback therefore
/// only enqueues (method id + the payload byte[] the dispatcher already copied
/// out of the IL2CPP span + an arrival timestamp) into a small bounded queue;
/// parsing, sink writes and ALL logging are deferred to
/// <see cref="DrainDeferred"/>, which Host drives from the framework tick — a
/// tick that is gated off during scene switches, so queued items naturally wait
/// until the world is stable again.
/// </summary>
internal sealed partial class PandaDungeonProbe
{
    // Bounded drop-new queue. 64 slots is generous: real traffic is a handful
    // of method-23 syncs per scene plus one method-55 per party member per run.
    // On overflow the NEW item is dropped (the queued ones are older and thus
    // closer to the true arrival order) and counted for the drain-side diag.
    private const int DeferredCap = 64;
    private const int MaxDrainPerTick = 16;

    private readonly ConcurrentQueue<DeferredLuaDelivery> _deferred = new();
    private int _deferredCount;
    private int _deferredDropped;

    private readonly struct DeferredLuaDelivery
    {
        public DeferredLuaDelivery(uint methodId, byte[] payload, long arrivalMs, bool arrivalIsServerClock)
        {
            MethodId = methodId;
            Payload = payload;
            ArrivalMs = arrivalMs;
            ArrivalIsServerClock = arrivalIsServerClock;
        }

        public uint MethodId { get; }
        public byte[] Payload { get; }

        /// <summary>Epoch ms captured at ENQUEUE (the packet's arrival), so drain latency never skews the method-55 edge stamp.</summary>
        public long ArrivalMs { get; }

        /// <summary>True when <see cref="ArrivalMs"/> came from the interpolated server clock; false = client UTC fallback (skew caveat).</summary>
        public bool ArrivalIsServerClock { get; }
    }

    // LUA-path callback (network thread, possibly mid scene-teardown): enqueue
    // ONLY. No parsing, no sink writes, no logging — a catch-all guard on top of
    // the dispatcher's own, because this path has crashed the client before.
    private void OnWorldNtfLua(uint methodId, byte[] payload)
    {
        try
        {
            if (Volatile.Read(ref _deferredCount) >= DeferredCap)
            {
                Interlocked.Increment(ref _deferredDropped);
                return;
            }
            Interlocked.Increment(ref _deferredCount);
            long arrivalMs = CaptureArrivalMs(out bool serverClock);
            _deferred.Enqueue(new DeferredLuaDelivery(methodId, payload, arrivalMs, serverClock));
        }
        catch
        {
            // Never throw across the IL2CPP boundary — and never log here
            // either (logging is work; this callback must stay inert).
        }
    }

    // Arrival stamp for the method-55 edge: the framework's interpolated server
    // clock (CombatService anchors it on SyncServerTime every ~5s and
    // extrapolates via Environment.TickCount64 — reading it is two field reads,
    // safe from this thread). Client UTC is the fallback until the first anchor
    // lands (then the stamp carries client-vs-server clock skew; flagged in the
    // diag line via ArrivalIsServerClock).
    private long CaptureArrivalMs(out bool serverClock)
    {
        long serverNow = _combat.ServerNowMs;
        if (serverNow != 0)
        {
            serverClock = true;
            return serverNow;
        }
        serverClock = false;
        return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    }

    /// <summary>
    /// Drain the deferred Lua-path deliveries. Called by Host from the
    /// framework tick (main thread) — the tick is gated off during scene
    /// transitions, so this never runs mid-teardown. Bounded per tick so a
    /// burst cannot stall a frame.
    /// </summary>
    public void DrainDeferred()
    {
        int processed = 0;
        while (processed < MaxDrainPerTick && _deferred.TryDequeue(out var item))
        {
            Interlocked.Decrement(ref _deferredCount);
            processed++;
            try { HandleDeferred(item); }
            catch (Exception ex) { DiagDeferredHandlerThrew(item.MethodId, ex); }
        }
        DiagDeferredDrops();
    }

    private void HandleDeferred(in DeferredLuaDelivery item)
    {
        if (item.MethodId == WorldNtfMethodIds.NotifyStartPlayingDungeon)
        {
            HandleStartPlayingDungeon(item);
            return;
        }
        HandleDelivery("lua", item.MethodId, item.Payload);
    }

    // WorldNtf method 55 (NotifyStartPlayingDungeon) — the play-start EDGE. Its
    // ARRIVAL is the run-timer start: stamp the arrival-time epoch as the rank-1
    // source (a strictly better rank than every payload-borne candidate, so it
    // UPGRADES the approximate flow.active_time latch the entry sync lands
    // first). The ntf also fires for OTHER party members' starts (the game's
    // own Lua impl early-returns on self) — duplicates are harmless, the rank
    // guard ignores equal-rank rewrites. char_id is parsed for the diag only.
    private void HandleStartPlayingDungeon(in DeferredLuaDelivery item)
    {
        long previousMs = _state.RunTimerStartMs;
        var write = _sink.SetRunTimerStart(item.ArrivalMs, RunTimerSource.Method55Edge);
        DiagStartPlayingDungeon(item, write, previousMs);
    }
}
