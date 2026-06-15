using System.Collections.Generic;
using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.GameData;

public class GameDataCombatServiceCatalogTests
{
    [Fact]
    public void GetAttribute_falls_back_to_catalog_when_live_table_misses()
    {
        var svc = new GameDataCombatService();           // no LoadAttributes → live cache empty
        var info = svc.GetAttribute(11710);
        Assert.NotNull(info);
        Assert.Equal("AttrCrit", info!.Value.EnumName);
        Assert.Equal(1, info.Value.NumType);
        Assert.Equal(AttributeGroup.Secondary, info.Value.Group);
    }

    // NOTE: these tests couple to generated catalog data (AttrCatalog.g.cs). 11710 = AttrCrit,
    // NumType 1, Secondary. A regeneration after a game patch can break them for data — not logic — reasons.
    [Fact]
    public void GetAttribute_enriches_live_row_with_catalog_fields()
    {
        var svc = new GameDataCombatService();
        svc.LoadAttributes(new Dictionary<int, AttributeInfo>
        {
            // Live NumType 0 deliberately differs from the catalog's 1 so precedence is pinned.
            [11710] = new AttributeInfo(11710, "Localized Crit", "LocCrit", "icon/path", AttributeGroup.Offensive)
            { NumType = 0, EnumName = "LiveJunk" },
        });
        var info = svc.GetAttribute(11710)!.Value;
        Assert.Equal("Localized Crit", info.Name);                  // live name wins
        Assert.Equal("LocCrit", info.ShortName);                    // live short name wins
        Assert.Equal("icon/path", info.IconPath);                   // live icon kept
        Assert.Equal("AttrCrit", info.EnumName);                    // catalog ALWAYS wins EnumName
        Assert.Equal(AttributeGroup.Offensive, info.Group);         // live non-Unknown group wins
        Assert.Equal(0, info.NumType);                              // live NumType >= 0 wins
    }

    [Fact]
    public void GetAttribute_backfills_empty_live_fields_from_catalog()
    {
        var svc = new GameDataCombatService();
        svc.LoadAttributes(new Dictionary<int, AttributeInfo>
        {
            [11710] = new AttributeInfo(11710, "", "", "", AttributeGroup.Unknown) { NumType = -1 },
        });
        var info = svc.GetAttribute(11710)!.Value;
        Assert.False(string.IsNullOrEmpty(info.Name));              // catalog name fills empty live name
        Assert.False(string.IsNullOrEmpty(info.ShortName));         // catalog short name fills too
        Assert.Equal(AttributeGroup.Secondary, info.Group);         // catalog fills Unknown group
        Assert.Equal(1, info.NumType);                              // catalog fills NumType when live is -1
    }

    [Fact]
    public void GetAttribute_still_null_for_uncatalogued_unknown_id()
        => Assert.Null(new GameDataCombatService().GetAttribute(999_999));
}
