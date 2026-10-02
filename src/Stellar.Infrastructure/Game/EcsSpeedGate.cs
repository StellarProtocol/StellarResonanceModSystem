using System;
using System.Collections.Generic;
namespace Stellar.Infrastructure.Game;

/// <summary>Keeps a frozen ECS model's ANIMATION frozen at the ECS layer (owner MAIN evidence 2026-10-02, combat-diag run:
/// the boss — <c>MonsterEnt</c> / <c>ECSAnimComp</c> / <c>ECSAnimController</c> — kept animating, casting skills, with its
/// drawn <c>AnimComp.Speed</c> AND its controller's speed both reading 0). The ECS animator reads a per-LAYER speed, and
/// the controller's own play paths write it straight from the state they play (release_3.7 ISIL:
/// <c>ECSAnimController.playBaseState / playUpperState / playAdditiveState / syncECSState / SetPersistTime / set_Speed</c>
/// all call <c>ECSModel.ECSModelResourceManager.SetAnimatorLayerData(uid, layer, speed, weight)</c>, and
/// <c>ECSAnimState.Play</c> passes the state's speed to <c>PlayState / PlayClip / PlayDynamicState</c>) — so a skill stage
/// restarts its layer at full speed past both gated speeds. Static prefixes on those four (<see cref="EcsSpeedPatch"/>)
/// ask <see cref="TrySubstitute"/>: while armed, for an ECS uid this freeze tracks, the game's wanted speed is RECORDED
/// per layer and 0 is written instead. Untracked uids (the local player, the photo copies, UI models), our own writes
/// (<see cref="OwnWrite"/>) and off-main-thread calls pass through untouched.
/// <para><b>Release</b> (<see cref="TakeReleasePlan"/>): the whole model at the controller's own speed — the game's own
/// <c>set_Speed</c> call, <c>(uid, -1, speed, 1)</c> — then each layer's latest wanted speed, in the order the game asked.
/// A whole-model write (<c>layer -1</c>) from the game drops the per-layer wishes before it but is not itself replayed:
/// while frozen it is the drawn-speed chain carrying our substituted 0 (the controller restore re-issues the real one).</para>
/// <para><b>Pooled uids</b> (as <see cref="DrawnSpeedGate"/>): <see cref="Track"/> re-keys a uid another entity held;
/// <see cref="Untrack"/> (the despawn) drops the entity's uid and its wishes, so nothing is ever replayed onto a recycled
/// model — and <see cref="Release"/> (a leaving entity, a mid-freeze release, the teardown) first writes the model back
/// (the whole model at its controller's speed, then its wishes) when it is still the entity's live model, so a pooled model
/// is never reused with its layers at 0 (review 2026-10-02). Counts are ungated ints (diagnostics read them); the per-writer
/// call counters live in <c>EcsSpeedGate.Diagnostics.cs</c>. Main thread. Pure (unit-tested).</para></summary>
internal sealed partial class EcsSpeedGate
{
    /// <summary>One ECS layer write: <c>layer -1</c> = every layer.</summary>
    internal readonly record struct LayerWrite(int Layer, float Speed, float Weight);

    internal const int WholeModel = -1;

    private readonly Dictionary<uint, long> _byUid = new();
    private readonly Dictionary<long, uint> _byUuid = new();
    private readonly Dictionary<uint, List<LayerWrite>> _wanted = new();
    private readonly Dictionary<long, int> _heldBy = new();
    private readonly Dictionary<long, int> _leakedBy = new();

    /// <summary>True between <see cref="Arm"/> and <see cref="Disarm"/>.</summary>
    public bool Armed { get; private set; }

    /// <summary>Set around the freeze's own writes so they pass through unrecorded.</summary>
    public bool OwnWrite { get; set; }

    /// <summary>The Unity main thread (observed by the late frame); 0 until observed = every write passes.</summary>
    public int MainThread { get; private set; }

    /// <summary>Game writes above 0 turned into 0 this freeze.</summary>
    public int Held { get; private set; }

    /// <summary>Game writes above 0 on a tracked uid that passed through (off the main thread) this freeze.</summary>
    public int Leaked { get; private set; }

    public int Tracked => _byUid.Count;

    public void Arm()
    {
        Clear();
        Held = Leaked = 0;
        _heldBy.Clear();
        _leakedBy.Clear();
        ResetCallCounts();
        Armed = true;
    }

    /// <summary>Stops gating; the tracked uids and their wishes stay for <see cref="TakeReleasePlan"/>.</summary>
    public void Disarm() => Armed = false;

    /// <summary>Forgets every uid and wish (after the release).</summary>
    public void Clear()
    {
        _byUid.Clear();
        _byUuid.Clear();
        _wanted.Clear();
    }

    public void ObserveMainThread(int threadId) => MainThread = threadId;

    /// <summary>Tracks <paramref name="uid"/> as <paramref name="uuid"/>'s model (re-keys a pooled uid; drops the
    /// entity's previous uid). True when newly tracked — the caller then writes the whole model to 0 once.</summary>
    public bool Track(uint uid, long uuid)
    {
        if (!Armed || uid == 0 || uuid == 0) return false;
        if (_byUid.TryGetValue(uid, out var owner) && owner == uuid) return false;
        Forget(uid);
        if (_byUuid.TryGetValue(uuid, out var previous)) Forget(previous);
        _byUid[uid] = uuid;
        _byUuid[uuid] = uid;
        return true;
    }

    /// <summary>Stops gating <paramref name="uid"/> and drops its wishes, whoever held it.</summary>
    public void Forget(uint uid)
    {
        _wanted.Remove(uid);
        if (!_byUid.Remove(uid, out var uuid)) return;
        if (_byUuid.TryGetValue(uuid, out var mine) && mine == uid) _byUuid.Remove(uuid);
    }

    /// <summary>Stops gating <paramref name="uuid"/>'s model (released mid-freeze, or leaving). Returns its uid (0 = none).</summary>
    public uint Untrack(long uuid)
    {
        if (!_byUuid.TryGetValue(uuid, out var uid)) return 0;
        Forget(uid);
        return uid;
    }

    public bool Tracks(uint uid) => _byUid.ContainsKey(uid);

    public uint UidOf(long uuid) => _byUuid.TryGetValue(uuid, out var uid) ? uid : 0;

    /// <summary>The tracked (uid, uuid) pairs, snapshotted.</summary>
    public (uint Uid, long Uuid)[] Snapshot()
    {
        var all = new (uint, long)[_byUid.Count];
        var i = 0;
        foreach (var kv in _byUid) all[i++] = (kv.Key, kv.Value);
        return all;
    }

    /// <summary>The prefixes' question. True = <paramref name="speed"/> became 0 (the wish recorded for the release).</summary>
    public bool TrySubstitute(uint uid, int layer, ref float speed, float weight, int threadId)
    {
        if (!Armed || OwnWrite || !_byUid.TryGetValue(uid, out var uuid)) return false;
        if (threadId == 0 || threadId != MainThread)
        {
            if (speed > FreezeLedger.SpeedEpsilon) { Leaked++; Bump(_leakedBy, uuid); }
            return false;
        }
        Record(uid, new LayerWrite(layer, speed, weight));
        if (speed > FreezeLedger.SpeedEpsilon) { Held++; Bump(_heldBy, uuid); }
        speed = 0f;
        return true;
    }

    /// <summary>What the release writes for <paramref name="uid"/>, in order, and forgets its wishes: the whole model at
    /// <paramref name="controllerSpeed"/> (weight 1, as the controller's own <c>set_Speed</c>), then each layer's latest
    /// wish since the game's last whole-model write.</summary>
    public List<LayerWrite> TakeReleasePlan(uint uid, float controllerSpeed)
    {
        var plan = new List<LayerWrite> { new(WholeModel, controllerSpeed, 1f) };
        if (_wanted.Remove(uid, out var wishes)) plan.AddRange(wishes);
        return plan;
    }

    /// <summary>Releases <paramref name="uuid"/>'s model and stops gating it. When <paramref name="liveControllerSpeed"/>
    /// confirms the uid is still this entity's live model (it answers that model's controller speed; null = gone, recycled
    /// or unreadable — nothing is written), the release plan (<see cref="TakeReleasePlan"/>) goes through
    /// <paramref name="write"/>. The uid is forgotten either way, even when a callback throws (the throw is the caller's).
    /// Returns the writes issued, or −1 when the entity was not tracked.</summary>
    public int Release(long uuid, Func<uint, long, float?> liveControllerSpeed, Action<uint, LayerWrite> write)
    {
        if (!_byUuid.TryGetValue(uuid, out var uid)) return -1;
        try
        {
            if (liveControllerSpeed(uid, uuid) is not float controller) return 0;
            var plan = TakeReleasePlan(uid, controller);
            foreach (var w in plan) write(uid, w);
            return plan.Count;
        }
        finally { Forget(uid); }
    }

    public int HeldFor(long uuid) => _heldBy.TryGetValue(uuid, out var n) ? n : 0;

    public int LeakedFor(long uuid) => _leakedBy.TryGetValue(uuid, out var n) ? n : 0;

    private void Record(uint uid, LayerWrite write)
    {
        if (!_wanted.TryGetValue(uid, out var list)) _wanted[uid] = list = new List<LayerWrite>();
        if (write.Layer < 0) { list.Clear(); return; }   // whole model: earlier per-layer wishes are overwritten
        for (var i = list.Count - 1; i >= 0; i--)   // indexed: no closure on the per-play path
            if (list[i].Layer == write.Layer) list.RemoveAt(i);
        list.Add(write);
    }

    private static void Bump(Dictionary<long, int> counts, long uuid) => counts[uuid] = counts.TryGetValue(uuid, out var n) ? n + 1 : 1;
}
