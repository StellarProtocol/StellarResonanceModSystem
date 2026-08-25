using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// Hand-built <c>AttrFashionData</c> (attr 201) wire payloads driving
/// <see cref="AttrFashionDataReader"/>. Shapes mirror the zproto truth:
///   FashionData       { repeated FashionInfo fashion_infos = 1 }
///   FashionInfo       { slot = 1; fashion_id = 2; colors = 3 }
///   FashionColorInfo  { id = 1; colors(map&lt;i32,IntVec3&gt;) = 2; attachment_color(map&lt;i32,IntVec3&gt;) = 3 }
///   IntVec3           { x = 1; y = 2; z = 3 }   (HSV: h 0-360, s 0-100, v 0-100)
/// </summary>
public sealed class AttrFashionDataReaderTests
{
    private static byte[] IntVec3(int x, int y, int z) => new WireBytes()
        .Tag(1, 0).Varint((ulong)x)
        .Tag(2, 0).Varint((ulong)y)
        .Tag(3, 0).Varint((ulong)z)
        .ToArray();

    private static byte[] ColorMapEntry(int key, byte[] vec3) => new WireBytes()
        .Tag(1, 0).Varint((ulong)key)
        .Tag(2, 2).LengthDelimited(vec3)
        .ToArray();

    private static byte[] ColorInfo(params byte[][] mapEntries)
    {
        var w = new WireBytes().Tag(1, 0).Varint(7);   // FashionColorInfo.id — must be skipped cleanly
        foreach (var e in mapEntries) w.Tag(2, 2).LengthDelimited(e);   // field 2 = colors map (base areas)
        return w.ToArray();
    }

    // FashionColorInfo carrying attachment_color (field 3) map entries — socks channels.
    private static byte[] AttachmentInfo(params byte[][] mapEntries)
    {
        var w = new WireBytes().Tag(1, 0).Varint(7);   // FashionColorInfo.id
        foreach (var e in mapEntries) w.Tag(3, 2).LengthDelimited(e);   // field 3 = attachment_color map (socks)
        return w.ToArray();
    }

    private static byte[] FashionInfo(int slot, int fashionId, byte[]? colors = null)
    {
        var w = new WireBytes()
            .Tag(1, 0).Varint((ulong)slot)
            .Tag(2, 0).Varint((ulong)fashionId);
        if (colors is not null) w.Tag(3, 2).LengthDelimited(colors);
        return w.ToArray();
    }

    private static byte[] FashionData(params byte[][] infos)
    {
        var w = new WireBytes();
        foreach (var i in infos) w.Tag(1, 2).LengthDelimited(i);
        return w.ToArray();
    }

    [Fact]
    public void Reads_slots_ids_dye_colors_and_areas()
    {
        // Dye triples are HSV on the wire: h 0-360, s 0-100, v 0-100 (lua fashion_vm truth).
        // h=0, s=0, v=85 → the white D9D9D9 dye (the misread-as-RGB regression case, 2026-06-13).
        // Map key 2 = the Base2 area — it must survive into DyeAreas so a consumer can place it correctly.
        var payload = FashionData(
            FashionInfo(3, 5301555),
            FashionInfo(1, 5300123, ColorInfo(ColorMapEntry(2, IntVec3(0, 0, 85)))));

        var list = AttrFashionDataReader.Read(payload);

        Assert.Equal(2, list.Count);

        // Ordered by slot.
        Assert.Equal(1, list[0].Slot);
        Assert.Equal(5300123, list[0].FashionId);
        var dye = Assert.Single(list[0].Dyes);
        Assert.Equal(0.85f, dye.R, 5);
        Assert.Equal(0.85f, dye.G, 5);
        Assert.Equal(0.85f, dye.B, 5);
        Assert.Equal(1f, dye.A, 5);
        Assert.Equal(new[] { 2 }, list[0].DyeAreas);   // field-2 key = absolute EFashionColorAreaType area

        Assert.Equal(3, list[1].Slot);
        Assert.Equal(5301555, list[1].FashionId);
        Assert.Empty(list[1].Dyes);
        Assert.Empty(list[1].DyeAreas);
    }

    [Fact]
    public void Reads_attachment_socks_as_absolute_areas()
    {
        // attachment_color (field 3) is keyed by a relative socks index 1..4 → absolute area 5..8.
        // h=120 (pure green) at socks 1 → area 5; h=240 (pure blue) at socks 3 → area 7.
        var payload = FashionData(FashionInfo(1, 5300123, AttachmentInfo(
            ColorMapEntry(1, IntVec3(120, 100, 100)),
            ColorMapEntry(3, IntVec3(240, 100, 100)))));

        var entry = Assert.Single(AttrFashionDataReader.Read(payload));

        Assert.Equal(new[] { 5, 7 }, entry.DyeAreas);
        Assert.Equal(1f, entry.Dyes[0].G, 5);   // green at socks-1
        Assert.Equal(1f, entry.Dyes[1].B, 5);   // blue at socks-3
    }

    [Fact]
    public void Skips_bare_attachment_vec3_without_map_value()
    {
        // If attachment_color is actually a bare repeated IntVec3 (no map value submessage), it must
        // contribute nothing rather than misread the vector's x as an area key and its y as a colour.
        var payload = FashionData(FashionInfo(1, 5300123, AttachmentInfo(IntVec3(120, 100, 100))));

        var entry = Assert.Single(AttrFashionDataReader.Read(payload));

        Assert.Empty(entry.Dyes);
        Assert.Empty(entry.DyeAreas);
    }

    [Fact]
    public void Skips_entries_without_fashion_id()
    {
        var payload = FashionData(
            FashionInfo(2, 0),                  // slot present, id 0 → excluded
            FashionInfo(4, 5302000));

        var list = AttrFashionDataReader.Read(payload);

        var entry = Assert.Single(list);
        Assert.Equal(4, entry.Slot);
        Assert.Equal(5302000, entry.FashionId);
    }

    [Fact]
    public void Empty_payload_returns_empty()
        => Assert.Empty(AttrFashionDataReader.Read(System.ReadOnlySpan<byte>.Empty));

    [Fact]
    public void Truncated_payload_does_not_throw()
    {
        // tag(field=1, wire=2) followed by a 5-byte varint declaring int.MaxValue
        // bytes — overflow-safe guards must bail without throwing.
        var payload = new byte[]
        {
            0x0A,                                     // tag: field 1, wire-type 2
            0xFF, 0xFF, 0xFF, 0xFF, 0x07              // varint 0x7FFFFFFF = int.MaxValue
        };

        var result = AttrFashionDataReader.Read(payload);

        Assert.Empty(result);
    }

    [Fact]
    public void Caps_dyes_at_sixteen()
    {
        // A piece has at most 16 EFashionColorAreaType channels; the reader caps there. s=0 → greyscale,
        // R = v/100. Feed 20 entries (keys 0..19); only the first 16 survive.
        var entries = new List<byte[]>();
        for (var i = 0; i < 20; i++) entries.Add(ColorMapEntry(i, IntVec3(0, 0, (i + 1) * 5)));
        var payload = FashionData(FashionInfo(1, 5300123, ColorInfo(entries.ToArray())));

        var entry = Assert.Single(AttrFashionDataReader.Read(payload));

        Assert.Equal(16, entry.Dyes.Length);
        Assert.Equal(16, entry.DyeAreas.Length);
        Assert.Equal(0.05f, entry.Dyes[0].R, 5);
        Assert.Equal(0.80f, entry.Dyes[15].R, 5);
        Assert.Equal(15, entry.DyeAreas[15]);
    }

    [Fact]
    public void Converts_saturated_hsv_to_rgb()
    {
        // h=120 (green), s=100, v=100 → pure green; h=240 (blue) s=50 v=100 → half-saturated blue.
        var colors = ColorInfo(
            ColorMapEntry(1, IntVec3(120, 100, 100)),
            ColorMapEntry(2, IntVec3(240, 50, 100)));
        var payload = FashionData(FashionInfo(1, 5300123, colors));

        var entry = Assert.Single(AttrFashionDataReader.Read(payload));

        Assert.Equal(0f, entry.Dyes[0].R, 5);
        Assert.Equal(1f, entry.Dyes[0].G, 5);
        Assert.Equal(0f, entry.Dyes[0].B, 5);
        Assert.Equal(0.5f, entry.Dyes[1].R, 5);
        Assert.Equal(0.5f, entry.Dyes[1].G, 5);
        Assert.Equal(1f, entry.Dyes[1].B, 5);
        Assert.Equal(new[] { 1, 2 }, entry.DyeAreas);
    }
}
