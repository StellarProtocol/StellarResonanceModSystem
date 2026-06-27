using System;
using System.Collections.Generic;
using Stellar.Abstractions.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// Computes the framework's master tick rate as <c>max(global, every plugin's effective rate)</c>
/// (clamped to the supported range) and gates each plugin's Update to its own rate via a
/// <see cref="RateGate"/>. A plugin's effective rate = <c>max(static config ?? global, held dynamic
/// ramp if permitted)</c>. Pure: no Unity/BepInEx. The host re-rates the live ticker on
/// <see cref="MasterRateChanged"/>. Dynamic ramps + per-beat dispatch live in the
/// <c>TickScheduler.Dispatch.cs</c> partial.
/// </summary>
internal sealed partial class TickScheduler
{
    internal sealed class Entry
    {
        public string Guid = "";
        public int? StaticRateHz;
        public bool AllowSelfControl;
        public bool AllowSustained;
        public readonly List<RampScope> Ramps = new();
        public readonly RateGate Gate = new();
        public Action<float>? RaiseUpdate;
    }

    private readonly Dictionary<string, Entry> _byGuid = new();
    private readonly List<Entry> _entries = new();   // iterated per beat; kept in sync with _byGuid
    private readonly double _maxHoldSeconds;
    private readonly Action<string>? _log;
    private int _globalRateHz = PerfControls.DefaultUpdateRateHz;
    private bool _inBeat;
    private readonly List<string> _pendingUnregister = new();

    public TickScheduler(double maxHoldSeconds = 10.0, Action<string>? log = null)
    {
        _maxHoldSeconds = maxHoldSeconds;
        _log = log;
        MasterRateHz = _globalRateHz;
    }

    public int MasterRateHz { get; private set; }
    public event Action<int>? MasterRateChanged;

    public void SetGlobalRate(int hz)
    {
        _globalRateHz = PerfControls.ClampRate(hz);
        Recompute();
    }

    public void ConfigurePlugin(string guid, int? staticRateHz, bool allowSelfControl, bool allowSustained = false)
    {
        var e = GetOrAdd(guid);
        e.StaticRateHz = staticRateHz is > 0 ? PerfControls.ClampRate(staticRateHz.Value) : null;
        e.AllowSelfControl = allowSelfControl;
        e.AllowSustained = allowSustained;
        Recompute();
    }

    public void RegisterPlugin(string guid, Action<float> raiseUpdate)
        => GetOrAdd(guid).RaiseUpdate = raiseUpdate;

    public void UnregisterPlugin(string guid)
    {
        if (_inBeat) { _pendingUnregister.Add(guid); return; }
        RemoveEntry(guid);
    }

    private void RemoveEntry(string guid)
    {
        if (!_byGuid.TryGetValue(guid, out var e)) return;
        _byGuid.Remove(guid);
        _entries.Remove(e);
        Recompute();
    }

    // Applies any unregister requested during dispatch. Allocation-free when nothing was deferred.
    private void DrainPendingUnregister()
    {
        if (_pendingUnregister.Count == 0) return;
        for (var i = 0; i < _pendingUnregister.Count; i++) RemoveEntry(_pendingUnregister[i]);
        _pendingUnregister.Clear();
    }

    public int EffectiveRateFor(string guid)
        => _byGuid.TryGetValue(guid, out var e) ? EffectiveRate(e) : _globalRateHz;

    private Entry GetOrAdd(string guid)
    {
        if (_byGuid.TryGetValue(guid, out var e)) return e;
        e = new Entry { Guid = guid };
        _byGuid[guid] = e;
        _entries.Add(e);
        return e;
    }

    private int EffectiveRate(Entry e)
    {
        var baseRate = e.StaticRateHz ?? _globalRateHz;
        var dyn = 0;
        if (e.AllowSelfControl)
            for (var i = 0; i < e.Ramps.Count; i++)
                if (e.Ramps[i].Hz > dyn) dyn = e.Ramps[i].Hz;
        return baseRate >= dyn ? baseRate : dyn;
    }

    private void Recompute()
    {
        var master = _globalRateHz;
        for (var i = 0; i < _entries.Count; i++)
        {
            var r = EffectiveRate(_entries[i]);
            if (r > master) master = r;
        }
        master = PerfControls.ClampRate(master);
        if (master == MasterRateHz) return;
        MasterRateHz = master;
        MasterRateChanged?.Invoke(master);
    }
}
