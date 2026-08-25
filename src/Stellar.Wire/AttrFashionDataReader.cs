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
    // Up to all 16 EFashionColorAreaType channels of one piece (Base1..4, Socks1..4, BaseEx1..4,
    // UnderWear1..4). Was 4 — too low for multi-area pieces; the Entity Inspector clamps its own swatch
    // display, so raising this does not change its UI.
    private const int MaxDyes = 16;

    // FashionColorInfo field 3 (attachment_color) is keyed by a RELATIVE socks index 1..4 (the game's
    // send path assigns attachmentColor[area - Socks1 + 1]); the absolute EFashionColorAreaType area is
    // Socks1(5) + key - 1 = SocksKeyBase + key.
    private const int SocksKeyBase = 4;

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
        List<int>? areas = null;
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
                ReadColorInfo(colors, ref dyes, ref areas);
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }
        entry = new FashionEntry(slot, fashionId, dyes?.ToArray() ?? FashionEntry.NoDyes)
        {
            DyeAreas = areas?.ToArray() ?? FashionEntry.NoAreas,
        };
        return fashionId != 0;
    }

    // FashionColorInfo { id=1, colors map<int32,IntVec3>=2, attachment_color map<int32,IntVec3>=3 } — each
    // map entry is a nested message { key=1 varint, value=2 IntVec3{ x,y,z } }. Field 2's key is the
    // ABSOLUTE EFashionColorAreaType area; field 3 (socks) is keyed by a relative 1..4 index → absolute
    // area SocksKeyBase+key. Both feed the parallel dyes/areas lists so a consumer can place each colour on
    // its real area. A field-3 element that is a bare IntVec3 (repeated, not a map) yields no value → it is
    // safely skipped rather than misread.
    private static void ReadColorInfo(ReadOnlySpan<byte> payload, ref List<ColorRgba>? dyes, ref List<int>? areas)
    {
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if ((field == 2 || field == 3) && wire == 2 && WireProtocol.TryReadLengthDelimited(payload, ref pos, out var mapEntry))
            {
                if ((dyes?.Count ?? 0) >= MaxDyes) continue;
                if (TryReadMapEntry(mapEntry, out var key, out var rgb))
                {
                    dyes ??= new List<ColorRgba>(MaxDyes);
                    areas ??= new List<int>(MaxDyes);
                    dyes.Add(rgb);
                    areas.Add(field == 3 ? SocksKeyBase + key : key);
                }
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }
    }

    // Read one map entry { key=1 varint, value=2 IntVec3 } → (area key, rgb). False when no value IntVec3
    // is present (so a bare repeated IntVec3 in field 3 contributes nothing rather than a garbage colour).
    private static bool TryReadMapEntry(ReadOnlySpan<byte> payload, out int key, out ColorRgba rgb)
    {
        key = 0;
        rgb = default;
        var haveVec = false;
        int pos = 0;
        while (pos < payload.Length)
        {
            if (!WireProtocol.TryReadTag(payload, ref pos, out var field, out var wire)) break;
            if (field == 1 && wire == 0 && WireProtocol.TryReadVarint(payload, ref pos, out var k)) key = (int)k;
            else if (field == 2 && wire == 2 && WireProtocol.TryReadLengthDelimited(payload, ref pos, out var vec))
            {
                rgb = ReadIntVec3Rgb(vec);
                haveVec = true;
            }
            else if (!WireProtocol.SkipField(payload, ref pos, wire)) break;
        }
        return haveVec;
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
