using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// Covers <see cref="PandaLoadoutProbe"/>'s pure row-parsing logic (the
/// <c>CUR=</c> header + <c>pid\tname\tprofessionId\tcurrentTalentStageCfgId\ttalentNodes\tequipMap\tmodMap</c>
/// rows the refresh Lua chunk serializes). Pure string-in/plans-out, so no
/// Lua bridge / IL2CPP host is needed to exercise it.
///
/// <para>PINNED: <see cref="TolerantOfTheOldTwoColumnRowForm"/>. A stale
/// in-flight read can still be carrying the pre-enrichment 2-column
/// <c>pid\tname</c> form (old chunk still executing server round-trip when the
/// framework updates); the parser must default the newer fields to 0/empty
/// rather than throwing or dropping the row.</para>
///
/// <para>PINNED: <see cref="ParsesPerClassEquipAndModUuidMaps"/> — the per-class
/// gear/module uuid maps (cols 6/7) are the source the item-container resolver
/// turns into <c>LoadoutSlot.Gear</c>/<c>Modules</c>; a parse regression there
/// silently reverts per-class gear to class-blind.</para>
/// </summary>
public sealed class PandaLoadoutProbeParseTests
{
    [Fact]
    public void ParsesProfessionIdAndTalentStageIdFromFourColumnRows()
    {
        var (current, plans) = PandaLoadoutProbe.ParseLoadoutData(
            "CUR=3\n3\tAttack/Frost Mage\t2\t104\n5\tHeal\t5\t50001");

        Assert.Equal(3, current);
        Assert.Equal(2, plans.Count);
        Assert.Equal((3, "Attack/Frost Mage", 2, 104), (plans[0].Index, plans[0].Name, plans[0].ProfessionId, plans[0].TalentStageId));
        Assert.Equal((5, "Heal", 5, 50001), (plans[1].Index, plans[1].Name, plans[1].ProfessionId, plans[1].TalentStageId));
    }

    [Fact]
    public void ParsesTalentNodeIdsFromFiveColumnRows()
    {
        var (_, plans) = PandaLoadoutProbe.ParseLoadoutData(
            "CUR=3\n3\tAttack/Frost Mage\t2\t104\t233002,5205,222011");

        var plan = Assert.Single(plans);
        Assert.Equal(new[] { 233002, 5205, 222011 }, plan.TalentNodes);
    }

    [Fact]
    public void FiveColumnRowWithEmptyNodeListLeavesTalentNodesNull()
    {
        var (_, plans) = PandaLoadoutProbe.ParseLoadoutData("CUR=3\n3\tAttack\t2\t104\t");

        Assert.Null(Assert.Single(plans).TalentNodes);
    }

    [Fact]
    public void ParsesPerClassEquipAndModUuidMaps()
    {
        var (_, plans) = PandaLoadoutProbe.ParseLoadoutData(
            "CUR=1\n1\tSmite\t5\t104\t233002\t200:18178,201:17084,208:17621\t1:11499,2:9772");

        var plan = Assert.Single(plans);
        Assert.Equal(3, plan.EquipUuids.Count);
        Assert.Equal(18178L, plan.EquipUuids[200]);
        Assert.Equal(17084L, plan.EquipUuids[201]);
        Assert.Equal(17621L, plan.EquipUuids[208]);
        Assert.Equal(2, plan.ModUuids.Count);
        Assert.Equal(11499L, plan.ModUuids[1]);
        Assert.Equal(9772L, plan.ModUuids[2]);
    }

    [Fact]
    public void EmptyEquipAndModColumnsLeaveMapsEmpty()
    {
        var (_, plans) = PandaLoadoutProbe.ParseLoadoutData("CUR=1\n1\tSmite\t5\t104\t233002\t\t");

        var plan = Assert.Single(plans);
        Assert.Empty(plan.EquipUuids);
        Assert.Empty(plan.ModUuids);
    }

    [Fact]
    public void MalformedUuidPairsAreSkippedNotThrown()
    {
        var (_, plans) = PandaLoadoutProbe.ParseLoadoutData(
            "CUR=1\n1\tSmite\t5\t104\t\t200:18178,junk,:99,300:\t1:11499");

        var plan = Assert.Single(plans);
        Assert.Equal(1, plan.EquipUuids.Count);
        Assert.Equal(18178L, plan.EquipUuids[200]);
        Assert.Equal(11499L, plan.ModUuids[1]);
    }

    [Fact]
    public void TolerantOfTheOldTwoColumnRowForm()
    {
        var (current, plans) = PandaLoadoutProbe.ParseLoadoutData("CUR=1\n1\tIci-LF");

        Assert.Equal(1, current);
        var plan = Assert.Single(plans);
        Assert.Equal((1, "Ici-LF", 0, 0), (plan.Index, plan.Name, plan.ProfessionId, plan.TalentStageId));
        Assert.Empty(plan.EquipUuids);
        Assert.Empty(plan.ModUuids);
    }

    [Fact]
    public void FallsBackToPlaceholderNameWhenNameColumnIsEmpty()
    {
        var (_, plans) = PandaLoadoutProbe.ParseLoadoutData("CUR=0\n7\t\t2\t104");

        var plan = Assert.Single(plans);
        Assert.Equal("Loadout 7", plan.Name);
        Assert.Equal(2, plan.ProfessionId);
        Assert.Equal(104, plan.TalentStageId);
    }
}
