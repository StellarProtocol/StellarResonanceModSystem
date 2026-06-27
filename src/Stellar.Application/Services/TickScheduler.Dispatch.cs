using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

internal sealed partial class TickScheduler
{
    /// <summary>A held dynamic-rate request. Disposing removes it and recomputes the master rate.</summary>
    internal sealed class RampScope : IUpdateRateScope
    {
        private readonly TickScheduler _scheduler;
        private readonly Entry _entry;
        private bool _disposed;

        public RampScope(TickScheduler scheduler, Entry entry, int hz)
        {
            _scheduler = scheduler;
            _entry = entry;
            Hz = hz;
        }

        public int Hz { get; }
        public double Elapsed;   // accumulated by Beat; force-released past maxHoldSeconds
        public bool IsActive => !_disposed;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_entry.Ramps.Remove(this)) _scheduler.Recompute();
        }

        // Force-release path used by Beat's auto-expire (already removed from the list there).
        internal void MarkReleased() => _disposed = true;
    }

    public IUpdateRateScope RequestDynamicRate(string guid, int hz)
    {
        if (!_byGuid.TryGetValue(guid, out var e) || !e.AllowSelfControl)
            return InertUpdateRateScope.Instance;
        var scope = new RampScope(this, e, PerfControls.ClampRate(hz));
        e.Ramps.Add(scope);
        Recompute();
        return scope;
    }

    /// <summary>Drive once per master beat. Advances every plugin's accumulator and fires the
    /// Updates that are due, auto-expires stale ramps, then recomputes the master rate ONLY if a
    /// ramp expired (the only master-rate change originating inside the beat). A plugin may raise
    /// its own rate from within its Update; that recomputes immediately, so the master-rate change
    /// (and the host re-rate) can fire mid-dispatch — this is safe. Structural register/unregister
    /// requested during dispatch is deferred to the end of the beat.</summary>
    public void Beat(float masterDt)
    {
        var expired = false;
        _inBeat = true;
        try
        {
            for (var i = 0; i < _entries.Count; i++)
            {
                var e = _entries[i];
                expired |= ExpireStaleRamps(e, masterDt);
                var rate = EffectiveRate(e);
                if (e.Gate.Crossed(masterDt, rate))
                    e.RaiseUpdate?.Invoke(e.Gate.LastDt);
            }
        }
        finally
        {
            _inBeat = false;
            DrainPendingUnregister();
        }
        if (expired) Recompute();   // auto-expired ramps may have lowered the needed master rate
    }

    // Returns true if at least one ramp on this entry was auto-released this beat. Empty-fast.
    private bool ExpireStaleRamps(Entry e, float masterDt)
    {
        if (e.Ramps.Count == 0) return false;
        if (e.AllowSustained) return false;   // user opted this plugin into indefinite holds — no leak-guard
        var any = false;
        for (var i = e.Ramps.Count - 1; i >= 0; i--)
        {
            var r = e.Ramps[i];
            r.Elapsed += masterDt;
            if (r.Elapsed < _maxHoldSeconds) continue;
            _log?.Invoke($"[TickScheduler] auto-released {r.Hz}Hz ramp on '{e.Guid}' after {_maxHoldSeconds:0}s (leaked scope?)");
            r.MarkReleased();
            e.Ramps.RemoveAt(i);
            any = true;
        }
        return any;
    }
}
