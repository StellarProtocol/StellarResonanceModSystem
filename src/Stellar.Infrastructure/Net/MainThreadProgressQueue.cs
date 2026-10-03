using System;
using System.Collections.Generic;
namespace Stellar.Infrastructure.Net;

/// <summary>
/// Hand-off of plugin-download progress (a repeating, "latest value wins" update) from a pool thread to the
/// main thread — <c>IPluginDownloads.DownloadAsync</c>'s <c>IProgress&lt;double&gt;</c> contract requires
/// delivery on the main thread only, never directly from the worker. Shaped like
/// <see cref="Stellar.Infrastructure.Rendering.ResumeQueue"/> (store on the pool thread, drain on the main
/// thread's tick) but for a repeating value instead of a one-shot completion: a slot is delivered on every
/// <see cref="Drain"/> while dirty (at most once per call) rather than exactly once ever. It is a SEPARATE,
/// independent queue — not layered onto <c>ResumeQueue</c> itself — driven from the SAME per-tick hook
/// (<c>Wiring.ServiceTick.RunGlobalRateWork</c>, right next to the frame grabber's resume drain), but
/// neither queue depends on the other's state; a stalled progress drain can never block a capture resume
/// or vice versa. Pure — unit-tested.
/// </summary>
internal sealed class MainThreadProgressQueue
{
    private readonly object _gate = new();
    private readonly HashSet<Slot> _dirty = new();

    /// <summary>Mints a fresh, single-use slot for one download's progress target. Never reused across
    /// downloads, so a drained slot needs no explicit unregister — it simply falls out of scope.</summary>
    public Slot CreateSlot(IProgress<double> target) => new(target);

    /// <summary>Any thread: stores the latest value for <paramref name="slot"/>, marking it dirty for the
    /// next <see cref="Drain"/> — unless the value is unchanged from what was last stored/delivered, in
    /// which case this is a no-op (no redundant delivery). A null slot (no progress requested) is a no-op.</summary>
    public void Report(Slot? slot, double value)
    {
        if (slot is null) return;
        lock (_gate)
        {
            if (slot.HasValue && slot.Value == value) return;
            slot.Value = value;
            slot.HasValue = true;
            _dirty.Add(slot);
        }
    }

    /// <summary>Main thread only: delivers each dirty slot's latest value exactly once, then forgets it.
    /// Returns how many were delivered (0 is the common case — cheap to call every tick).</summary>
    public int Drain()
    {
        List<Slot> ready;
        lock (_gate)
        {
            if (_dirty.Count == 0) return 0;
            ready = new List<Slot>(_dirty);
            _dirty.Clear();
        }
        foreach (var slot in ready) slot.Target.Report(slot.Value);
        return ready.Count;
    }

    /// <summary>Lock-free-ish check for the main-thread tick: is anything waiting to be delivered?</summary>
    public bool HasQueued { get { lock (_gate) return _dirty.Count > 0; } }

    /// <summary>One download's progress channel: the real target plus the latest undelivered value.</summary>
    internal sealed class Slot
    {
        internal readonly IProgress<double> Target;
        internal double Value;
        internal bool HasValue;
        internal Slot(IProgress<double> target) => Target = target;
    }
}
