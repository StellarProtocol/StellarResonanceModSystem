using System;

namespace Stellar.Wire;

/// <summary>Pure parser for <c>Zproto.AvatarInfo</c> (field numbers confirmed from
/// StarResonanceData stru_avatar_info.proto):
/// <c>AvatarInfo{ avatar_id=1, profile=2 PictureInfo, half_body=3 PictureInfo,
/// business_card_style_id=4, avatar_frame_id=5 }</c>, <c>PictureInfo{ url=1, verify=2 }</c>.
/// Defensive: malformed input yields empty strings, never throws.</summary>
public static class AvatarInfoReader
{
    /// <summary>Extract the profile / half-body picture URLs; empty strings when absent.</summary>
    public static void Read(ReadOnlySpan<byte> payload, out string profileUrl, out string halfBodyUrl)
    {
        profileUrl = ""; halfBodyUrl = "";
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var f, out var w)) break;
            if (w == 2 && (f == 2 || f == 3) && WireProtocol.TryReadLengthDelimited(payload, ref pos, out var pic))
            {
                var url = ReadPictureUrl(pic);
                if (f == 2) profileUrl = url; else halfBodyUrl = url;
            }
            else if (!WireProtocol.SkipField(payload, ref pos, w)) break;
        }
    }

    private static string ReadPictureUrl(ReadOnlySpan<byte> p)
    {
        int pos = 0;
        while (pos < p.Length)
        {
            if (!WireProtocol.TryReadTag(p, ref pos, out var f, out var w)) break;
            if (f == 1 && w == 2 && WireProtocol.TryReadLengthDelimited(p, ref pos, out var s))
                return System.Text.Encoding.UTF8.GetString(s);
            if (!WireProtocol.SkipField(p, ref pos, w)) break;
        }
        return "";
    }
}
