using System;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Shared decoding of <c>BuffEffectLogicInfo { EBuffEffectLogicPbType EffectType = 1; bytes RawData = 2; }</c>
/// entries for the buff effect ids (BuffEffect field 5 and BuffInfo field 13). Port of resonance-logs-cn
/// decoder.rs <c>decode_play_effect_ids</c> / <c>decode_customize_values</c>, byte-for-byte the rules of the
/// Mechanic Callouts McBuffEffectPatch it replaces (Experiment) — including its two quirks, which matter:
/// <list type="bullet">
/// <item>A PlayEffect entry whose RawData is absent or carries no <c>effect_id</c> yields id <b>0</b> (proto3 elides
/// zero, the game's own parse reads it as 0). Tina's Wudi-Slash order is 0-indexed, so dropping it (as prost's
/// <c>Option</c> would) turns the first slasher into "?".</item>
/// <item>The <c>effect_id</c> probe succeeds only when the WHOLE buffer parses (field number 0, a group wire type or a
/// truncated field fails it), so a 1-/2-byte customize blob that isn't a valid message falls back to its raw
/// little-endian integer.</item>
/// </list>
/// Never throws, never allocates.
/// </summary>
internal static class BuffEffectLogicReader
{
    // EBuffEffectLogicPbType (zproto enum_e_buff_effect_logic_pb_type).
    public const int LogicPlayEffect = 0, LogicStopAll = 15, LogicAddBuff = 18, LogicBuffChange = 19;

    /// <summary>Reads one logic entry's EffectType (default 0 = PlayEffect) and RawData (empty when absent).
    /// False on malformed framing.</summary>
    public static bool TryReadEntry(ReadOnlySpan<byte> payload, out int effectType, out ReadOnlySpan<byte> raw, out bool hasRaw)
    {
        effectType = 0; raw = default; hasRaw = false;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) return false;
            switch ((field, wire))
            {
                case (1, 0): if (!WireProtocol.TryReadVarint(payload, ref pos, out var et)) return false; effectType = (int)et; break;
                case (2, 2): if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out raw)) return false; hasRaw = true; break;
                default: if (!WireProtocol.SkipField(payload, ref pos, wire)) return false; break;
            }
        }
        return true;
    }

    /// <summary><c>BuffEffectLogicPlayEffect { int32 effect_id = 1; }</c>: true only if the whole buffer parses; a
    /// missing field = 0 (last occurrence wins, like protobuf merge).</summary>
    public static bool TryDecodePlayEffectId(ReadOnlySpan<byte> raw, out int id)
    {
        id = 0;
        long value = 0;
        int pos = 0;
        while (pos < raw.Length)
        {
            if (!WireProtocol.TryReadTag(raw, ref pos, out var field, out var wire) || field == 0) return false;
            if (wire == 0)
            {
                if (!WireProtocol.TryReadVarint(raw, ref pos, out var v)) return false;
                if (field == 1) value = (long)v;
            }
            else if (!WireProtocol.SkipField(raw, ref pos, wire)) return false;
        }
        id = (int)value;
        return true;
    }

    /// <summary>One customize value (<c>decode_customize_value</c>): a non-zero PlayEffect <c>effect_id</c>, else a
    /// 1-byte value, else a 2-byte little-endian <c>i16</c>. False when none applies.</summary>
    public static bool TryDecodeCustomizeValue(ReadOnlySpan<byte> raw, out int value)
    {
        if (TryDecodePlayEffectId(raw, out value) && value != 0) return true;
        if (raw.Length == 1) { value = raw[0]; return true; }
        if (raw.Length == 2) { value = (short)(raw[0] | (raw[1] << 8)); return true; }
        value = 0;
        return false;
    }

    /// <summary>Adds a logic entry's PlayEffect id (PlayEffect entries only) to <paramref name="ids"/>. Malformed
    /// entries are ignored (they never fail the enclosing message — the old reader skipped these fields).</summary>
    public static void AddPlayEffectId(ReadOnlySpan<byte> entry, ref IdBuffer ids)
    {
        if (TryReadEntry(entry, out var type, out var raw, out _) && type == LogicPlayEffect
            && TryDecodePlayEffectId(raw, out int id))
            ids.Add(id);
    }
}

/// <summary>
/// Growable int list over a caller's <c>stackalloc</c> span: no heap allocation until it outgrows the span (raid
/// mechanics carry ≤4 ids per effect), and <see cref="ToArrayOrNull"/> allocates the one result array only when
/// there are ids.
/// </summary>
internal ref struct IdBuffer
{
    private Span<int> _buf;
    public int Count { get; private set; }

    public IdBuffer(Span<int> initial) { _buf = initial; Count = 0; }

    public void Add(int value)
    {
        if (Count == _buf.Length)
        {
            var grown = new int[Math.Max(8, _buf.Length * 2)];
            _buf.CopyTo(grown);
            _buf = grown;
        }
        _buf[Count++] = value;
    }

    public void Clear() => Count = 0;

    public int[]? ToArrayOrNull() => Count == 0 ? null : _buf.Slice(0, Count).ToArray();
}
