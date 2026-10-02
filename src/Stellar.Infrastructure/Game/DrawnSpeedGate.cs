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
/// component key is its native pointer).
/// <para><b>Pooled components</b> (review, regression <c>freeze_combat_resume_recycled_comp_*</c>): the game recycles
/// models, so one anim component can belong to a dead entity and then to a new one. The mapping follows the component's
/// CURRENT owner: <see cref="Track"/> re-keys a component (and drops the entity's previous one), <see cref="Forget"/>
/// drops a component whose new owner is refused, and <see cref="Untrack"/> (the despawn) drops the leaving entity's
/// component in O(1) through the uuid → component map.</para></summary>
internal sealed partial class DrawnSpeedGate
{
    /// <summary>Diagnostics buckets, in summary order.</summary>
    internal enum Bucket { Monster, Player, Npc, Pet, Mount, Other }

    private readonly Dictionary<IntPtr, (long Uuid, int Kind)> _byComp = new();
    private readonly Dictionary<long, IntPtr> _byUuid = new();
    private readonly int[] _held = new int[6];
    private FreezeLedger? _ledger;

    /// <summary>True between <see cref="Arm"/> and <see cref="Disarm"/>.</summary>
    public bool Armed => _ledger is not null;

    /// <summary>Set around the freeze's own <c>set_Speed</c> writes (stage 2, re-apply, restore) so they pass through.</summary>
    public bool OwnWrite { get; set; }

    /// <summary>The Unity main thread, as observed by the frame driver's late frame (<see cref="ObserveMainThread"/>) —
    /// never taken from whoever called <c>FreezeAll</c> (review). 0 until observed: every write passes through.</summary>
    public int MainThread { get; private set; }

    // The counters below are deliberately UNGATED (not behind StellarDiagnostics): an int increment each, cheaper than the
    // gate check itself, and they make the gated summary line exact whenever diagnostics are on.

    /// <summary>Game writes to a tracked component seen this freeze (any value, substituted or not) — the diagnostics
    /// proof that the prefix fires for this freeze's entities.</summary>
    public int Seen { get; private set; }

    /// <summary>Every <c>set_Speed</c> call the prefix saw since this freeze was armed (any component, any thread, armed
    /// or not) — the real game call rate for the perf budget (perf review: measure it in a raid). Not atomic: an
    /// off-thread call may be lost, which a diagnostics count tolerates.</summary>
    public int Calls { get; private set; }

    /// <summary>Components tracked this freeze.</summary>
    public int Tracked => _byComp.Count;

    /// <summary>Uuids of the tracked entities (for the diagnostics read at unfreeze; one component per entity).</summary>
    public IEnumerable<long> TrackedUuids => _byUuid.Keys;

    /// <summary>Starts gating for one freeze: forgets the previous freeze's components and counts.</summary>
    public void Arm(FreezeLedger ledger)
    {
        _byComp.Clear();
        _byUuid.Clear();
        Array.Clear(_held, 0, _held.Length);
        Seen = 0;
        Calls = 0;
        _ledger = ledger;
    }

    /// <summary>Stops gating (before the unfreeze restores). Keeps the counts for the summary line.</summary>
    public void Disarm()
    {
        _ledger = null;
        _byComp.Clear();
        _byUuid.Clear();
    }

    /// <summary>Records the main thread (called from the frame driver's late frame, which Unity runs on it).</summary>
    public void ObserveMainThread(int threadId) => MainThread = threadId;

    /// <summary>Counts one prefix entry (every call, before any other check).</summary>
    public void CountCall() => Calls++;

    /// <summary>Tracks <paramref name="comp"/> (an entity's live <c>ZModel.AnimComp</c>) as belonging to
    /// <paramref name="uuid"/>: re-keys a component another entity held (a recycled model) and drops a component the
    /// entity held before (a model swap). An excluded entity's component is FORGOTTEN, never tracked. Ignored when
    /// unarmed or for a null pointer.</summary>
    public void Track(IntPtr comp, long uuid, int kind)
    {
        if (_ledger is null || comp == IntPtr.Zero) return;
        if (_ledger.Excludes(uuid)) { Forget(comp); return; }
        Forget(comp);
        if (_byUuid.TryGetValue(uuid, out var previous)) _byComp.Remove(previous);
        _byComp[comp] = (uuid, kind);
        _byUuid[uuid] = comp;
    }

    /// <summary>The <c>AddEntity</c> postfix's re-key: the appearing entity's component (possibly a recycled one still
    /// keyed to a dead entity) is tracked under <paramref name="uuid"/> when <paramref name="admitted"/>, else forgotten —
    /// a refused owner (the local player, their own mount) is never gated, even when the ledger does not exclude it.</summary>
    public void Rekey(IntPtr comp, long uuid, int kind, bool admitted)
    {
        if (admitted) Track(comp, uuid, kind);
        else Forget(comp);
    }

    /// <summary>Stops gating <paramref name="comp"/> whoever it belonged to (its new owner is refused).</summary>
    public void Forget(IntPtr comp)
    {
        if (!_byComp.Remove(comp, out var t)) return;
        if (_byUuid.TryGetValue(t.Uuid, out var mine) && mine == comp) _byUuid.Remove(t.Uuid);
    }

    /// <summary>Stops gating <paramref name="uuid"/>'s component (an entity released mid-freeze, or leaving). O(1).</summary>
    public void Untrack(long uuid)
    {
        if (!_byUuid.Remove(uuid, out var comp)) return;
        if (_byComp.TryGetValue(comp, out var t) && t.Uuid == uuid) _byComp.Remove(comp);
    }

    /// <summary>The <c>set_Speed</c> prefix's question. True = <paramref name="value"/> was replaced by 0 and the incoming
    /// value kept as the restore value.</summary>
    public bool TrySubstitute(IntPtr comp, ref float value, int threadId)
    {
        if (_ledger is null || OwnWrite || threadId != MainThread || threadId == 0 || !_byComp.TryGetValue(comp, out var t)) return false;
        Seen++;
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
