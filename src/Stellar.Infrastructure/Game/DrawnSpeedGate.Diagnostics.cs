using System;
namespace Stellar.Infrastructure.Game;

/// <summary>Diagnostics-only queries and the counted decision for the combat-freeze evidence capture. The decision itself
/// is always <see cref="TrySubstitute"/>'s — this partial only classifies a call before handing it over, so the counted
/// prefix cannot drift from the plain one.</summary>
internal sealed partial class DrawnSpeedGate
{
    /// <summary><see cref="TrySubstitute"/>, counting the outcome in <paramref name="counters"/>: a substitution or a
    /// passthrough by reason (own write, off the main thread, untracked component, excluded entity). Off the main thread
    /// only the atomic counter moves — no table is touched there. Unarmed: no count (nothing is being sampled).</summary>
    public bool TrySubstituteCounted(IntPtr comp, ref float value, int threadId, FreezeDiagCounters counters)
    {
        if (_ledger is null) return false;
        if (threadId == 0 || threadId != MainThread) { counters.OffThread(); return false; }
        if (OwnWrite) { counters.HitPtr(comp, DiagSlot.GatePassOwn); return false; }
        if (!_byComp.TryGetValue(comp, out var t)) { counters.HitPtr(comp, DiagSlot.GatePassUntracked); return false; }
        var substituted = TrySubstitute(comp, ref value, threadId);
        counters.Hit(t.Uuid, substituted ? DiagSlot.GateSub : DiagSlot.GatePassExcluded);
        return substituted;
    }

    /// <summary>True when the gate tracks <paramref name="comp"/> as <paramref name="uuid"/>'s component.</summary>
    public bool TracksFor(IntPtr comp, long uuid) => _byComp.TryGetValue(comp, out var t) && t.Uuid == uuid;

    /// <summary>True when the gate tracks some component for <paramref name="uuid"/>.</summary>
    public bool TracksUuid(long uuid) => _byUuid.ContainsKey(uuid);
}
