using Stellar.Application.Tests.Wire;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Inspector;

public sealed class AttrEquipDataReaderTests
{
    private static byte[] EquipNine(int slot, int itemId) =>
        new WireBytes().Tag(1, 0).Varint((ulong)slot).Tag(2, 0).Varint((ulong)itemId).ToArray();

    private static byte[] Concat(params byte[][] msgs)
    {
        var w = new WireBytes();
        foreach (var m in msgs) w.LengthDelimited(m);
        return w.ToArray();
    }

    private static byte[] Tagged(params byte[][] msgs)
    {
        var w = new WireBytes();
        foreach (var m in msgs) w.Tag(1, 2).LengthDelimited(m);
        return w.ToArray();
    }

    [Fact]
    public void Read_TaggedRepeatedList_ParsesAllSlots()
    {
        // Live wire shape (2026-06-13 [EquipDiag]): tagged `repeated EquipNine = 1`.
        var payload = Tagged(EquipNine(200, 2001110), EquipNine(201, 2010942), EquipNine(210, 2101024));
        var list = AttrEquipDataReader.Read(payload);
        Assert.Equal(3, list.Count);
        Assert.Equal(200, list[0].Slot);   Assert.Equal(2001110, list[0].ItemId);
        Assert.Equal(201, list[1].Slot);   Assert.Equal(2010942, list[1].ItemId);
        Assert.Equal(210, list[2].Slot);   Assert.Equal(2101024, list[2].ItemId);
    }

    [Fact]
    public void Read_RealAppearPayload_ParsesElevenSlots()
    {
        // Verbatim [EquipDiag] hex from a live appear (entity 2214773654144) — the misparse-as-bare
        // regression case that shredded this to a single junk item.
        var payload = System.Convert.FromHexString(
            "0A0708CF0110E5B37E0A0808D20110959E80010A0708CC0110EAC87C0A0708C90110BADE7A0A0708C80110A8907A" +
            "0A0708CE01108AE57D0A0708D101109ED07F0A0708CB0110DAFA7B0A0708CA0110C9AC7B0A0708CD0110FA967D" +
            "0A0708D00110AA817F");
        var list = AttrEquipDataReader.Read(payload);
        Assert.Equal(11, list.Count);
        Assert.All(list, e => Assert.InRange(e.Slot, 200, 210));
        Assert.Contains(list, e => e.Slot == 200);   // weapon present
    }

    [Fact]
    public void Read_BareSequence_StillParses()
    {
        var payload = Concat(EquipNine(200, 2001110), EquipNine(201, 2010942));
        var list = AttrEquipDataReader.Read(payload);
        Assert.Equal(2, list.Count);
        Assert.Equal(200, list[0].Slot);   Assert.Equal(2001110, list[0].ItemId);
        Assert.Equal(201, list[1].Slot);   Assert.Equal(2010942, list[1].ItemId);
    }

    [Fact]
    public void Read_Empty_ReturnsEmpty()
        => Assert.Empty(AttrEquipDataReader.Read(System.ReadOnlySpan<byte>.Empty));
}
