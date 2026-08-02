using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Covers <see cref="PandaLoadoutProbe"/>'s pure row-parsing logic (the
/// <c>CUR=</c> header + <c>pid\tname\tprofessionId\tcurrentTalentStageCfgId</c>
/// rows the refresh Lua chunk serializes). Pure string-in/entries-out, so no
/// Lua bridge / IL2CPP host is needed to exercise it.
///
/// <para>PINNED: <see cref="TolerantOfTheOldTwoColumnRowForm"/>. A stale
/// in-flight read can still be carrying the pre-enrichment 2-column
/// <c>pid\tname</c> form (old chunk still executing server round-trip when the
/// framework updates); the parser must default the two new fields to 0 rather
/// than throwing or dropping the row.</para>
/// </summary>
public sealed class PandaLoadoutProbeParseTests
{
    [Fact]
    public void ParsesProfessionIdAndTalentStageIdFromFourColumnRows()
    {
        var (current, entries) = PandaLoadoutProbe.ParseLoadoutData(
            "CUR=3\n3\tAttack/Frost Mage\t2\t104\n5\tHeal\t5\t50001");

        Assert.Equal(3, current);
        Assert.Equal(2, entries.Count);
        Assert.Equal(new LoadoutEntry(3, "Attack/Frost Mage", 2, 104), entries[0]);
        Assert.Equal(new LoadoutEntry(5, "Heal", 5, 50001), entries[1]);
    }

    [Fact]
    public void TolerantOfTheOldTwoColumnRowForm()
    {
        var (current, entries) = PandaLoadoutProbe.ParseLoadoutData("CUR=1\n1\tIci-LF");

        Assert.Equal(1, current);
        var entry = Assert.Single(entries);
        Assert.Equal(new LoadoutEntry(1, "Ici-LF", 0, 0), entry);
    }

    [Fact]
    public void FallsBackToPlaceholderNameWhenNameColumnIsEmpty()
    {
        var (_, entries) = PandaLoadoutProbe.ParseLoadoutData("CUR=0\n7\t\t2\t104");

        var entry = Assert.Single(entries);
        Assert.Equal("Loadout 7", entry.Name);
        Assert.Equal(2, entry.ProfessionId);
        Assert.Equal(104, entry.TalentStageId);
    }
}
