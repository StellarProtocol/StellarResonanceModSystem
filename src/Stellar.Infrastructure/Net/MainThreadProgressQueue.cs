using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
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
    private readonly IPluginLog _log;

    public MainThreadProgressQueue(IPluginLog log) => _log = log;

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

    /// <summary>
    /// Called exactly once, when a download completes (fix round 2 — N2): on success, stamps
    /// <paramref name="finalValue"/> (e.g. 1.0) as the one value left to deliver, superseding anything still
    /// pending; on failure (<paramref name="finalValue"/> null), drops the slot's pending value outright — a
    /// finished download must never report progress again, even a stale intermediate value that arrived just
    /// before it failed. A null slot is a no-op.
    /// </summary>
    public void Finish(Slot? slot, double? finalValue)
    {
        if (slot is null) return;
        lock (_gate)
        {
            if (finalValue is { } v)
            {
                slot.Value = v;
                slot.HasValue = true;
                _dirty.Add(slot);
            }
            else
            {
                _dirty.Remove(slot);
            }
        }
    }

    /// <summary>Main thread only: delivers each dirty slot's latest value exactly once, then forgets it. A
    /// target that throws is logged (fix round 2 — N1) and skipped — it never stops delivery to the rest, nor
    /// escapes this call. Returns how many slots were drained (0 is the common case — cheap every tick).</summary>
    public int Drain()
    {
        List<Slot> ready;
        lock (_gate)
        {
            if (_dirty.Count == 0) return 0;
            ready = new List<Slot>(_dirty);
            _dirty.Clear();
        }
        foreach (var slot in ready)
        {
            try { slot.Target.Report(slot.Value); }
            catch (Exception ex) { _log.Warning($"[PluginDownloads] progress callback threw: {ex.GetType().Name}: {ex.Message}"); }
        }
        return ready.Count;
    }

    /// <summary>One download's progress channel: the real target plus the latest undelivered value.</summary>
    internal sealed class Slot
    {
        internal readonly IProgress<double> Target;
        internal double Value;
        internal bool HasValue;
        internal Slot(IProgress<double> target) => Target = target;
    }
}
