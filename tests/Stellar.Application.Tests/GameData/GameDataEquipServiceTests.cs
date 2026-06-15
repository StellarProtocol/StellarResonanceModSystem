using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.GameData;

public class GameDataEquipServiceTests
{
    [Fact]
    public void Lookups_return_null_or_empty_before_load()
    {
        var svc = new GameDataEquipService();
        Assert.Null(svc.GetEquipRow(2000101));
        Assert.Empty(svc.GetAttrLib(101, 200));
        Assert.Empty(svc.GetAttrLibRow(2847));
        Assert.Null(svc.GetSlotName(200));
    }

    [Fact]
    public void Lookups_resolve_after_load()
    {
        var svc = new GameDataEquipService();
        svc.LoadEquipRows(new Dictionary<int, EquipRowInfo>
        {
            [2000101] = new EquipRowInfo(2000101, 200, 740, 60, 100,
                BasicLibVersion: 1, new[] { 1001003 }, AdvancedLibVersion: 1, new[] { 2003003 }),
        });
        // Loader emits ROW-keyed data; the service indexes by row id AND regroups by AttrLibId.
        svc.LoadAttrLibs(new Dictionary<int, EquipAttrLibRowData>
        {
            [2847] = new EquipAttrLibRowData(2003003, new[] { 200 }, new[] { new EquipAttrRange(11112, 140, 200) }),
        });
        svc.LoadSlotNames(new Dictionary<int, string> { [200] = "Helmet" });

        var row = svc.GetEquipRow(2000101);
        Assert.Equal(740, row!.Value.Gs);
        Assert.Equal(60, row.Value.WearLevel);
        var lib = svc.GetAttrLib(2003003, 200);
        Assert.Single(lib);
        Assert.Equal((11112, 140, 200), (lib[0].AttrId, lib[0].Min, lib[0].Max));
        var byRow = svc.GetAttrLibRow(2847);
        Assert.Single(byRow);
        Assert.Equal(11112, byRow[0].AttrId);
        Assert.Equal("Helmet", svc.GetSlotName(200));
    }

    // A lib id has one row per slot-part group (ZDPS AllowPart semantics): the part-filtered lookup
    // must return the matching row's entries, never a cross-part merge — the merge rendered every
    // attr with one giant identical range in the gear-detail popup (in-world 2026-06-13).
    [Fact]
    public void GetAttrLib_filters_rows_by_equip_part()
    {
        var svc = new GameDataEquipService();
        svc.LoadAttrLibs(new Dictionary<int, EquipAttrLibRowData>
        {
            [1] = new EquipAttrLibRowData(5001, new[] { 200 },      new[] { new EquipAttrRange(11710, 675, 954) }),
            [2] = new EquipAttrLibRowData(5001, new[] { 205, 206 }, new[] { new EquipAttrRange(11710, 337, 477) }),
        });

        var weapon = svc.GetAttrLib(5001, 200);
        Assert.Single(weapon);
        Assert.Equal((675, 954), (weapon[0].Min, weapon[0].Max));

        var earring = svc.GetAttrLib(5001, 205);
        Assert.Single(earring);
        Assert.Equal((337, 477), (earring[0].Min, earring[0].Max));

        Assert.Empty(svc.GetAttrLib(5001, 999));   // no row allows this part
    }

    // v2 school libs filter by part AND talent school — a far player's spec (→ talent school) picks the
    // right raid-gear advanced rolls; a wrong/zero school must not return another spec's ranges.
    [Fact]
    public void GetSchoolAttrLib_filters_by_part_and_talent_school()
    {
        var svc = new GameDataEquipService();
        svc.LoadSchoolAttrLibs(new Dictionary<int, EquipAttrSchoolLibRowData>
        {
            [1] = new EquipAttrSchoolLibRowData(7001, new[] { 207 }, new[] { 113 }, new[] { new EquipAttrRange(11710, 200, 400) }),
            [2] = new EquipAttrSchoolLibRowData(7001, new[] { 207 }, new[] { 114 }, new[] { new EquipAttrRange(11710, 500, 700) }),
        });

        var earthfort = svc.GetSchoolAttrLib(7001, 207, 113);
        Assert.Single(earthfort);
        Assert.Equal((200, 400), (earthfort[0].Min, earthfort[0].Max));

        var block = svc.GetSchoolAttrLib(7001, 207, 114);
        Assert.Equal((500, 700), (block[0].Min, block[0].Max));

        Assert.Empty(svc.GetSchoolAttrLib(7001, 207, 0));     // spec unknown → no guess
        Assert.Empty(svc.GetSchoolAttrLib(7001, 999, 113));   // wrong part
    }

    // Spec-set rolls key by SCHOOL row id; a school row id can collide with a v1 row id mapping to
    // different attrs. GetSchoolAttrLibRow must resolve against the school table only (in-world: a
    // colliding v1 row showed DMG-Bonus/Element-Resist instead of the spec's Crit/Luck, 2026-06-13).
    [Fact]
    public void School_and_v1_row_id_lookups_are_independent()
    {
        var svc = new GameDataEquipService();
        svc.LoadAttrLibs(new Dictionary<int, EquipAttrLibRowData>
        {
            [2013000] = new EquipAttrLibRowData(9001, System.Array.Empty<int>(), new[] { new EquipAttrRange(13700, 1, 350) }), // v1 row 2013000 = DMG Bonus
        });
        svc.LoadSchoolAttrLibs(new Dictionary<int, EquipAttrSchoolLibRowData>
        {
            [2013000] = new EquipAttrSchoolLibRowData(9001, System.Array.Empty<int>(), new[] { 104 }, new[] { new EquipAttrRange(11710, 1, 954) }), // school row 2013000 = Crit
        });

        Assert.Equal(13700, svc.GetAttrLibRow(2013000)[0].AttrId);        // v1 → DMG Bonus
        Assert.Equal(11710, svc.GetSchoolAttrLibRow(2013000)[0].AttrId);  // school → Crit (not the v1 collision)
    }

    // The wire's (enchant_item_type_id, enchant_level) resolves to the gem item id (whose name carries the
    // DISPLAY level) + flat effects — the wire level is an internal index (read "Lv 8" for "Lv.2", 2026-06-13).
    [Fact]
    public void GetEnchantItem_resolves_by_type_and_level()
    {
        var svc = new GameDataEquipService();
        svc.LoadEnchantItems(new Dictionary<int, EnchantItemRowData>
        {
            [501] = new EnchantItemRowData(TypeId: 30, Level: 8, GemItemId: 91234,
                new[] { new EnchantEffect(11930, 560) }),   // Haste +560
        });

        var gem = svc.GetEnchantItem(30, 8);
        Assert.NotNull(gem);
        Assert.Equal(91234, gem!.Value.GemItemId);
        Assert.Equal((11930, 560), (gem.Value.Effects[0].AttrId, gem.Value.Effects[0].Value));
        Assert.Null(svc.GetEnchantItem(30, 7));   // different level → different (or no) row
    }

    [Fact]
    public void GetAttrLib_row_without_allow_parts_matches_any_part()
    {
        var svc = new GameDataEquipService();
        svc.LoadAttrLibs(new Dictionary<int, EquipAttrLibRowData>
        {
            [1] = new EquipAttrLibRowData(5001, System.Array.Empty<int>(),
                new[] { new EquipAttrRange(11710, 100, 200) }),
        });
        Assert.Single(svc.GetAttrLib(5001, 207));   // defensive: absent AllowPart column degrades gracefully
    }
}
