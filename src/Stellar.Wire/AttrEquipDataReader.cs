using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Wire;

/// <summary>Pure parser for the wire <c>AttrEquipData</c> attribute: a TAGGED
/// <c>repeated EquipNine{ slot=1, equip_id=2 } = 1</c> list (live-confirmed via the 2026-06-13
/// <c>[EquipDiag]</c> hex dumps — every appear carries the full ~11-slot list as
/// <c>0A len 08 slot 10 id …</c>). A bare tag-less <c>len+EquipNine</c> sequence (the shape the
/// ZDPS reference decodes on an older build) is kept as a defensive fallback — misreading the
/// tagged form as bare shredded the list to a single junk item (the "1-item gear grid").
/// Returns one <see cref="EquipNineEntry"/> per slot.</summary>
public static class AttrEquipDataReader
{
    /// <summary>Decode the <c>AttrEquipData</c> raw payload into a list of equipped slots.
    /// Returns an empty list (never null) when nothing parsed.</summary>
    public static IReadOnlyList<EquipNineEntry> Read(ReadOnlySpan<byte> payload)
    {
        var list = new List<EquipNineEntry>(11);
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if (field == 1 && wire == 2 && WireProtocol.TryReadLengthDelimited(payload, ref pos, out var inner))
            {
                if (TryReadEquipNine(inner, out var entry)) list.Add(entry);
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }
        if (list.Count > 0) return list;

        // Fallback: bare len-prefixed sequence without tags.
        pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var inner)) break;
            if (TryReadEquipNine(inner, out var entry)) list.Add(entry);
        }
        return list;
    }

    private static bool TryReadEquipNine(ReadOnlySpan<byte> payload, out EquipNineEntry entry)
    {
        int slot = 0, itemId = 0, pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if (wire == 0 && WireProtocol.TryReadVarint(payload, ref pos, out var v))
            {
                if (field == 1) slot = (int)v;
                else if (field == 2) itemId = (int)v;
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }
        entry = new EquipNineEntry(slot, itemId);
        return itemId != 0;
    }
}
