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

    // ── LIVE line (current class's live equipped set + talents) ───────────────────────────────
    // The refresh chunk appends a "LIVE\t<equip>\t<mod>\t<curProf>\t<talentStage>\t<talentNodes>" row
    // carrying the CURRENT class's live equipped set. Parsed by the pure static ParseLiveLine; the plan
    // parser skips it (its "LIVE" first column fails the int-parse). This row is the ONLY source of the
    // current class's loadout when the player has NO saved plan (owner requirement 2026-08-05).

    [Fact]
    public void ParsesLiveLineEquipModProfessionStageAndTalents()
    {
        var live = PandaLoadoutProbe.ParseLiveLine(
            "CUR=1\n1\tAtk\t4\t106\t\t\t\nLIVE\t200:2000835,201:2010937\t3:122,4:115,5:221\t4\t106\t69126,10442,1497");

        Assert.Equal(2, live.Equip.Count);
        Assert.Equal(2000835L, live.Equip[200]);
        Assert.Equal(3, live.Mod.Count);
        Assert.Equal(221L, live.Mod[5]);
        Assert.Equal(4, live.ProfessionId);
        Assert.Equal(106, live.TalentStageId);
        Assert.Equal(new[] { 69126, 10442, 1497 }, live.TalentNodes);
    }

    [Fact]
    public void ParsesLiveLineWithEmptyModulesButPopulatedTalents()
    {
        // A partial account can have gear + talents but zero equipped modules — the exact Ribery state.
        var live = PandaLoadoutProbe.ParseLiveLine("LIVE\t200:2000835\t\t4\t106\t69126,10442");

        Assert.Single(live.Equip);
        Assert.Empty(live.Mod);
        Assert.Equal(4, live.ProfessionId);
        Assert.Equal(new[] { 69126, 10442 }, live.TalentNodes);
    }

    [Fact]
    public void ToleratesTheOldThreeColumnLiveLineForm()
    {
        // A stale in-flight read can still carry the pre-talent 3-column "LIVE\t<eq>\t<mod>" form —
        // profession/stage/nodes must default to 0/0/null rather than throw or drop.
        var live = PandaLoadoutProbe.ParseLiveLine("LIVE\t200:2000835\t1:99");

        Assert.Single(live.Equip);
        Assert.Single(live.Mod);
        Assert.Equal(0, live.ProfessionId);
        Assert.Equal(0, live.TalentStageId);
        Assert.Null(live.TalentNodes);
    }

    [Fact]
    public void AbsentLiveLineYieldsEmptyLiveLoadout()
    {
        var live = PandaLoadoutProbe.ParseLiveLine("CUR=1\n1\tAtk\t4\t106");

        Assert.Empty(live.Equip);
        Assert.Empty(live.Mod);
        Assert.Equal(0, live.ProfessionId);
        Assert.Null(live.TalentNodes);
    }
}
