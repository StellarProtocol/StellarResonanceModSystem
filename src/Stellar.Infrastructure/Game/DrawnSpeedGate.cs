using System;
using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>Keeps a frozen entity frozen when the GAME rewrites its drawn speed (owner report 2026-10-02: monsters resume a
/// few seconds into a frozen fight; recon run 7 R7-5: the factor writers are inlined, <c>AnimCompBase.set_Speed</c> has 4
/// native callers and is the one hookable choke point for the drawn speed). A prefix on <c>set_Speed</c> asks
/// <see cref="TrySubstitute"/>: while armed, for an anim component this freeze tracks (built at stage 2 / appear time —
/// never a per-call reflection read) whose entity is not excluded, the incoming value becomes the latest restore value
/// (<see cref="FreezeLedger.NoteGameSpeed"/>) and 0 is written instead. Unarmed, untracked, our own write
/// (<see cref="OwnWrite"/>) or off the main thread: passthrough, untouched, allocation-free. Counts the substitutions
/// that stopped a real speed (incoming above 0) per entity kind for the diagnostics summary. Pure (unit-tested; the
/// component key is its native pointer).</summary>
internal sealed class DrawnSpeedGate
{
    /// <summary>Diagnostics buckets, in summary order.</summary>
    internal enum Bucket { Monster, Player, Npc, Pet, Mount, Other }

    private readonly Dictionary<IntPtr, (long Uuid, int Kind)> _byComp = new();
    private readonly int[] _held = new int[6];
    private readonly List<IntPtr> _drop = new();
    private int _mainThread;
    private FreezeLedger? _ledger;

    /// <summary>True between <see cref="Arm"/> and <see cref="Disarm"/>.</summary>
    public bool Armed => _ledger is not null;

    /// <summary>Set around the freeze's own <c>set_Speed</c> writes (stage 2, re-apply, restore) so they pass through.</summary>
    public bool OwnWrite { get; set; }

    /// <summary>Components tracked this freeze.</summary>
    public int Tracked => _byComp.Count;

    /// <summary>Uuids of the tracked entities (for the diagnostics read at unfreeze).</summary>
    public IEnumerable<long> TrackedUuids
    {
        get { foreach (var t in _byComp.Values) yield return t.Uuid; }
    }

    /// <summary>Starts gating for one freeze on the calling (main) thread: forgets the previous freeze's components and
    /// counts. Writes from any other thread pass through untouched.</summary>
    public void Arm(FreezeLedger ledger, int mainThreadId)
    {
        _byComp.Clear();
        Array.Clear(_held, 0, _held.Length);
        _mainThread = mainThreadId;
        _ledger = ledger;
    }

    /// <summary>Stops gating (before the unfreeze restores). Keeps the counts for the summary line.</summary>
    public void Disarm()
    {
        _ledger = null;
        _byComp.Clear();
    }

    /// <summary>Tracks <paramref name="comp"/> (an entity's live <c>ZModel.AnimComp</c>) as belonging to
    /// <paramref name="uuid"/>. Idempotent; ignored when unarmed, for a null pointer or an excluded entity.</summary>
    public void Track(IntPtr comp, long uuid, int kind)
    {
        if (_ledger is null || comp == IntPtr.Zero || _ledger.Excludes(uuid)) return;
        _byComp[comp] = (uuid, kind);
    }

    /// <summary>Stops gating every component of <paramref name="uuid"/> (an entity released mid-freeze).</summary>
    public void Untrack(long uuid)
    {
        _drop.Clear();
        foreach (var kv in _byComp)
            if (kv.Value.Uuid == uuid) _drop.Add(kv.Key);
        foreach (var comp in _drop) _byComp.Remove(comp);
    }

    /// <summary>The <c>set_Speed</c> prefix's question. True = <paramref name="value"/> was replaced by 0 and the incoming
    /// value kept as the restore value.</summary>
    public bool TrySubstitute(IntPtr comp, ref float value, int threadId)
    {
        if (_ledger is null || OwnWrite || threadId != _mainThread || !_byComp.TryGetValue(comp, out var t)) return false;
        if (!_ledger.NoteGameSpeed(t.Uuid, value)) return false;
        if (value > FreezeLedger.SpeedEpsilon) _held[(int)BucketOf(t.Kind)]++;
        value = 0f;
        return true;
    }

    /// <summary>Substitutions that stopped a real speed this freeze, for <paramref name="bucket"/>.</summary>
    public int Held(Bucket bucket) => _held[(int)bucket];

    internal static Bucket BucketOf(int kind) => kind switch
    {
        FreezeKinds.Monster => Bucket.Monster,
        FreezeKinds.Char => Bucket.Player,
        FreezeKinds.Npc => Bucket.Npc,
        FreezeKinds.Pet or FreezeKinds.VanityPet => Bucket.Pet,
        FreezeKinds.Vehicle => Bucket.Mount,
        _ => Bucket.Other,
    };
}
