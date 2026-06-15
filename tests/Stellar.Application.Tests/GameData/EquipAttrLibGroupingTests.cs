using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.GameData;

public class EquipAttrLibGroupingTests
{
    // Fact 1: Malformed pair (effect too short) is skipped.
    [Fact]
    public void PairAttrEntries_malformed_effect_row_skipped()
    {
        var effects = new int[][] { new[] { 1 } };   // only 1 element — needs 2
        var configs = new int[][] { new[] { 10, 20 } };

        var result = EquipAttrLibGrouping.PairAttrEntries(effects, configs);

        Assert.Empty(result);
    }

    // Fact 2: Missing config entry defaults to min=0, max=0.
    [Fact]
    public void PairAttrEntries_missing_config_defaults_to_zero_range()
    {
        var effects = new int[][] { new[] { 1, 11112 } };
        var configs = new int[][] { };   // no config row

        var result = EquipAttrLibGrouping.PairAttrEntries(effects, configs);

        Assert.Single(result);
        Assert.Equal(11112, result[0].AttrId);
        Assert.Equal(0, result[0].Min);
        Assert.Equal(0, result[0].Max);
    }

    // Fact 3: Rows sharing an AttrLibId are kept as separate rows in input order.
    [Fact]
    public void RegroupRowsByLibId_keeps_same_lib_rows_separate_in_input_order()
    {
        // Rows sharing a lib id stay SEPARATE (one row per slot-part group) so the part-filtered
        // lookup can pick one — the old concatenating merge mixed other slots' ranges together.
        var entryA = new EquipAttrRange(11112, 10, 20);
        var entryB = new EquipAttrRange(11113, 30, 40);
        var rows = new List<EquipAttrLibRowData>
        {
            new(101, new[] { 200 }, new[] { entryA }),
            new(101, new[] { 205 }, new[] { entryB }),
        };

        var result = EquipAttrLibGrouping.RegroupByLibId(rows, r => r.AttrLibId, r => r.Entries.Length > 0);

        Assert.True(result.TryGetValue(101, out var lib));
        Assert.Equal(2, lib!.Count);
        Assert.Equal(entryA, lib[0].Entries[0]);
        Assert.Equal(new[] { 200 }, lib[0].AllowParts);
        Assert.Equal(entryB, lib[1].Entries[0]);
        Assert.Equal(new[] { 205 }, lib[1].AllowParts);
    }

    // Fact 4: Rows with AttrLibId == 0 or empty entries are skipped.
    [Fact]
    public void RegroupRowsByLibId_zero_lib_id_and_empty_entries_skipped()
    {
        var rows = new List<EquipAttrLibRowData>
        {
            new(0,   new[] { 200 }, new[] { new EquipAttrRange(11112, 1, 2) }),   // zero lib id
            new(101, new[] { 200 }, new EquipAttrRange[] { }),                     // empty entries
        };

        var result = EquipAttrLibGrouping.RegroupByLibId(rows, r => r.AttrLibId, r => r.Entries.Length > 0);

        Assert.Empty(result);
    }
}
