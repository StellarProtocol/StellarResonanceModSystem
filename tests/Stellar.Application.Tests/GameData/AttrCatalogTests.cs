using Stellar.Abstractions.Domain.GameData;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.GameData;

public class AttrCatalogTests
{
    [Theory]
    [InlineData(11710, "AttrCrit",       1, AttributeGroup.Secondary)]    // percent, ÷100
    [InlineData(11110, "AttrCri",        0, AttributeGroup.Secondary)]    // flat rating
    [InlineData(11330, "AttrAttack",     0, AttributeGroup.Core)]
    [InlineData(11750, "AttrSkillCD",    2, AttributeGroup.Offensive)]    // milliseconds
    [InlineData(10030, "AttrFightPoint", 0, AttributeGroup.Progression)]
    [InlineData(13110, "AttrFireDamage", 1, AttributeGroup.ElementalBonus)]
    [InlineData(200,   "AttrEquipData", -2, AttributeGroup.Identity)]     // non-stat
    public void Catalog_maps_known_ids(int id, string enumName, int numType, AttributeGroup group)
    {
        var info = AttrCatalog.TryGet(id);
        Assert.NotNull(info);
        Assert.Equal(enumName, info!.Value.EnumName);
        Assert.Equal(numType, info.Value.NumType);
        Assert.Equal(group, info.Value.Group);
        Assert.False(string.IsNullOrEmpty(info.Value.Name));
    }

    [Fact]
    public void Deprecated_ids_are_flagged()
    {
        Assert.Equal(AttributeGroup.Deprecated, AttrCatalog.TryGet(11160)!.Value.Group);
    }

    [Fact]
    public void Unknown_id_returns_null() => Assert.Null(AttrCatalog.TryGet(999_999));

    [Fact]
    public void Every_entry_has_nonempty_name_and_enum_name()
    {
        foreach (var e in AttrCatalog.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.Name), $"entry {e.Id} has empty Name");
            Assert.False(string.IsNullOrWhiteSpace(e.EnumName), $"entry {e.Id} has empty EnumName");
        }
    }

    [Fact]
    public void Entry_ids_are_unique()
    {
        var seen = new System.Collections.Generic.HashSet<int>();
        foreach (var e in AttrCatalog.Entries)
            Assert.True(seen.Add(e.Id), $"duplicate catalog id {e.Id}");
    }

    [Fact]
    public void Placeholder_official_name_never_leaks()
    {
        foreach (var e in AttrCatalog.Entries)
            if (e.Id != 10000)
                Assert.NotEqual("AttrLevel", e.Name);
    }
}
