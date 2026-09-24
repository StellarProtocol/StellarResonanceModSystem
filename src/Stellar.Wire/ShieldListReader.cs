using System;

namespace Stellar.Wire;

/// <summary>
/// Pure parser for the <c>AttrShieldList</c> attribute (<c>EAttrType</c>=60050)
/// carried on the combat wire's <c>AttrCollection</c>. Unlike <c>AttrHp</c> /
/// <c>AttrMaxHp</c> (scalar Int64 attrs) this is a LIST attr — the payload is a
/// repeated shield-entry message:
/// <code>
///   message ShieldList { repeated ShieldEntry entries = 1; }
///   message ShieldEntry {
///     uint64 f1 = 1;   // (source/slot bookkeeping — unused here)
///     uint64 f2 = 2;   // (unused here)
///     uint64 cur = 3;  // CURRENT shield of this entry  ← summed
///     uint64 max = 4;  // MAX shield of this entry
///     uint64 f5 = 5;   // (unused here)
///   }
/// </code>
///
/// <para>
/// <see cref="Read"/> returns the TOTAL current shield = Σ (field 3) over every
/// entry. Mirrors the defensive idiom of <see cref="SkillLevelListReader"/> /
/// <see cref="AttrFashionDataReader"/>: the top-level loop descends into every
/// length-delimited field and reads its field-3, so both a bare
/// <c>repeated ShieldEntry = 1</c> and a single wrapper message that itself holds
/// the repeated field are handled. On empty or malformed input it returns
/// whatever summed cleanly up to the fault (never throws, never negative), so 0
/// is the natural "no shield" result.
/// </para>
/// </summary>
public static class ShieldListReader
{
    /// <summary>
    /// Decode the attr-60050 raw payload into the total CURRENT shield (Σ of each
    /// entry's field 3). Returns 0 when the payload is empty or nothing parsed.
    /// </summary>
    public static long Read(ReadOnlySpan<byte> payload)
    {
        long total = 0;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out _, out var wire)) break;
            if (wire == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var entry)) break;
                total += ReadEntryCurrentShield(entry);
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire))
            {
                break;
            }
        }
        return total;
    }

    // One ShieldEntry sub-message → its field 3 (current shield). Field 3 appears once
    // per entry; anything else is skipped. Returns 0 when field 3 is absent.
    private static long ReadEntryCurrentShield(ReadOnlySpan<byte> payload)
    {
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if (field == 3 && wire == 0)
            {
                if (!WireProtocol.TryReadVarint(payload, ref pos, out var v)) break;
                return (long)v;
            }
            if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }
        return 0;
    }
}
