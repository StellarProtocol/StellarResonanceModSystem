using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Collects the scalar attrs ONE packet stored for ONE entity and turns them into a single
/// <see cref="CombatEvent.EntityAttributesChanged"/> (rDPS sheet track, spec § 6.1). Pure so it is testable
/// without the IL2CPP-backed probe; the probe feeds it from <c>CaptureEntityDetail</c> (right AFTER the
/// sink write, so "event fired ⇒ GetAttributes already carries the change") and flushes it once per
/// entity per packet with that packet's receive stamp. Player entities only — a monster's scalars are
/// stored (inspector) but never announced.
/// </summary>
internal sealed class AttrChangeBatch
{
    private readonly List<AttrValue> _pairs = new(16);

    internal int Count => _pairs.Count;

    internal void Add(int attrId, long value) => _pairs.Add(new AttrValue(attrId, value));

    internal CombatEvent.EntityAttributesChanged? Flush(EntityId eid, long timestampMs)
    {
        if (_pairs.Count == 0) return null;
        CombatEvent.EntityAttributesChanged? ev = null;
        if (eid.IsPlayer) ev = new CombatEvent.EntityAttributesChanged(timestampMs, eid, _pairs.ToArray());
        _pairs.Clear();
        return ev;
    }
}

internal sealed partial class PandaCombatStubProbe
{
    // Receive-thread only (one packet at a time), reused across packets — see AttrChangeBatch.
    private readonly AttrChangeBatch _attrBatch = new();

    /// <summary>The ONE place a scalar attr is stored: writes the sink, then records the pair for this packet's
    /// EntityAttributesChanged. Every scalar write in the probe goes through here so the event payload can never
    /// drift from what GetAttributes serves.</summary>
    private void StoreScalarAttr(EntityId eid, int attrId, long value)
    {
        _sink.SetEntityAttribute(eid, attrId, value);
        _attrBatch.Add(attrId, value);
    }

    /// <summary>Called once per entity per attr-carrying packet, AFTER the loop that stored its scalars.</summary>
    private void FlushAttrBatch(EntityId eid, long timestampMs)
    {
        var ev = _attrBatch.Flush(eid, timestampMs);
        if (ev is null) return;
        _sink.EnqueueEvent(ev);
        DiagAttrEvent(eid, ev.Attrs.Count, timestampMs);
    }
}
