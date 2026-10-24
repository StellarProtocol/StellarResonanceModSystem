using System;

namespace Stellar.Wire;

/// <summary>
/// <c>BasicShowInfo</c> parser — the sender-identity sub-message carried inside a
/// <c>ChitChatMsg</c> (see <see cref="TryParseChitChatMsg"/>). Split out of
/// <c>WireProtocol.ChitChat.cs</c> to keep that file under the 500-LoC cap.
/// </summary>
public static partial class WireProtocol
{
    /// <summary>
    /// Parsed result from <see cref="TryParseBasicShowInfo"/>.
    /// Public because <see cref="TryParseBasicShowInfo"/> is a public plugin-facing API.
    /// </summary>
    public readonly record struct BasicShowInfoResult(
        string Name,
        long CharId,
        int Gender,
        int Level,
        bool IsNewbie);

    /// <summary>
    /// Parse <c>BasicShowInfo</c> per <c>stru_basic_show_info.proto</c>: extracts
    /// char_id (1), name (2), gender (3), level (5), is_newbie (8). body_size,
    /// talent pool, union hunt idx, and is_backflow are skipped (no plugin
    /// consumers yet).
    /// </summary>
    public static BasicShowInfoResult TryParseBasicShowInfo(ReadOnlySpan<byte> data)
    {
        var fields = new BasicShowInfoState { Name = string.Empty };
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadTag(data, ref pos, out var field, out var wireType)) break;
            if (!ReadBasicShowInfoFields(data, ref pos, ref fields, field, wireType)) break;
        }
        return new BasicShowInfoResult(
            Name: fields.Name,
            CharId: fields.CharId,
            Gender: fields.Gender,
            Level: fields.Level,
            IsNewbie: fields.IsNewbie);
    }

    /// <summary>
    /// Per-tag handler for <see cref="TryParseBasicShowInfo"/>. Returns false on
    /// malformed input — the outer loop short-circuits and preserves whatever
    /// fields were extracted before the failure (matches the original method's
    /// tolerant semantics).
    /// </summary>
    private static bool ReadBasicShowInfoFields(
        ReadOnlySpan<byte> data,
        ref int pos,
        ref BasicShowInfoState state,
        int field,
        int wireType)
    {
        if (field == 1 && wireType == 0)
        {
            if (!TryReadVarint(data, ref pos, out var v)) return false;
            state.CharId = unchecked((long)v);
            return true;
        }
        if (field == 2 && wireType == 2)
        {
            if (!TryReadString(data, ref pos, out state.Name)) return false;
            return true;
        }
        if (field == 3 && wireType == 0)
        {
            if (!TryReadVarint(data, ref pos, out var v)) return false;
            state.Gender = (int)v;
            return true;
        }
        if (field == 5 && wireType == 0)
        {
            if (!TryReadVarint(data, ref pos, out var v)) return false;
            state.Level = (int)v;
            return true;
        }
        if (field == 8 && wireType == 0)
        {
            if (!TryReadVarint(data, ref pos, out var v)) return false;
            state.IsNewbie = v != 0;
            return true;
        }
        return SkipField(data, ref pos, wireType);
    }

    /// <summary>
    /// Mutable state holder threaded through <see cref="ReadBasicShowInfoFields"/>.
    /// </summary>
    private ref struct BasicShowInfoState
    {
        public string Name;
        public long CharId;
        public int Gender;
        public int Level;
        public bool IsNewbie;
    }
}
