using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>A monster killed while the scene is frozen stays visible, as it was, until the freeze ends (owner choice
/// 2026-10-02: local only — the game and the server still treat it as dead). Owner MAIN evidence (combat-diag run): the
/// kill arrives as <c>ZEntityMgr.RemoveEntity(uuid, EDisappearDead, removeImmediately: false)</c>, and the game removes it
/// at once (<c>GetEntity</c> stops serving it, so the freeze can no longer hold it; its death and dissolve then run
/// unfrozen). While armed, <see cref="Decide"/> answers the gated <c>RemoveEntity</c> prefix: a frozen monster's
/// <c>EDisappearDead</c> removal is QUEUED (the game's body and every chained prefix callback skipped — they all run
/// once, at the replay); <see cref="Replay"/> hands the queue back, in arrival order, to be re-issued through the game's
/// own <c>RemoveEntity</c> — on unfreeze, on the game's <c>ClearEntities</c> (leave scene / disconnect, before the scene
/// unloads) and on any framework release.
/// <para><b>Only <c>EDisappearDead</c>, only monsters, never immediate.</b> <c>Normal</c> is leaving the area of interest
/// and <c>TransferLeave</c>/<c>TransferPassLineLeave</c> are teleports — deferring those would keep a stale entity the
/// server already moved elsewhere; <c>Destroy</c> is a summon / Battle Imagine expiring (measured: every <c>Destroy</c> on
/// the owner's run was a player's summon), not a kill. Bullets and dummies (kinds 6/7/11) also die as <c>Dead</c>, ~20 a
/// second — effect-driven, already frozen as effects; deferring them would only grow the queue. A
/// <c>removeImmediately: true</c> call is the game insisting (<c>ZEntityCreator.TryCreateEntityReady</c> re-creating the
/// same uuid — a server-side reuse): it always runs, and a queued removal of that uuid is dropped, superseded by it.</para>
/// <para><b>Identity</b> (review 2026-10-02): a deferred call keeps the entity's native pointer (<see cref="Call.Identity"/>);
/// <see cref="Replay"/> re-issues it only while <c>GetEntity(uuid)</c> still serves that SAME object, so a uuid the server
/// reused for a different entity in the meantime is never removed by a stale replay. A call whose identity is unreadable
/// is never deferred. Off the main thread every call passes through (as the speed gates).</para>
/// Bounded at <see cref="Cap"/> (beyond it the game removes as usual). Main thread. Pure (unit-tested).</summary>
internal sealed class DeferredRemovals
{
    internal const int Cap = 32;
    internal const string DeadType = "EDisappearDead";

    /// <summary>What the gated <c>RemoveEntity</c> prefix does with one call.</summary>
    internal enum Decision { Run, Defer, RunSuperseding }

    /// <summary>One <c>RemoveEntity(uuid, type, immediate)</c> call as the gate reads it: <see cref="Type"/> is the
    /// <c>EDisappearType</c> name, <see cref="Kind"/> the entity's <c>LuaEntType</c>, <see cref="Excluded"/> the freeze's
    /// exclusion (the local player, their own mount), <see cref="Identity"/> the entity's native pointer (0 = unreadable)
    /// and <see cref="OffMainThread"/> whether the call came from another thread than Unity's main one (or before it was
    /// observed).</summary>
    internal readonly record struct Call(long Uuid, string? Type, bool Immediate, int Kind, bool Excluded, nint Identity, bool OffMainThread);

    private readonly List<(long Uuid, nint Identity, object?[] Args)> _queue = new();

    /// <summary>True while a freeze is on.</summary>
    public bool Armed { get; private set; }

    /// <summary>True while <see cref="Replay"/> re-issues the queue: those calls always run.</summary>
    public bool Replaying { get; private set; }

    public int Count => _queue.Count;

    /// <summary>Removals deferred this freeze (diagnostics).</summary>
    public int Deferred { get; private set; }

    /// <summary>Removals replayed this freeze (diagnostics).</summary>
    public int Replayed { get; private set; }

    /// <summary>Queued removals dropped at the replay because their uuid now serves another entity, or none (diagnostics).</summary>
    public int Stale { get; private set; }

    public void Arm()
    {
        Armed = true;
        Deferred = Replayed = Stale = 0;
    }

    /// <summary>Stops deferring. The queue must already be replayed (<see cref="Replay"/>); anything left is dropped.</summary>
    public void Disarm()
    {
        Armed = false;
        _queue.Clear();
    }

    /// <summary>Drops the queue unreplayed (the game's manager is gone: nothing left to remove). Returns how many.</summary>
    public int Drop()
    {
        var n = _queue.Count;
        _queue.Clear();
        return n;
    }

    public bool IsQueued(long uuid) => IndexOf(uuid) >= 0;

    /// <summary>The prefix's question for one <paramref name="call"/>. <paramref name="args"/> (the game's own arguments)
    /// are kept, copied, for the replay.</summary>
    public Decision Decide(Call call, object?[] args)
    {
        var uuid = call.Uuid;
        if (!Armed || Replaying || uuid == 0 || call.OffMainThread) return Decision.Run;
        var deferrable = call.Type == DeadType && !call.Immediate && call.Kind == FreezeKinds.Monster && !call.Excluded &&
                         call.Identity != 0;
        var queued = IndexOf(uuid);
        if (queued >= 0)
        {
            if (deferrable)
            {
                // The same death again: the queued call stands — re-keyed to the entity the uuid serves NOW.
                if (_queue[queued].Identity != call.Identity) _queue[queued] = (uuid, call.Identity, (object?[])args.Clone());
                return Decision.Defer;
            }
            _queue.RemoveAt(queued);                 // the game insists (immediate, another type): it runs now, once
            return Decision.RunSuperseding;
        }
        if (!deferrable || _queue.Count >= Cap) return Decision.Run;
        _queue.Add((uuid, call.Identity, (object?[])args.Clone()));
        Deferred++;
        return Decision.Defer;
    }

    /// <summary>Re-issues every queued removal through <paramref name="remove"/>, in arrival order, with
    /// <see cref="Replaying"/> set (so the gate lets them through) and the queue emptied first (a reentrant removal never
    /// sees a stale entry). An entry is re-issued only while <paramref name="identityNow"/> (the native pointer
    /// <c>GetEntity(uuid)</c> serves now, 0 = none) is the one kept at defer time; otherwise it is dropped
    /// (<see cref="Stale"/>) — the uuid now belongs to another entity, or to none. One failing replay never skips the rest.
    /// Returns how many were re-issued.</summary>
    public int Replay(System.Func<long, nint> identityNow, System.Action<long, object?[]> remove)
    {
        if (_queue.Count == 0) return 0;
        var batch = _queue.ToArray();
        _queue.Clear();
        var issued = 0;
        Replaying = true;
        try
        {
            foreach (var (uuid, identity, args) in batch)
            {
                if (!SameEntity(identityNow, uuid, identity)) { Stale++; continue; }
                try { remove(uuid, args); }
                catch { /* the caller warns; the rest still replay */ }
                Replayed++;
                issued++;
            }
        }
        finally { Replaying = false; }
        return issued;
    }

    private static bool SameEntity(System.Func<long, nint> identityNow, long uuid, nint identity)
    {
        try { return identity != 0 && identityNow(uuid) == identity; }
        catch { return false; }   // unreadable now: never remove what cannot be proven to be the same entity
    }

    private int IndexOf(long uuid)
    {
        for (var i = 0; i < _queue.Count; i++)
            if (_queue[i].Uuid == uuid) return i;
        return -1;
    }
}
