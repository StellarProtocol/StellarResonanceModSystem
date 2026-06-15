using Stellar.Application.Tests.Wire;
using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Inventory;

/// <summary>
/// Hand-built CharSerialize wire payloads driving <see cref="GearInstanceReader"/>.
/// Shapes mirror the zproto truth:
///   CharSerialize { item_package = 7; equip = 12 }
///   EquipList     { equip_list(map&lt;i32,EquipInfo&gt;) = 1; equip_enchant(map&lt;i64,EquipEnchantInfo&gt;) = 5 }
///   EquipInfo     { equip_slot = 1; item_uuid = 2; equip_slot_refine_level = 3 }
///   ItemPackage   { packages(map&lt;i32,Package&gt;) = 1 }   Package { items(map&lt;i64,Item&gt;) = 4 }
///   Item          { uuid = 1; config_id = 2; quality = 9; equip_attr = 10 }
///   EquipAttr     { perfection_value = 7; basic_attr = 10; advance_attr = 11;
///                   recast_attr = 12; perfection_level = 13; rare_quality_attr = 14;
///                   max_perfection_value = 15 }
/// </summary>
public sealed class GearInstanceReaderTests
{
    private const ulong WeaponUuid = 900001;
    private const int WeaponSlot = 200;
    private const int WeaponConfigId = 2001110;

    private static byte[] AttrEntry(int attrId, int value) =>
        new WireBytes().Tag(1, 0).Varint((ulong)attrId).Tag(2, 0).Varint((ulong)value).ToArray();

    private static byte[] EquipAttr() => new WireBytes()
        .Tag(7, 0).Varint(4500)                       // perfection_value
        .Tag(10, 2).LengthDelimited(AttrEntry(101, 250))   // basic_attr
        .Tag(11, 2).LengthDelimited(AttrEntry(2202, 700))  // advance_attr
        .Tag(11, 2).LengthDelimited(AttrEntry(2204, 350))  // advance_attr (2nd roll)
        .Tag(12, 2).LengthDelimited(AttrEntry(2203, 120))  // recast_attr
        .Tag(13, 0).Varint(3)                         // perfection_level
        .Tag(14, 2).LengthDelimited(AttrEntry(2901, 60))   // rare_quality_attr
        .Tag(15, 0).Varint(5000)                      // max_perfection_value
        .ToArray();

    private static byte[] Item(ulong uuid, int configId, int quality, byte[]? equipAttr)
    {
        var w = new WireBytes()
            .Tag(1, 0).Varint(uuid)
            .Tag(2, 0).Varint((ulong)configId)
            .Tag(9, 0).Varint((ulong)quality);
        if (equipAttr is not null) w.Tag(10, 2).LengthDelimited(equipAttr);
        return w.ToArray();
    }

    private static byte[] ItemMapEntry(ulong uuid, byte[] item) =>
        new WireBytes().Tag(1, 0).Varint(uuid).Tag(2, 2).LengthDelimited(item).ToArray();

    private static byte[] ItemPackage(int packageType, params byte[][] itemEntries)
    {
        var pkg = new WireBytes();
        foreach (var e in itemEntries) pkg.Tag(4, 2).LengthDelimited(e);
        var pkgEntry = new WireBytes()
            .Tag(1, 0).Varint((ulong)packageType)
            .Tag(2, 2).LengthDelimited(pkg.ToArray())
            .ToArray();
        return new WireBytes().Tag(1, 2).LengthDelimited(pkgEntry).ToArray();
    }

    private static byte[] EquipInfo(int slot, ulong uuid, int refine) => new WireBytes()
        .Tag(1, 0).Varint((ulong)slot).Tag(2, 0).Varint(uuid).Tag(3, 0).Varint((ulong)refine).ToArray();

    private static byte[] EquipListMsg(byte[][] equipInfos, byte[][]? enchantEntries = null)
    {
        var w = new WireBytes();
        foreach (var info in equipInfos)
        {
            var entry = new WireBytes().Tag(1, 0).Varint(0).Tag(2, 2).LengthDelimited(info).ToArray();
            w.Tag(1, 2).LengthDelimited(entry);
        }
        foreach (var e in enchantEntries ?? System.Array.Empty<byte[]>()) w.Tag(5, 2).LengthDelimited(e);
        return w.ToArray();
    }

    private static byte[] EnchantEntry(ulong uuid, int enchantItemTypeId, int level)
    {
        var info = new WireBytes().Tag(1, 0).Varint((ulong)enchantItemTypeId).Tag(2, 0).Varint((ulong)level).ToArray();
        return new WireBytes().Tag(1, 0).Varint(uuid).Tag(2, 2).LengthDelimited(info).ToArray();
    }

    private static byte[] CharSerialize(byte[] itemPackage, byte[] equipList) => new WireBytes()
        .Tag(1, 0).Varint(424242)                     // char_id — must be skipped cleanly
        .Tag(7, 2).LengthDelimited(itemPackage)
        .Tag(12, 2).LengthDelimited(equipList)
        .ToArray();

    [Fact]
    public void Read_FullPiece_ParsesRollsRefinePerfectionEnchant()
    {
        var payload = CharSerialize(
            ItemPackage(2, ItemMapEntry(WeaponUuid, Item(WeaponUuid, WeaponConfigId, 4, EquipAttr()))),
            EquipListMsg(new[] { EquipInfo(WeaponSlot, WeaponUuid, 7) },
                         new[] { EnchantEntry(WeaponUuid, 55501, 2) }));

        var list = GearInstanceReader.Read(payload);

        var g = Assert.Single(list);
        Assert.Equal(WeaponSlot, g.Slot);
        Assert.Equal((long)WeaponUuid, g.ItemUuid);
        Assert.Equal(WeaponConfigId, g.ConfigId);
        Assert.Equal(4, g.Quality);
        Assert.Equal(7, g.RefineLevel);
        Assert.Equal(4500, g.Perfection.Value);
        Assert.Equal(5000, g.Perfection.Max);
        Assert.Equal(3, g.Perfection.Level);

        var basic = Assert.Single(g.Attrs.Basic);
        Assert.Equal(101, basic.LibRowId);   Assert.Equal(250, basic.Percentile);
        Assert.Equal(2, g.Attrs.Advanced.Count);
        Assert.Equal(2202, g.Attrs.Advanced[0].LibRowId); Assert.Equal(700, g.Attrs.Advanced[0].Percentile);
        Assert.Equal(2204, g.Attrs.Advanced[1].LibRowId); Assert.Equal(350, g.Attrs.Advanced[1].Percentile);
        var recast = Assert.Single(g.Attrs.Recast);
        Assert.Equal(2203, recast.LibRowId); Assert.Equal(120, recast.Percentile);
        var rare = Assert.Single(g.Attrs.Rare);
        Assert.Equal(2901, rare.LibRowId);   Assert.Equal(60, rare.Percentile);

        Assert.NotNull(g.Enchant);
        Assert.Equal(55501, g.Enchant!.Value.ItemTypeId);
        Assert.Equal(2, g.Enchant.Value.Level);
    }

    // Spec/school (v2) gear leaves top-level advance_attr(11) EMPTY and puts the current spec's rolls in
    // equip_attr_set(17) { advance=2 } — the in-world Icicle case where adv=0 but the game shows Crit/Luck.
    [Fact]
    public void Read_SpecGear_UsesEquipAttrSetWhenTopLevelAdvancedEmpty()
    {
        var equipAttr = new WireBytes()
            .Tag(7, 0).Varint(4500)
            .Tag(10, 2).LengthDelimited(AttrEntry(101, 250))   // top-level basic only
            .Tag(12, 2).LengthDelimited(AttrEntry(2203, 120))  // top-level recast
            // equip_attr_set { basic=1; advance=2; recast=3; rare=4 } — the current spec's rolls.
            .Tag(17, 2).LengthDelimited(new WireBytes()
                .Tag(2, 2).LengthDelimited(AttrEntry(5101, 954))   // advance (Crit roll)
                .Tag(2, 2).LengthDelimited(AttrEntry(5102, 954))   // advance (Luck roll)
                .ToArray())
            .ToArray();
        var payload = CharSerialize(
            ItemPackage(2, ItemMapEntry(WeaponUuid, Item(WeaponUuid, WeaponConfigId, 4, equipAttr))),
            EquipListMsg(new[] { EquipInfo(WeaponSlot, WeaponUuid, 0) }));

        var g = Assert.Single(GearInstanceReader.Read(payload));
        // The set's advance wins (top-level was empty); basic/recast fall back to top-level.
        Assert.Equal(2, g.Attrs.Advanced.Count);
        Assert.Equal(5101, g.Attrs.Advanced[0].LibRowId); Assert.Equal(954, g.Attrs.Advanced[0].Percentile);
        Assert.Equal(5102, g.Attrs.Advanced[1].LibRowId);
        Assert.Single(g.Attrs.Basic);   // top-level basic retained
        Assert.Single(g.Attrs.Recast);
    }

    [Fact]
    public void Read_NoEnchant_EnchantIsNull()
    {
        var payload = CharSerialize(
            ItemPackage(2, ItemMapEntry(WeaponUuid, Item(WeaponUuid, WeaponConfigId, 4, EquipAttr()))),
            EquipListMsg(new[] { EquipInfo(WeaponSlot, WeaponUuid, 0) }));

        var g = Assert.Single(GearInstanceReader.Read(payload));
        Assert.Null(g.Enchant);
        Assert.Equal(0, g.RefineLevel);
    }

    [Fact]
    public void Read_UnequippedItemInEquipPackage_Excluded()
    {
        const ulong bagUuid = 900002;
        var payload = CharSerialize(
            ItemPackage(2,
                ItemMapEntry(WeaponUuid, Item(WeaponUuid, WeaponConfigId, 4, EquipAttr())),
                ItemMapEntry(bagUuid, Item(bagUuid, 2001111, 3, EquipAttr()))),
            EquipListMsg(new[] { EquipInfo(WeaponSlot, WeaponUuid, 1) }));

        var g = Assert.Single(GearInstanceReader.Read(payload));
        Assert.Equal((long)WeaponUuid, g.ItemUuid);
    }

    [Fact]
    public void Read_NonEquipPackage_Ignored()
    {
        // Same item uuid lives in package 5 (modules) — must not surface.
        var payload = CharSerialize(
            ItemPackage(5, ItemMapEntry(WeaponUuid, Item(WeaponUuid, WeaponConfigId, 4, EquipAttr()))),
            EquipListMsg(new[] { EquipInfo(WeaponSlot, WeaponUuid, 1) }));

        Assert.Empty(GearInstanceReader.Read(payload));
    }

    [Fact]
    public void Read_TwoSlots_SortedBySlot()
    {
        const ulong headUuid = 900003;
        var payload = CharSerialize(
            ItemPackage(2,
                ItemMapEntry(headUuid, Item(headUuid, 2010942, 3, EquipAttr())),
                ItemMapEntry(WeaponUuid, Item(WeaponUuid, WeaponConfigId, 4, EquipAttr()))),
            EquipListMsg(new[] { EquipInfo(201, headUuid, 0), EquipInfo(WeaponSlot, WeaponUuid, 0) }));

        var list = GearInstanceReader.Read(payload);
        Assert.Equal(2, list.Count);
        Assert.Equal(200, list[0].Slot);
        Assert.Equal(201, list[1].Slot);
    }

    [Fact]
    public void Read_Empty_ReturnsEmpty()
        => Assert.Empty(GearInstanceReader.Read(System.ReadOnlySpan<byte>.Empty));

    [Fact]
    public void Read_Truncated_ReturnsEmptyNotThrow()
    {
        var full = CharSerialize(
            ItemPackage(2, ItemMapEntry(WeaponUuid, Item(WeaponUuid, WeaponConfigId, 4, EquipAttr()))),
            EquipListMsg(new[] { EquipInfo(WeaponSlot, WeaponUuid, 1) }));
        var truncated = new byte[full.Length / 2];
        System.Array.Copy(full, truncated, truncated.Length);

        // Must not throw; partial/empty result acceptable.
        _ = GearInstanceReader.Read(truncated);
    }

    [Fact]
    public void Huge_declared_length_does_not_throw()
    {
        // tag(field=7, wire=2) = (7 << 3) | 2 = 0x3A, followed by a 5-byte varint
        // encoding 0x7FFFFFFF (int.MaxValue).  This makes pos + n wrap negative
        // under the old guard, bypassing the check and reaching Slice() which
        // throws ArgumentOutOfRangeException.  The fix ensures the guard catches
        // it and Read() returns empty without throwing.
        var payload = new byte[]
        {
            0x3A,                                     // tag: field 7, wire-type 2
            0xFF, 0xFF, 0xFF, 0xFF, 0x07              // varint 0x7FFFFFFF = int.MaxValue
        };

        var result = GearInstanceReader.Read(payload);

        Assert.Empty(result);
    }

    [Fact]
    public void Dangling_equip_slot_reference_is_dropped()
    {
        // equip_list references uuid 999999 in slot 200, but that uuid has no
        // matching item in the package-2 item map.  The reader must drop the
        // dangling slot rather than fabricating a GearInstance with zeroed fields.
        const ulong danglingUuid = 999999;

        var payload = CharSerialize(
            ItemPackage(2, ItemMapEntry(WeaponUuid, Item(WeaponUuid, WeaponConfigId, 4, EquipAttr()))),
            EquipListMsg(new[] { EquipInfo(WeaponSlot, danglingUuid, 0) }));

        var result = GearInstanceReader.Read(payload);

        Assert.Empty(result);
    }
}
