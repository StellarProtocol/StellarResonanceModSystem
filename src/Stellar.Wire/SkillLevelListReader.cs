using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Wire;

/// <summary>
/// Pure parser for the <c>AttrSkillLevelIdList</c> attribute (<c>EAttrType</c>=116)
/// carried on the combat wire's <c>AttrCollection</c>. The attribute's raw bytes
/// are a repeated <c>SkillLevelInfo</c>:
/// <code>
///   message SkillLevelInfo {
///     int32 skill_id      = 1;
///     int32 current_level = 2;
///     int32 remodel_level = 3;  // Tier
///   }
/// </code>
///
/// <para>
/// The repeated framing observed on the wire is UNCONFIRMED, so this parser
/// handles two plausible encodings defensively:
/// </para>
/// <list type="number">
///   <item>A bare sequence of length-delimited <c>SkillLevelInfo</c> sub-messages
///         at field 1 — i.e. <c>repeated SkillLevelInfo list = 1;</c>.</item>
///   <item>A single wrapper message that itself holds the repeated field at some
///         field number — handled transparently because the top-level loop reads
///         tags and descends into every length-delimited field, treating each as
///         a candidate <c>SkillLevelInfo</c>.</item>
/// </list>
///
/// <para>
/// On malformed input the parser yields whatever parsed cleanly up to the fault
/// (never throws), matching the defensive Try* convention used across the wire
/// parsers. A sub-message that doesn't carry a non-zero skill id is dropped.
/// </para>
/// </summary>
public static class SkillLevelListReader
{
    /// <summary>
    /// Decode the attr-116 raw payload into the equipped skill loadout. Returns
    /// an empty list (never null) when nothing parsed.
    /// </summary>
    public static IReadOnlyList<SkillLevel> Read(ReadOnlySpan<byte> payload)
    {
        var list = new List<SkillLevel>(8);
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out _, out var wire)) break;
            if (wire == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var inner)) break;
                if (TryReadSkillLevelInfo(inner, out var entry))
                    list.Add(entry);
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire))
            {
                break;
            }
        }
        return list;
    }

    // Parse one SkillLevelInfo sub-message {1:skill_id, 2:current_level, 3:remodel_level}.
    // Returns false only when no non-zero skill id was found (so non-loadout
    // sub-messages from an unexpected wrapper shape are skipped silently).
    private static bool TryReadSkillLevelInfo(ReadOnlySpan<byte> payload, out SkillLevel entry)
    {
        int skillId = 0, level = 0, tier = 0;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if (wire == 0 && WireProtocol.TryReadVarint(payload, ref pos, out var v))
            {
                switch (field)
                {
                    case 1: skillId = (int)v; break;
                    case 2: level   = (int)v; break;
                    case 3: tier    = (int)v; break;
                }
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire))
            {
                break;
            }
        }
        entry = new SkillLevel(skillId, level, tier);
        return skillId != 0;
    }
}
