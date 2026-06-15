using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;

namespace Stellar.Wire;

/// <summary>Pure parser for the wire <c>AttrFashionData</c> attribute (id 201): a
/// <c>FashionData{ repeated FashionInfo fashion_infos = 1 }</c> message where each
/// <c>FashionInfo{ slot=1, fashion_id=2, colors=3 }</c> carries the worn cosmetic and its dye
/// colours (<c>FashionColorInfo.colors map&lt;int32, IntVec3&gt;</c>, HSV — see
/// <see cref="HsvToRgb"/>). Defensive: malformed input yields what parsed so far, never an
/// exception.</summary>
public static class AttrFashionDataReader
{
    private const int MaxDyes = 4;

    /// <summary>Decode the raw payload into worn-cosmetic entries ordered by slot.
    /// Returns an empty list (never null) when nothing parsed.</summary>
    public static IReadOnlyList<FashionEntry> Read(ReadOnlySpan<byte> payload)
    {
        var list = new List<FashionEntry>(8);
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if (field == 1 && wire == 2)
            {
                if (!WireProtocol.TryReadLengthDelimited(payload, ref pos, out var inner)) break;
                if (TryReadFashionInfo(inner, out var entry)) list.Add(entry);
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }
        list.Sort(static (a, b) => a.Slot.CompareTo(b.Slot));
        return list;
    }

    private static bool TryReadFashionInfo(ReadOnlySpan<byte> payload, out FashionEntry entry)
    {
        int slot = 0, fashionId = 0, pos = 0;
        List<ColorRgba>? dyes = null;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if (wire == 0 && WireProtocol.TryReadVarint(payload, ref pos, out var v))
            {
                if (field == 1) slot = (int)v;
                else if (field == 2) fashionId = (int)v;
            }
            else if (field == 3 && wire == 2 && WireProtocol.TryReadLengthDelimited(payload, ref pos, out var colors))
            {
                ReadColorInfo(colors, ref dyes);
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }
        entry = new FashionEntry(slot, fashionId, dyes?.ToArray() ?? FashionEntry.NoDyes);
        return fashionId != 0;
    }

    // FashionColorInfo { id=1, colors map<int32,IntVec3>=2, attachment_color=3 } — each map entry
    // is a nested message { key=1 varint, value=2 IntVec3{ x=1, y=2, z=3 } }. Attachment colours
    // are ignored (the primary dye channels are what the wardrobe list shows).
    private static void ReadColorInfo(ReadOnlySpan<byte> payload, ref List<ColorRgba>? dyes)
    {
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if (field == 2 && wire == 2 && WireProtocol.TryReadLengthDelimited(payload, ref pos, out var mapEntry))
            {
                if ((dyes?.Count ?? 0) >= MaxDyes) continue;
                if (TryReadMapVec3(mapEntry, out var rgb))
                {
                    dyes ??= new List<ColorRgba>(MaxDyes);
                    dyes.Add(rgb);
                }
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }
    }

    private static bool TryReadMapVec3(ReadOnlySpan<byte> payload, out ColorRgba rgb)
    {
        rgb = default;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if (field == 2 && wire == 2 && WireProtocol.TryReadLengthDelimited(payload, ref pos, out var vec))
            {
                rgb = ReadIntVec3Rgb(vec);
                return true;
            }
            if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }
        return false;
    }

    private static ColorRgba ReadIntVec3Rgb(ReadOnlySpan<byte> vec)
    {
        int x = 0, y = 0, z = 0, pos = 0;
        while (pos < vec.Length)
        {
            if (!WireProtocol.TryReadTag(vec, ref pos, out var field, out var wire)) break;
            if (wire == 0 && WireProtocol.TryReadVarint(vec, ref pos, out var v))
            {
                if (field == 1) x = (int)v;
                else if (field == 2) y = (int)v;
                else if (field == 3) z = (int)v;
            }
            else if (!WireProtocol.SkipField(vec, ref pos, wire)) break;
        }
        return HsvToRgb(x, y, z);
    }

    // Dye IntVec3 is HSV ON THE WIRE: x = hue 0-360, y = saturation 0-100, z = value 0-100. Truth:
    // fashion_vm.lua compares server values against floor(x*360)/floor(y*100)/floor(z*100) defaults,
    // and the game's dye picker is an HSV picker. Misreading the triple as RGB/255 rendered a WHITE
    // dye (s=0, v=85 → D9D9D9) as magenta — user-flagged in-world 2026-06-13.
    private static ColorRgba HsvToRgb(int h, int s, int v)
    {
        float sf = Math.Clamp(s, 0, 100) / 100f;
        float vf = Math.Clamp(v, 0, 100) / 100f;
        float hf = ((h % 360) + 360) % 360 / 60f;        // sector 0..6
        int   i  = (int)hf;
        float f  = hf - i;
        float p  = vf * (1f - sf);
        float q  = vf * (1f - sf * f);
        float t  = vf * (1f - sf * (1f - f));
        return i switch
        {
            0 => new ColorRgba(vf, t, p, 1f),
            1 => new ColorRgba(q, vf, p, 1f),
            2 => new ColorRgba(p, vf, t, 1f),
            3 => new ColorRgba(p, q, vf, 1f),
            4 => new ColorRgba(t, p, vf, 1f),
            _ => new ColorRgba(vf, p, q, 1f),
        };
    }
}
