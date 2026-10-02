using System.Collections.Generic;
using System.Reflection;
namespace Stellar.Infrastructure.Game;

/// <summary>The animation REQUESTS that still change a pose while the clock is stopped (owner report on the TEST window
/// 2026-10-02, run 10: "someone animated movement when freeze"; measured run 11, devkit recon free-camera-recon.md § Run 11):
/// at <c>Time.timeScale = 0</c> the ECS animator's clock is frozen — over a 20 s pause, 0 animation events (loop end / state
/// enter) on every entity in range, against a loop end every 1–2 s unpaused — but the game's own logic keeps REQUESTING new
/// states for moving entities: a monster walking under server control made 9 <c>PlayBaseState</c>, 5 <c>PlayUpperState</c>
/// and 5 <c>playManualClip</c> calls in 20 s paused. Each request switches the model to another state's pose, so a mover
/// "animates" in stop-motion in place. While armed, <see cref="Decide"/> answers the gated controller entry points: a call
/// on a TRACKED controller (an entity of the freeze press — never the local player, their own mount, a posed copy or an
/// NPC model of posing, which are other controllers) is DEFERRED, keeping only the latest call per controller and layer;
/// <see cref="TakeReplay"/> hands the kept calls back, in arrival order, to be re-issued on unfreeze so every model resumes in
/// the state the game last asked for. A controller is pooled by the game (<c>ECSAnimController.Rent/Return</c>), so a tracked
/// one is forgotten when its entity leaves (<see cref="Untrack"/>) and a replay is re-checked by the caller against the entity
/// that serves it now. Off the main thread every call runs. Bounded at <see cref="Cap"/> kept calls. Pure (unit-tested).</summary>
internal sealed class AnimRequestGate
{
    internal const int Cap = 512;

    /// <summary>The animator layer a request plays on (the game's <c>EAnimLayer</c>: Base 0, Upper 1, Additive 2).</summary>
    internal enum Layer { Base = 0, Upper = 1, Additive = 2 }

    /// <summary>One kept request: the controller (native pointer + the wrapper to call), its entity, the layer, the game method
    /// and a copy of its arguments.</summary>
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

    public void Arm()
    {
        Armed = true;
        Deferred = 0;
        _tracked.Clear();
        _kept.Clear();
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
        _kept.RemoveAll(p => p.Controller == found);
    }

    /// <summary>The gated entry point's question: true = the game's call runs now. False = deferred (the latest call per
    /// controller and layer is kept, its arguments copied).</summary>
    public bool Decide(in Request r)
    {
        var controller = r.Controller;
        if (!Armed || Replaying || r.OffMainThread || controller == 0 || !_tracked.TryGetValue(controller, out var uuid)) return true;
        var layer = r.Layer;
        var i = _kept.FindIndex(p => p.Controller == controller && p.Layer == layer);
        if (i >= 0) _kept.RemoveAt(i);                    // a newer request replaces the kept one (and moves to the end)
        else if (_kept.Count >= Cap) return true;        // full: the game plays it now
        _kept.Add(new Pending(controller, r.Instance, uuid, layer, r.Method, (object?[])r.Args.Clone()));
        Deferred++;
        return false;
    }

    /// <summary>Stops gating and hands back every kept call in arrival order (emptied).</summary>
    public List<Pending> TakeReplay()
    {
        Armed = false;
        var batch = new List<Pending>(_kept);
        _kept.Clear();
        _tracked.Clear();
        return batch;
    }
}
