using System;
using System.Buffers.Binary;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game.Protobuf;

/// <summary>
/// Decoded world position + optional facing carried by <c>AttrPos(52)</c>.
/// <see cref="Dir"/> is meaningful only when <see cref="HasDir"/> is true.
/// </summary>
internal readonly record struct WirePos(float X, float Y, float Z, float Dir, bool HasDir);

/// <summary>
/// Pure decoder for the position-family attrs carried in <c>AoiSyncDelta</c>
/// <c>AttrCollection</c> rows:
/// <list type="bullet">
///   <item><c>AttrPos(52)</c> = the serialized <c>zproto.Position { float x=1; y=2; z=3; dir=4 }</c>
///   message — each field a little-endian fixed32 (wire-type 5); proto3 omits zero-valued fields.</item>
///   <item><c>AttrDir(50)</c> = a single little-endian fixed32 float (facing).</item>
/// </list>
///
/// <para>VERIFIED against <c>StarResonanceData/proto/zproto/stru_position.proto</c>
/// (<c>x=1,y=2,z=3,dir=4</c>, all <c>float</c>) and <c>enum_e_attr_type.proto</c>
/// (<c>AttrDir=50</c>, <c>AttrPos=52</c>). The attr value is the RAW serialized field value —
/// no outer tag/length — the same convention <c>AttrName</c> uses (raw UTF-8, see
/// <see cref="AttrCollectionReader"/>), so a message-typed attr's <c>RawData</c> is the inner
/// message's own tagged fields.</para>
///
/// <para>Also accepts a tag-less packed form (12 B = x,y,z or 16 B = x,y,z,dir, little-endian
/// float32) as a defensive fallback in case a build packs the value raw; the caller's diagnostic
/// logs the raw byte length so an owner run disambiguates which form actually ships. Pure and
/// allocation-free; BCL + Wire only.</para>
/// </summary>
internal static class WirePositionReader
{
    /// <summary>
    /// Decode an <c>AttrPos(52)</c> payload. Returns false on empty/malformed input or when no
    /// x/y/z field was present (a pure <paramref name="pos"/> default is never a real position).
    /// </summary>
    public static bool TryReadPosition(ReadOnlySpan<byte> raw, out WirePos pos)
    {
        pos = default;
        if (raw.IsEmpty) return false;
        // Tag-less packed float32 form (defensive): exactly 12 or 16 bytes. ASSUMES Position stays
        // all-fixed32 — a future non-float field that happens to total 12/16 B would decode as garbage
        // here; the caller's [PosDbg][wire] raw-length line is the tripwire (tagged form is 15/20 B).
        if (raw.Length == 12 || raw.Length == 16)
            return TryReadPacked(raw, out pos);
        return TryReadTagged(raw, out pos);
    }

    /// <summary>Decode an <c>AttrDir(50)</c> payload (a single little-endian fixed32 float).</summary>
    public static bool TryReadDir(ReadOnlySpan<byte> raw, out float dir)
    {
        dir = 0f;
        if (raw.Length != 4) return false;
        dir = BinaryPrimitives.ReadSingleLittleEndian(raw);
        return true;
    }

    private static bool TryReadPacked(ReadOnlySpan<byte> raw, out WirePos pos)
    {
        var x = BinaryPrimitives.ReadSingleLittleEndian(raw.Slice(0, 4));
        var y = BinaryPrimitives.ReadSingleLittleEndian(raw.Slice(4, 4));
        var z = BinaryPrimitives.ReadSingleLittleEndian(raw.Slice(8, 4));
        var hasDir = raw.Length == 16;
        var dir = hasDir ? BinaryPrimitives.ReadSingleLittleEndian(raw.Slice(12, 4)) : 0f;
        pos = new WirePos(x, y, z, dir, hasDir);
        return true;
    }

    private static bool TryReadTagged(ReadOnlySpan<byte> raw, out WirePos pos)
    {
        pos = default;
        float x = 0f, y = 0f, z = 0f, dir = 0f;
        bool hasXyz = false, hasDir = false;
        int p = 0;
        while (p < raw.Length)
        {
            if (!WireProtocol.TryReadTag(raw, ref p, out var field, out var wire)) return false;
            if (wire == 5) // 32-bit fixed (float)
            {
                if (p + 4 > raw.Length) return false;
                var f = BinaryPrimitives.ReadSingleLittleEndian(raw.Slice(p, 4));
                p += 4;
                switch (field)
                {
                    case 1: x = f; hasXyz = true; break;
                    case 2: y = f; hasXyz = true; break;
                    case 3: z = f; hasXyz = true; break;
                    case 4: dir = f; hasDir = true; break;
                }
            }
            else if (!WireProtocol.SkipField(raw, ref p, wire)) return false;
        }
        if (!hasXyz && !hasDir) return false;
        pos = new WirePos(x, y, z, dir, hasDir);
        return hasXyz;
    }
}
