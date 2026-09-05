using System;
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
    private EntityId _eid;

    internal int Count => _pairs.Count;

    /// <summary>Records one stored pair for <paramref name="eid"/>. A batch that already holds pairs for a
    /// DIFFERENT entity is a leak from an interrupted packet (the caller never reached its Flush) — it is
    /// discarded rather than mis-attributed to the new entity.</summary>
    internal void Add(EntityId eid, int attrId, long value)
    {
        if (_pairs.Count > 0 && eid != _eid) _pairs.Clear();
        _eid = eid;
        _pairs.Add(new AttrValue(attrId, value));
    }

    /// <summary>Returns the batched event for <paramref name="eid"/> and clears the batch either way. Returns
    /// null when the batch is empty, holds pairs for a DIFFERENT entity than <paramref name="eid"/>, or the
    /// entity is not a player.</summary>
    internal CombatEvent.EntityAttributesChanged? Flush(EntityId eid, long timestampMs)
    {
        CombatEvent.EntityAttributesChanged? ev = null;
        if (_pairs.Count > 0 && eid == _eid && eid.IsPlayer) ev = new CombatEvent.EntityAttributesChanged(timestampMs, eid, _pairs.ToArray());
        _pairs.Clear();
        return ev;
    }

    /// <summary>True when a decoded scalar should be stored. A genuine protobuf varint zero is exactly ONE
    /// raw byte (0x00) — it IS stored, because the rDPS sheet track regresses over step functions and an
    /// attribute that returns to 0 (e.g. an element damage bonus after its buff expires) must be able to step
    /// back down. A non-varint (string/packed) payload also decodes to 0 via <c>DecodedLong</c>'s safe-try,
    /// but is never exactly one 0x00 byte (an empty string is zero-length, a packed message is longer) — so
    /// junk non-varint payloads are still skipped.</summary>
    internal static bool IsStorableScalar(long decoded, ReadOnlySpan<byte> raw) =>
        decoded != 0 || (raw.Length == 1 && raw[0] == 0);
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
        _attrBatch.Add(eid, attrId, value);
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
