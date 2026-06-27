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
    /// Updates that are due, auto-expires stale ramps, then recomputes the master rate.
    /// Do NOT register/unregister plugins from inside an Update callback raised here.</summary>
    public void Beat(float masterDt)
    {
        for (var i = 0; i < _entries.Count; i++)
        {
            var e = _entries[i];
            ExpireStaleRamps(e, masterDt);
            var rate = EffectiveRate(e);
            if (e.Gate.Crossed(masterDt, rate))
                e.RaiseUpdate?.Invoke(e.Gate.LastDt);
        }
        Recompute();   // auto-expired ramps may have lowered the needed master rate
    }

    private void ExpireStaleRamps(Entry e, float masterDt)
    {
        for (var i = e.Ramps.Count - 1; i >= 0; i--)
        {
            var r = e.Ramps[i];
            r.Elapsed += masterDt;
            if (r.Elapsed < _maxHoldSeconds) continue;
            _log?.Invoke($"[TickScheduler] auto-released {r.Hz}Hz ramp on '{e.Guid}' after {_maxHoldSeconds:0}s (leaked scope?)");
            r.MarkReleased();
            e.Ramps.RemoveAt(i);
        }
    }
}
