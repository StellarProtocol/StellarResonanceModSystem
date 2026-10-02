using System;
using System.Collections.Generic;
using UnityEngine;
namespace Stellar.Infrastructure.Game;

/// <summary>Where the combat-freeze diagnostic prefixes (<see cref="FreezeDiagPatches"/>) count (diagnostics only).
/// Active only while a freeze is sampled; otherwise every hit returns after one field read. Main-thread hits are counted
/// per entity through <see cref="FreezeDiagCounters"/>; off-thread hits only bump an atomic counter — no shared table is
/// touched off the main thread (the run 7c hang: unlocked dictionaries shared with network-thread hooks). A game
/// component (<c>MoveComp</c>, <c>ZStateSkillComp</c>, <c>ZStateMoveComp</c>) is named by its native pointer; its owner
/// (<c>ZComponent.Host</c> → uuid) is resolved once per pointer, inside the game's own call on that live component, and
/// cached for the freeze. Nothing here dereferences a cached pointer.</summary>
internal sealed class FreezeDiagSink
{
    private readonly Func<object, object?>? _host;
    private readonly Func<object, long> _uuid;
    private readonly Func<int> _mainThread;

    public FreezeDiagSink(FreezeDiagCounters counters, Func<object, object?>? host, Func<object, long> uuid, Func<int> mainThread)
    {
        Counters = counters;
        _host = host;
        _uuid = uuid;
        _mainThread = mainThread;
    }

    public FreezeDiagCounters Counters { get; }

    /// <summary>True while a freeze is being sampled.</summary>
    public bool Active { get; set; }

    /// <summary>True while our own hold writes run (their <c>set_Position</c> calls are ours, not the game's).</summary>
    public bool InOwnHold { get; set; }

    /// <summary><c>Time.frameCount</c> of the last frame our hold wrote positions in.</summary>
    public int HoldFrame { get; set; } = -1;

    /// <summary>Methods whose first hit was already reported (one event line each, process rules § 15).</summary>
    public HashSet<string> FirstHits { get; } = new(StringComparer.Ordinal);

    /// <summary>First-hit event names waiting for the sampler's next frame (never logged from inside a game call).</summary>
    public List<string> PendingFirstHits { get; } = new();

    /// <summary>The last skill stage each watched caster played (<c>PlaySkillStage(skillEffectId, stageId, uuid, …)</c>).</summary>
    public Dictionary<long, (int Effect, int Stage)> LastStage { get; } = new();

    /// <summary>Frames our hold wrote positions in this freeze.</summary>
    public int HoldTicks { get; set; }

    public void NoteStage(long uuid, int effect, int stage)
    {
        if (Counters.IsWatched(uuid)) LastStage[uuid] = (effect, stage);
    }

    /// <summary>Starts one freeze: nothing hit yet.</summary>
    public void Begin()
    {
        LastStage.Clear();
        HoldTicks = 0;
        FirstHits.Clear();
        PendingFirstHits.Clear();
        InOwnHold = false;
        HoldFrame = -1;
        Active = true;
    }

    /// <summary>True on the main thread while active; an off-thread entry is counted and refused.</summary>
    public bool Enter()
    {
        if (!Active) return false;
        if (Environment.CurrentManagedThreadId == _mainThread()) return true;
        Counters.OffThread();
        return false;
    }

    public void Hit(object? instance, DiagSlot slot) => Counters.HitPtr(FreezeDiagReader.Ptr(instance), slot);

    public void Hit(long uuid, DiagSlot slot) => Counters.Hit(uuid, slot);

    /// <summary>One hit for the entity owning game component <paramref name="comp"/> (resolved once per pointer).</summary>
    public void HitHost(object? comp, DiagSlot slot)
    {
        var p = FreezeDiagReader.Ptr(comp);
        if (p == IntPtr.Zero) return;
        if (!Counters.TryOwner(p, out var uuid))
        {
            uuid = 0;
            try { if (_host?.Invoke(comp!) is { } host) uuid = _uuid(host); }
            catch { /* unresolvable: counted global */ }
            Counters.Map(p, uuid);   // the real owner: counted per entity whenever it is (or later becomes) watched
        }
        Counters.Hit(uuid, slot);
    }

    /// <summary>A game write of a drawn position: before or after our hold write this frame. Watched entities only (the
    /// frame read is skipped for everything else).</summary>
    public void HitPosition(object? goComp)
    {
        if (InOwnHold || !Counters.TryOwner(FreezeDiagReader.Ptr(goComp), out var uuid) || uuid == 0) return;
        Counters.Hit(uuid, HoldFrame == Time.frameCount ? DiagSlot.GoPosAfter : DiagSlot.GoPosBefore);
    }

    /// <summary>Records the first hit of <paramref name="name"/> for the sampler to log (§ 15: a hook that fires is
    /// distinguishable from one that never does).</summary>
    public void First(string name)
    {
        if (FirstHits.Add(name)) PendingFirstHits.Add(name);
    }
}
