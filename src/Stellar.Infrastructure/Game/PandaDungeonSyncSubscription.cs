using System;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Events;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// MessagePipe SUBSCRIPTION to <c>Zservice.WorldNtfEvents.SyncDungeonDirtyDataMessageEvent</c>
/// (Panda.ZRpcGen; <c>WorldNtfEvents</c> is a namespace, not a declaring type) — the dungeon
/// container's dirty-DELTA event, whose C# consumer (<c>Panda.ZGame.DungeonSyncService</c>)
/// feeds Lua's <c>Z.ContainerMgr.DungeonSyncData:MergeData</c> (confirmed trace:
/// <c>lua/sync/dungeon_sync.lua</c> → <c>lua/zcontainer/dungeon_sync_data.lua</c>
/// field 15 timerInfo → <c>lua/zcontainer/dungeon_timer_info.lua</c> field 2
/// <c>startTime</c>) — the value the game's own dungeon-timer HUD displays.
///
/// <para>
/// HISTORY — why a subscription and not a HarmonyX patch: the first implementation
/// PREFIX-patched the service's compiler-generated MessagePipe handler
/// (<c>&lt;.ctor&gt;b__4_0</c>). The game CRASHED natively at the first event fire
/// (dungeon start click) — a marshaling fault in the Harmony trampoline on the
/// compiler-generated lambda, below any managed catch. Patching IL2CPP
/// compiler-generated lambdas is a dead end. Registering our OWN handler on the game's
/// <c>ISubscriber&lt;T&gt;</c> broker (via <see cref="MessagePipeContainerBridge"/>) is a
/// properly-registered il2cpp delegate invoked by the game's own dispatch — the
/// sanctioned pattern.
/// </para>
///
/// <para>
/// The handler copies the delta bytes out of the (pooled) event payload immediately —
/// <c>event.VData</c> (<c>Zproto.BufferStream</c>) <c>.Buffer</c>
/// (<c>Google.Protobuf.ByteString</c>) — and enqueues into
/// <see cref="PandaDungeonProbe"/>'s crash-safe deferred queue
/// (<see cref="PandaDungeonProbe.OnDungeonSyncDeltaDeferred"/>); parsing, sink writes and
/// logging happen at drain on the gated framework tick. Resolution is retried from the
/// tick until a route lands (bounded); failure degrades to a one-shot warning with the
/// method-24 stub tap, the method-55 edge and the rank-4 active_time latch as fallbacks.
/// The handler never throws into the game's publish path.
/// </para>
/// </summary>
internal sealed partial class PandaDungeonSyncSubscription : IDisposable
{
    private const string EventTypeFullName = "Zservice.WorldNtfEvents.SyncDungeonDirtyDataMessageEvent";

    // Retry budget for the tick-driven resolution loop (bounded by policy — see
    // "no unbounded polling"). At the default 30 Hz global rate this is ~10 minutes,
    // far past the point where the game's containers exist (world entry at latest).
    private const int MaxSubscribeAttempts = 18000;

    private readonly PandaDungeonProbe _probe;
    private readonly IPluginLog _log;
    private IDisposable? _subscription;
    private int _attempts;
    private bool _messagePipeImpossible;   // non-blittable delegate wall — MessagePipe route dead, wrap route unaffected

    public PandaDungeonSyncSubscription(PandaDungeonProbe probe, IPluginLog log)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        _log   = log   ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>
    /// Attempt to subscribe to the dirty-delta event through the game's own MessagePipe.
    /// Driven from the gated framework tick (and opportunistically when the VContainer
    /// resolver attaches) until it succeeds or the attempt budget is exhausted —
    /// idempotent after success. Failure never takes down the host: the fallback
    /// latches stay in charge.
    /// </summary>
    public void TrySubscribe(MessagePipeContainerBridge bridge)
    {
        if (_subscription is not null || _attempts >= MaxSubscribeAttempts) return;
        _attempts++;

        try
        {
            // PRIMARY route: wrap DungeonSyncService.OnSync — the delegate property
            // lua itself assigns (sync/dungeon_sync.lua). System.Action<IntPtr,int>
            // is fully blittable, so this dodges the non-blittable-struct wall that
            // makes the MessagePipe event subscription impossible. Also re-wraps if
            // lua re-assigns the property (checked every attempt tick). An active
            // wrap refunds the attempt so the re-check never exhausts the budget.
            if (TryWrapOnSync(bridge)) { _attempts--; return; }
            DiagWrapStillPending();

            if (_messagePipeImpossible) return;   // wrap keeps retrying; MessagePipe is dead
            _subscription = bridge.TrySubscribe(EventTypeFullName, OnDirtyDataEvent);
            if (_subscription is null)
            {
                DiagSubscribePending();
                return;
            }
            _log.Info($"[DungeonSync] subscribed to {EventTypeFullName} via game MessagePipe (no patching; attempt {_attempts})");
        }
        catch (Exception ex)
        {
            DiagSubscribeThrew(ex);
        }
    }

    /// <summary>Unsubscribes from the game broker and restores lua's OnSync on shutdown.</summary>
    public void Dispose()
    {
        UnwrapOnSync();
        var token = _subscription;
        _subscription = null;
        token?.Dispose();
    }

    // MessagePipe handler — runs on the game's publish path, so it must stay
    // copy-and-enqueue only and never throw back into the publisher.
    private void OnDirtyDataEvent(object? evt)
    {
        try { CaptureDelta(evt); }
        catch { /* never throw into the game's dispatch */ }
    }

    // Copy the delta bytes out of the (pooled) event payload IMMEDIATELY, then
    // hand the managed copy to the probe's deferred queue. No parsing, no sink
    // writes here — only the one-shot diagnostics.
    private void CaptureDelta(object? evt)
    {
        if (evt is null) return;

        var blob = ExtractBlob(evt);
        if (blob is null)
        {
            DiagExtractFailed();
            return;
        }

        _probe.OnDungeonSyncDeltaDeferred(blob);
        DiagDeltaCaptured(blob.Length);
    }
}
