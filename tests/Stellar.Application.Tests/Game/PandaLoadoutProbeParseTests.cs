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

    // ── Deep-Slumber (season cultivate) rows — the Lua-bridge read (owner-verified gap 2026-08-19:
    // the C# CharSerialize mirror populates lazily; this reads the login-populated Lua mirror instead
    // via the SAME refresh chunk/global) ──────────────────────────────────────────────────────────

    [Fact]
    public void ParsesDeepSlumberHappyPathWithTwoLinesAndOneInactiveArea()
    {
        var state = PandaLoadoutProbe.ParseDeepSlumber(
            "CUR=1\n1\tAtk\t2\t104\n" +
            "DSLV\t93:65,94:10\n" +
            "DSA\t93\t3\t1\t1\t120\t11:5110001\t\t21:4\n" +
            "DSA\t93\t3\t2\t0\t0\t\t\t\n" +
            "DSA\t94\t1\t1\t1\t50\t\t100:2000\t");

        Assert.NotNull(state);
        Assert.Equal(2, state!.SeasonLevels.Count);
        Assert.Equal(new[] { 93, 65 }, state.SeasonLevels[0]);
        Assert.Equal(new[] { 94, 10 }, state.SeasonLevels[1]);

        Assert.Equal(2, state.Lines.Count);   // two distinct (lineId, subType) groups
        var line93 = state.Lines[0];
        Assert.Equal(93, line93.LineId);
        Assert.Equal(3, line93.SubType);
        Assert.Equal(2, line93.Areas.Count);

        var activeArea = line93.Areas[0];
        Assert.Equal(1, activeArea.AreaId);
        Assert.True(activeArea.IsActive);
        Assert.Equal(120, activeArea.Score);
        Assert.Equal(new[] { 11, 5110001 }, Assert.Single(activeArea.BigNodes));
        Assert.Empty(activeArea.MiddleNodes);
        Assert.Equal(new[] { 21, 4 }, Assert.Single(activeArea.NormalNodes));

        var inactiveArea = line93.Areas[1];
        Assert.Equal(2, inactiveArea.AreaId);
        Assert.False(inactiveArea.IsActive);
        Assert.Equal(0, inactiveArea.Score);
        Assert.Empty(inactiveArea.BigNodes);
        Assert.Empty(inactiveArea.MiddleNodes);
        Assert.Empty(inactiveArea.NormalNodes);

        var line94 = state.Lines[1];
        Assert.Equal(94, line94.LineId);
        Assert.Equal(1, line94.SubType);
        var area94 = Assert.Single(line94.Areas);
        Assert.Equal(1, area94.AreaId);
        Assert.True(area94.IsActive);
        Assert.Equal(50, area94.Score);
        Assert.Empty(area94.BigNodes);
        Assert.Equal(new[] { 100, 2000 }, Assert.Single(area94.MiddleNodes));
        Assert.Empty(area94.NormalNodes);
    }

    [Fact]
    public void AbsentDslvRowYieldsNullDeepSlumberState()
    {
        // An OLD dump predating this enrichment (no DSLV/DSA rows at all) must parse to null — not an
        // empty-but-real state — so IDeepSlumber correctly reports "not read yet", never "genuinely
        // empty".
        var state = PandaLoadoutProbe.ParseDeepSlumber("CUR=1\n1\tAtk\t2\t104\nLIVE\t\t\t2\t104\t");

        Assert.Null(state);
    }

    [Fact]
    public void EmptyDslvPayloadWithNoDsaRowsYieldsGenuinelyEmptyState()
    {
        // PINNED CHOICE: the refresh chunk ALWAYS emits the "DSLV" row, even with an empty payload —
        // its PRESENCE (not its content) is what distinguishes a genuinely-empty season state from an
        // old dump. No DSA rows either → an all-empty (never null) DeepSlumberState.
        var state = PandaLoadoutProbe.ParseDeepSlumber("CUR=1\n1\tAtk\t2\t104\nDSLV\t");

        Assert.NotNull(state);
        Assert.Empty(state!.SeasonLevels);
        Assert.Empty(state.Lines);
    }

    [Fact]
    public void MalformedDeepSlumberNodePairsAreSkippedNotThrown()
    {
        var state = PandaLoadoutProbe.ParseDeepSlumber(
            "DSLV\t93:65\n" +
            "DSA\t93\t3\t1\t1\t120\t11:5110001,junk,:99,22:\t\t");

        var area = Assert.Single(Assert.Single(state!.Lines).Areas);
        Assert.Equal(new[] { 11, 5110001 }, Assert.Single(area.BigNodes));
    }

    [Fact]
    public void MalformedDeepSlumberIdColumnsDropTheWholeRowNotThrown()
    {
        var state = PandaLoadoutProbe.ParseDeepSlumber(
            "DSLV\t93:sixty-five,94:10\n" +
            "DSA\t93\tX\t1\t1\t120\n" +          // non-numeric subType -> dropped
            "DSA\tnotanumber\t3\t1\t1\t120\n" +  // non-numeric lineId -> dropped
            "DSA\t95\t1\tnotanumber\n" +         // non-numeric areaId -> dropped
            "DSA\t95\t1");                       // too few columns (<4) -> dropped

        Assert.NotNull(state);
        Assert.Equal(new[] { 94, 10 }, Assert.Single(state!.SeasonLevels));   // "93:sixty-five" skipped
        Assert.Empty(state.Lines);
    }

    // ── "DSN"/"DSERR" diagnostic rows (Task: DS iteration fix, owner run sea/O1jJepsgKC, 2026-08-20) —
    // the root cause was the game's zcontainer __pairs yielding nil values, so a plain "for k,v in
    // pairs(m)" walk produced nothing with no error at all. The fixed chunk now walks keys-then-index
    // and always appends "DSN\t<lineCount>" plus any "DSERR\t<section>\t<msg>" pcall failures. Neither
    // row kind may affect ParseDeepSlumber's state-building — they are diagnostics-only, read back via
    // ParseDeepSlumberDiagnosticRows instead. ─────────────────────────────────────────────────────────

    [Fact]
    public void DsnAndDserrRowsAreIgnoredByParseDeepSlumberStateBuilding()
    {
        var state = PandaLoadoutProbe.ParseDeepSlumber(
            "DSLV\t93:65\n" +
            "DSN\t1\n" +
            "DSA\t93\t3\t1\t1\t120\t11:5110001\t\t21:4\n" +
            "DSERR\tcultivateLines\tsome lua error");

        Assert.NotNull(state);
        Assert.Equal(new[] { 93, 65 }, Assert.Single(state!.SeasonLevels));
        var area = Assert.Single(Assert.Single(state.Lines).Areas);
        Assert.Equal(new[] { 11, 5110001 }, Assert.Single(area.BigNodes));
    }

    [Fact]
    public void ParsesDeepSlumberDiagnosticRowsLineCountAndErrors()
    {
        var (lineCount, errors) = PandaLoadoutProbe.ParseDeepSlumberDiagnosticRows(
            "DSLV\t93:65\n" +
            "DSN\t7\n" +
            "DSERR\tseasonLevel\tattempt to index a nil value\n" +
            "DSERR\tcultivateLines\ttoo many results to unpack");

        Assert.Equal(7, lineCount);
        Assert.Equal(2, errors.Count);
        Assert.Equal("seasonLevel\tattempt to index a nil value", errors[0]);
        Assert.Equal("cultivateLines\ttoo many results to unpack", errors[1]);
    }

    [Fact]
    public void AbsentDsnRowYieldsNullLineCountAndNoErrors()
    {
        // An OLD dump predating this enrichment (no DSN/DSERR rows at all) must parse to a null line
        // count and an empty error list, never throw.
        var (lineCount, errors) = PandaLoadoutProbe.ParseDeepSlumberDiagnosticRows("DSLV\t93:65\nDSA\t93\t3\t1\t1\t120");

        Assert.Null(lineCount);
        Assert.Empty(errors);
    }
}
