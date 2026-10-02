using System;
using System.Collections.Generic;
using System.Reflection;
namespace Stellar.Infrastructure.Game;

/// <summary>The animation REQUESTS that still change a pose while the clock is stopped (owner report on the TEST window
/// 2026-10-02, run 10: "someone animated movement when freeze"; measured run 11, devkit recon free-camera-recon.md § Run 11):
/// at <c>Time.timeScale = 0</c> the ECS animator's clock is frozen — over a 20 s pause, 0 animation events (loop end / state
/// enter) on every entity in range, against a loop end every 1–2 s unpaused — but the game's own logic keeps REQUESTING new
/// states for moving entities: a monster walking under server control made 9 <c>PlayBaseState</c>, 5 <c>PlayUpperState</c>
/// and 5 <c>playManualClip</c> calls in 20 s paused. Each request switches the model to another state's pose, so a mover
/// "animates" in stop-motion in place. While armed, <see cref="Defers"/> answers the gated controller entry points: a call
/// on a TRACKED controller (an entity of the freeze press — never the local player, their own mount, a posed copy or an
/// NPC model of posing, which are other controllers) is DEFERRED, keeping only the latest call per controller and layer
/// (<see cref="Keep"/>); <see cref="TakeReplay"/> hands the kept calls back, in arrival order, to be re-issued on unfreeze so
/// every model resumes in the state the game last asked for. A controller is pooled by the game
/// (<c>ECSAnimController.Rent/Return</c>), so a tracked one is forgotten when its entity leaves (<see cref="Untrack"/>) and
/// <see cref="TakeReplay"/> keeps a call only while its entity still serves the SAME controller (qa M-10: a re-rented
/// controller is never driven). A press that does not know the local player tracks nobody (<see cref="ArmFor"/>, qa M-7).
/// Off the main thread every call runs. Bounded at <see cref="Cap"/> kept calls. Allocation-free until a call is deferred
/// (perf review 2026-10-03). Pure (unit-tested).</summary>
internal sealed class AnimRequestGate
{
    internal const int Cap = 512;

    /// <summary>The animator layer a request plays on (the game's <c>EAnimLayer</c>: Base 0, Upper 1, Additive 2).</summary>
    internal enum Layer { Base = 0, Upper = 1, Additive = 2 }

    /// <summary>One kept request: the controller (native pointer + the wrapper to call), its entity, the layer, the game method
    /// and its arguments.</summary>
    internal readonly record struct Pending(nint Controller, object Instance, long Uuid, Layer Layer, MethodBase Method, object?[] Args);

    /// <summary>One gated call as the entry point sees it: the controller (native pointer + its wrapper), the layer, the game
    /// method, its arguments, and whether it came from another thread than the main one.</summary>
    internal readonly record struct Request(nint Controller, object Instance, Layer Layer, MethodBase Method, object?[] Args, bool OffMainThread);

    private readonly Dictionary<nint, long> _tracked = new();
    private readonly List<Pending> _kept = new();

    public bool Armed { get; private set; }

    /// <summary>True while <see cref="TakeReplay"/>'s calls are re-issued: those always run.</summary>
    public bool Replaying { get; set; }

    public int Tracked => _tracked.Count;

    public int Kept => _kept.Count;

    /// <summary>Calls deferred this freeze (diagnostics).</summary>
    public int Deferred { get; private set; }

    /// <summary>Kept calls the last <see cref="TakeReplay"/> dropped because their entity serves another controller now.</summary>
    public int Stale { get; private set; }

    public void Arm()
    {
        Armed = true;
        Deferred = Stale = 0;
        _tracked.Clear();
        _kept.Clear();
    }

    /// <summary>The press: arms and tracks the live controller of each of <paramref name="ids"/> (<paramref name="controllerOf"/>,
    /// 0 = none) — or nobody when the press does not know the local player (<see cref="FreezeTargets.MayHoldAny"/>: the
    /// player is then still in <paramref name="ids"/>, and gating their controller would freeze their own movement pose).</summary>
    public void ArmFor(FreezeLedger ledger, List<long> ids, Func<long, nint> controllerOf)
    {
        Arm();
        if (!FreezeTargets.MayHoldAny(ledger)) return;
        foreach (var uuid in ids) Track(controllerOf(uuid), uuid);
    }

    /// <summary>Gates <paramref name="controller"/> (entity <paramref name="uuid"/>) for this freeze. 0 is ignored.</summary>
    public void Track(nint controller, long uuid)
    {
        if (controller != 0 && uuid != 0) _tracked[controller] = uuid;
    }

    /// <summary>The entity <paramref name="uuid"/> left: its controller (pooled, maybe re-rented next) is no longer gated and
    /// its kept calls are dropped.</summary>
    public void Untrack(long uuid)
    {
        if (uuid == 0 || _tracked.Count == 0) return;
        nint found = 0;
        foreach (var kv in _tracked)
            if (kv.Value == uuid) { found = kv.Key; break; }
        if (found == 0) return;
        _tracked.Remove(found);
        for (var i = _kept.Count - 1; i >= 0; i--)
            if (_kept[i].Controller == found) _kept.RemoveAt(i);
    }

    /// <summary>The gated entry point's first question, allocation-free: true = defer this call (the caller then builds its
    /// argument copy and hands it to <see cref="Keep"/>); false = the game's call runs now — not armed, replaying, off the
    /// main thread, an untracked controller, or the queue full with no kept call to replace.</summary>
    public bool Defers(nint controller, Layer layer, bool offMainThread)
    {
        if (!Armed || Replaying || offMainThread || controller == 0 || !_tracked.ContainsKey(controller)) return false;
        return _kept.Count < Cap || IndexOf(controller, layer) >= 0;
    }

    /// <summary>Keeps a deferred call (after <see cref="Defers"/> said true): a newer request replaces the kept one for that
    /// controller and layer and moves to the end. <paramref name="args"/> is owned by the gate from here.</summary>
    public void Keep(nint controller, object instance, Layer layer, MethodBase method, object?[] args)
    {
        if (!_tracked.TryGetValue(controller, out var uuid)) return;
        var i = IndexOf(controller, layer);
        if (i >= 0) _kept.RemoveAt(i);
        _kept.Add(new Pending(controller, instance, uuid, layer, method, args));
        Deferred++;
    }

    /// <summary><see cref="Defers"/> + <see cref="Keep"/> over a call whose arguments the caller still owns (copied). True =
    /// the game's call runs now.</summary>
    public bool Decide(in Request r)
    {
        if (!Defers(r.Controller, r.Layer, r.OffMainThread)) return true;
        Keep(r.Controller, r.Instance, r.Layer, r.Method, (object?[])r.Args.Clone());
        return false;
    }

    /// <summary>Stops gating and hands back, in arrival order, every kept call whose entity still serves the same controller
    /// (<paramref name="controllerNow"/>: the entity's live controller now, 0 = none). The others are counted in
    /// <see cref="Stale"/> and dropped. Emptied either way.</summary>
    public List<Pending> TakeReplay(Func<long, nint> controllerNow)
    {
        Armed = false;
        var batch = new List<Pending>(_kept.Count);
        foreach (var p in _kept)
        {
            nint now;
            try { now = controllerNow(p.Uuid); }
            catch { now = 0; }
            if (now == p.Controller) batch.Add(p);
            else Stale++;
        }
        _kept.Clear();
        _tracked.Clear();
        return batch;
    }

    private int IndexOf(nint controller, Layer layer)
    {
        for (var i = 0; i < _kept.Count; i++)
            if (_kept[i].Controller == controller && _kept[i].Layer == layer) return i;
        return -1;
    }
}
