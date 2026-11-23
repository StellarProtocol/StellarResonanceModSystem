using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Wire;

/// <summary>
/// Decoders for the rich <c>ChatMsgInfo</c> payloads: <c>chat_hypertext</c> (7) — position / item / player / home /
/// blueprint shares and the server's templated notices — and <c>voice</c> (6). Raw decode only (BCL, any thread): the
/// display segments need the game's live tables, so the Application layer builds them on the main thread.
/// Shapes: <c>Knowledge Base\Chat-Hypertext-Links.md</c> (proto/zproto <c>stru_place_holder*.proto</c>).
/// </summary>
public static partial class WireProtocol
{
    // A real share carries 3 slots and the longest notice ~6; anything past this is junk, not a message.
    private const int MaxPlaceholders = 32;

    /// <summary>
    /// Decode <c>ChatHypertext{ uint32 config_id = 1; repeated PlaceHolder hypertext_contents = 2 }</c>. Each
    /// <c>PlaceHolder{ PlaceHolderType type = 1; bytes bytes_content = 2 }</c> is decoded by type; unknown types and
    /// types without a payload are kept as a bare slot (<see cref="ChatPlaceholder.Type"/> only). Never throws: a
    /// malformed body returns the slots read before the damage (null only when nothing at all was read).
    /// </summary>
    public static ChatHypertext? ParseChatHypertext(ReadOnlySpan<byte> data)
    {
        uint configId = 0;
        List<ChatPlaceholder>? slots = null;
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadTag(data, ref pos, out var field, out var wireType)) break;
            if (field == 1 && wireType == 0)
            {
                if (!TryReadVarint(data, ref pos, out var v)) break;
                configId = unchecked((uint)v);
            }
            else if (field == 2 && wireType == 2)
            {
                if (!TryReadLengthDelimited(data, ref pos, out var ph)) break;
                if ((slots ??= new List<ChatPlaceholder>(4)).Count < MaxPlaceholders) slots.Add(ParsePlaceholder(ph));
            }
            else if (!SkipField(data, ref pos, wireType)) break;
        }
        if (configId == 0 && slots is null) return null;
        return new ChatHypertext
        {
            ConfigId = unchecked((int)configId),
            Placeholders = slots is null ? Array.Empty<ChatPlaceholder>() : slots.ToArray(),
        };
    }

    /// <summary>Decode <c>ChatVoice{ string file_id = 1; string text = 2; uint32 seconds = 3 }</c>. Transcript is
    /// returned raw (unsanitised). Never throws; a malformed body keeps what was read before the damage.</summary>
    public static ChatVoice ParseChatVoice(ReadOnlySpan<byte> data)
    {
        string fileId = string.Empty, text = string.Empty;
        uint seconds = 0;
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadTag(data, ref pos, out var field, out var wireType)) break;
            bool ok = (field, wireType) switch
            {
                (1, 2) => TryReadString(data, ref pos, out fileId),
                (2, 2) => TryReadString(data, ref pos, out text),
                (3, 0) => TryReadUInt32(data, ref pos, out seconds),
                _ => SkipField(data, ref pos, wireType),
            };
            if (!ok) break;
        }
        return new ChatVoice { FileId = fileId, Transcript = text, Seconds = (int)Math.Min(seconds, int.MaxValue) };
    }

    // One PlaceHolder: read its type + raw bytes (either order), then decode the bytes by type.
    private static ChatPlaceholder ParsePlaceholder(ReadOnlySpan<byte> data)
    {
        int type = 0;
        ReadOnlySpan<byte> content = default;
        int pos = 0;
        while (pos < data.Length)
        {
            if (!TryReadTag(data, ref pos, out var field, out var wireType)) break;
            if (field == 1 && wireType == 0)
            {
                if (!TryReadVarint(data, ref pos, out var v)) break;
                type = unchecked((int)v);
            }
            else if (field == 2 && wireType == 2)
            {
                if (!TryReadLengthDelimited(data, ref pos, out content)) break;
            }
            else if (!SkipField(data, ref pos, wireType)) break;
        }
        var kind = type is >= 1 and <= 15 ? (ChatPlaceholderType)type : ChatPlaceholderType.None;
        return DecodePlaceholderBody(kind, content);
    }

    // Per-type nested proto. Each sub-reader walks the body with SkipField for unknown tags and stops (keeping
    // what it has) on damage, so a newer server adding fields never breaks the decode.
    private static ChatPlaceholder DecodePlaceholderBody(ChatPlaceholderType kind, ReadOnlySpan<byte> body)
    {
        var b = new PlaceholderBody(body);
        return kind switch
        {
            ChatPlaceholderType.Val or ChatPlaceholderType.Union or ChatPlaceholderType.Buff
                or ChatPlaceholderType.Timestamp or ChatPlaceholderType.Item
                => new ChatPlaceholder { Type = kind, Value = b.Int(1) },
            ChatPlaceholderType.Player => new ChatPlaceholder { Type = kind, CharId = b.Int(1), Text = b.Str(2) },
            ChatPlaceholderType.String => new ChatPlaceholder { Type = kind, Text = b.Str(1) },
            ChatPlaceholderType.FishPersonalTotal => new ChatPlaceholder { Type = kind, Text = b.Str(8) },
            ChatPlaceholderType.FishItem => new ChatPlaceholder { Type = kind, Value = b.Int(1), Rank = (int)b.Int(2) },
            ChatPlaceholderType.FishRank => new ChatPlaceholder { Type = kind, Value = b.Int(1), Rank = (int)b.Int(3) },
            ChatPlaceholderType.MasterMode => new ChatPlaceholder { Type = kind, Text = b.Str(2), Value = b.Int(3) },
            ChatPlaceholderType.ScenePosition => new ChatPlaceholder { Type = kind, Position = ReadScenePosition(b) },
            ChatPlaceholderType.Homeland => new ChatPlaceholder
            {
                Type = kind, CommunityId = b.Int(1), HomelandId = b.Int(2), CharId = b.Int(3), Text = b.Str(4),
            },
            ChatPlaceholderType.Blueprint => new ChatPlaceholder { Type = kind, Text = b.Str(2) },
            _ => new ChatPlaceholder { Type = kind },
        };
    }

    // PlaceHolderScenePosition{ scene_id=1, line_id=2, positionx=3, positiony=4, positionz=5, map_layer_id=6 (int32),
    // scene_guid=7 (string, new in Season 4) }. int32 negatives arrive as 10-byte varints → truncate, don't check.
    private static ChatPosition ReadScenePosition(PlaceholderBody b)
    {
        var guid = b.Str(7);
        return new ChatPosition
        {
            SceneId = (int)b.Int(1), LineId = (int)b.Int(2),
            X = (int)b.Int(3), Y = (int)b.Int(4), Z = (int)b.Int(5),
            MapLayerId = (int)b.Int(6),
            SceneGuid = guid.Length == 0 ? null : guid,
        };
    }

    private static bool TryReadUInt32(ReadOnlySpan<byte> data, ref int pos, out uint value)
    {
        value = 0;
        if (!TryReadVarint(data, ref pos, out var v)) return false;
        value = unchecked((uint)v);
        return true;
    }

    /// <summary>
    /// Random access to the scalar fields of one small nested proto: each lookup re-walks the body (a placeholder
    /// body is a few dozen bytes, so this is cheaper than allocating a field map). Missing / damaged → 0 or "".
    /// </summary>
    private readonly ref struct PlaceholderBody
    {
        private readonly ReadOnlySpan<byte> _data;
        public PlaceholderBody(ReadOnlySpan<byte> data) => _data = data;

        /// <summary>Varint field <paramref name="number"/> as a signed 64-bit value (int32/int64/uint32 alike).</summary>
        public long Int(int number)
        {
            int pos = 0;
            while (pos < _data.Length)
            {
                if (!TryReadTag(_data, ref pos, out var field, out var wireType)) return 0;
                if (field == number && wireType == 0)
                    return TryReadVarint(_data, ref pos, out var v) ? unchecked((long)v) : 0;
                if (!SkipField(_data, ref pos, wireType)) return 0;
            }
            return 0;
        }

        /// <summary>String field <paramref name="number"/> (UTF-8); "" when absent or damaged.</summary>
        public string Str(int number)
        {
            int pos = 0;
            while (pos < _data.Length)
            {
                if (!TryReadTag(_data, ref pos, out var field, out var wireType)) return string.Empty;
                if (field == number && wireType == 2)
                    return TryReadString(_data, ref pos, out var s) ? s : string.Empty;
                if (!SkipField(_data, ref pos, wireType)) return string.Empty;
            }
            return string.Empty;
        }
    }
}
