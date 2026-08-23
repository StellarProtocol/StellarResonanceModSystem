using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain.DeepSlumber;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// PINNED — Deep-Slumber (Psychoscope) membership in the live-build CHANGE EVENT.
///
/// <para>Owner ruling (CLAUDE.md, verbatim): <em>"when any equipment change such as
/// module,talents,equipments,slumberdream etc., and use have a combat with that setup it require plugin
/// to take snapshot of it even class has no change."</em> Owner staging run <c>sea/dXkw1PSyOG</c>
/// (2026-08-23) is the failure this pins: a Deep-Slumber factor was UNEQUIPPED before one archive and
/// RE-EQUIPPED before the next, the framework re-read the state correctly both times (the in-game panels
/// updated), but nothing downstream was told — <c>ApplyLiveRows</c> only compares the LIVE row and the
/// imagine hotbar — so the CombatMeter never re-captured and the run uploaded ONE setup
/// (<c>actors[81789846144].loadouts.length == 1</c>, one activation stamp) for two materially different
/// builds.</para>
///
/// <para>Three properties are load-bearing:</para>
/// <list type="number">
///   <item><b>A real DS change raises the event — exactly once.</b></item>
///   <item><b>An identical re-parse raises NOTHING, whatever order the maps serialize in.</b> Every
///   level of the DS walk iterates a zcontainer map with Lua <c>pairs</c> (unspecified order), so a
///   sequence compare would fire a phantom change on every container delta and make every consumer
///   re-snapshot the player's build.</item>
///   <item><b>A null read is NO-SIGNAL in both directions.</b> <c>ParseDeepSlumber</c> returns null when
///   the dump carries no "DSLV" row (bridge unresolved / stale in-flight read) — "not read yet", never
///   "the player cleared their psychoscope".</item>
/// </list>
/// </summary>
public sealed class PandaLoadoutProbeDeepSlumberChangeTests
{
    private sealed class FakeTypeRegistry : IGameTypeRegistry
    {
        public Type? FindType(string fullName) => null;   // bridge resolution not exercised here
    }

    // ── The pure structural gate ──────────────────────────────────────────────────────────────

    private static DeepSlumberState StateA() => new(
        new[] { new[] { 2, 100 }, new[] { 3, 65 } },
        new[]
        {
            new DeepSlumberLine(2, 800522, new[]
            {
                new DeepSlumberArea(1, true, 46,
                    new[] { new[] { 24, 3950 }, new[] { 25, 3905 } },
                    new[] { new[] { 100, 20010940 }, new[] { 101, 20010930 } },
                    new[] { new[] { 1008, 1 }, new[] { 1001, 1 } }),
                new DeepSlumberArea(5, false, 20,
                    Array.Empty<int[]>(), new[] { new[] { 140, 20010881 } }, new[] { new[] { 1403, 1 } }),
            }),
            // The owner's real character carries TWO subType variants under ONE lineId — the compare
            // must key on the PAIR, not the line id alone.
            new DeepSlumberLine(2, 800523, new[]
            {
                new DeepSlumberArea(20002, true, 0,
                    Array.Empty<int[]>(), new[] { new[] { 193, 20010224 } }, new[] { new[] { 5105, 1 } }),
            }),
        });

    // Byte-different SERIALIZATION of the very same state: every map's pair order permuted, lines and
    // areas reordered. This is what Lua `pairs` legitimately does between two reads.
    private static DeepSlumberState StateAReordered() => new(
        new[] { new[] { 3, 65 }, new[] { 2, 100 } },
        new[]
        {
            new DeepSlumberLine(2, 800523, new[]
            {
                new DeepSlumberArea(20002, true, 0,
                    Array.Empty<int[]>(), new[] { new[] { 193, 20010224 } }, new[] { new[] { 5105, 1 } }),
            }),
            new DeepSlumberLine(2, 800522, new[]
            {
                new DeepSlumberArea(5, false, 20,
                    Array.Empty<int[]>(), new[] { new[] { 140, 20010881 } }, new[] { new[] { 1403, 1 } }),
                new DeepSlumberArea(1, true, 46,
                    new[] { new[] { 25, 3905 }, new[] { 24, 3950 } },
                    new[] { new[] { 101, 20010930 }, new[] { 100, 20010940 } },
                    new[] { new[] { 1001, 1 }, new[] { 1008, 1 } }),
            }),
        });

    [Fact]
    public void IdenticalState_IsNotADifference()
        => Assert.False(PandaLoadoutProbe.DeepSlumberStateDiffers(StateA(), StateA()));

    [Fact]
    public void ReorderedSerializationOfTheSameState_IsNotADifference()
        => Assert.False(PandaLoadoutProbe.DeepSlumberStateDiffers(StateA(), StateAReordered()));

    [Fact]
    public void NullOnEitherSide_IsNoSignal_NeverADifference()
    {
        Assert.False(PandaLoadoutProbe.DeepSlumberStateDiffers(null, StateA()));
        Assert.False(PandaLoadoutProbe.DeepSlumberStateDiffers(StateA(), null));
        Assert.False(PandaLoadoutProbe.DeepSlumberStateDiffers(null, null));
    }

    /// <summary>THE OWNER SCENARIO, at the gate: a socketed middle-node factor removed (the
    /// unequip half of run <c>sea/dXkw1PSyOG</c>) and put back.</summary>
    [Fact]
    public void UnEquippingAFactor_IsADifference_AndReEquippingItComesBack()
    {
        var equipped = StateA();
        var unequipped = WithFirstMiddleNode(equipped, itemId: 0);

        Assert.True(PandaLoadoutProbe.DeepSlumberStateDiffers(equipped, unequipped));
        Assert.True(PandaLoadoutProbe.DeepSlumberStateDiffers(unequipped, equipped));
        Assert.False(PandaLoadoutProbe.DeepSlumberStateDiffers(unequipped, WithFirstMiddleNode(StateA(), 0)));
    }

    [Fact]
    public void TogglingAnAreaActive_IsADifference()
    {
        var before = StateA();
        var line = before.Lines[0];
        var area = line.Areas[1];
        var after = new DeepSlumberState(before.SeasonLevels, new[]
        {
            new DeepSlumberLine(line.LineId, line.SubType,
                new[] { line.Areas[0], area with { IsActive = !area.IsActive } }),
            before.Lines[1],
        });

        Assert.True(PandaLoadoutProbe.DeepSlumberStateDiffers(before, after));
    }

    [Fact]
    public void ADroppedLineVariant_IsADifference()
    {
        var before = StateA();
        var after = new DeepSlumberState(before.SeasonLevels, new[] { before.Lines[0] });
        Assert.True(PandaLoadoutProbe.DeepSlumberStateDiffers(before, after));
    }

    [Fact]
    public void ASeasonLevelUp_IsADifference()
    {
        var before = StateA();
        var after = new DeepSlumberState(new[] { new[] { 2, 101 }, new[] { 3, 65 } }, before.Lines);
        Assert.True(PandaLoadoutProbe.DeepSlumberStateDiffers(before, after));
    }

    /// <summary>A pair map that gained a key with the same COUNT on one side must still differ —
    /// counting matches alone would let {1:1, 2:2} compare equal to {1:1, 1:1}.</summary>
    [Fact]
    public void SamePairMap_IsAKeyedCompare_NotACount()
    {
        Assert.False(PandaLoadoutProbe.SamePairMap(
            new[] { new[] { 1, 1 }, new[] { 2, 2 } },
            new[] { new[] { 1, 1 }, new[] { 1, 1 } }));
        Assert.True(PandaLoadoutProbe.SamePairMap(
            new[] { new[] { 2, 2 }, new[] { 1, 1 } },
            new[] { new[] { 1, 1 }, new[] { 2, 2 } }));
    }

    private static DeepSlumberState WithFirstMiddleNode(DeepSlumberState state, int itemId)
    {
        var line = state.Lines[0];
        var area = line.Areas[0];
        var mid = new List<int[]>();
        for (var i = 0; i < area.MiddleNodes.Count; i++)
            mid.Add(i == 0 ? new[] { area.MiddleNodes[i][0], itemId } : area.MiddleNodes[i]);
        var rewritten = new List<DeepSlumberArea> { area with { MiddleNodes = mid } };
        for (var i = 1; i < line.Areas.Count; i++) rewritten.Add(line.Areas[i]);
        var lines = new List<DeepSlumberLine> { new(line.LineId, line.SubType, rewritten) };
        for (var i = 1; i < state.Lines.Count; i++) lines.Add(state.Lines[i]);
        return new DeepSlumberState(state.SeasonLevels, lines);
    }

    // ── End-to-end through the probe: parse → arm → publish on the resolve ────────────────────

    // The live half of a refresh dump, with NO Deep-Slumber rows at all — the state a session is in
    // before the refresh chunk's DS section has run (ParseDeepSlumber returns null without a "DSLV" row).
    private const string DumpBase =
        "0\tPlan\t4\t106\t200:2000835\t3:122\n" +
        "RES\t50101,50102\nRESSLOT\t7:50310003,8:50310010\n" +
        "LIVE\t200:2000835\t3:122\t4\t106\t69126,10442\n";

    private const string DsRowFactorEquipped =
        "DSLV\t2:100,3:65\n" +
        "DSA\t2\t800522\t1\t1\t46\t24:3950\t100:20010940,101:20010930\t1008:1\nDSN\t1";

    // The SAME area with the middle-node factor removed (itemId 0) — the owner's unequip.
    private const string DsRowFactorRemoved =
        "DSLV\t2:100,3:65\n" +
        "DSA\t2\t800522\t1\t1\t46\t24:3950\t100:0,101:20010930\t1008:1\nDSN\t1";

    private static PandaLoadoutProbe NewProbe()
    {
        var probe = new PandaLoadoutProbe(new StubLog(), new FakeTypeRegistry());
        probe.AttachGearResolver(plans =>
        {
            var results = new List<(IReadOnlyList<GearInstance>, IReadOnlyDictionary<int, ModuleInfo>)>(plans.Count);
            for (var i = 0; i < plans.Count; i++)
                results.Add((new[] { new GearInstance(200, 11, 2000835, 5, 0, default, GearAttrRolls.Empty, null, 0) },
                             new Dictionary<int, ModuleInfo>()));
            return results;
        });
        return probe;
    }

    private static void ApplyLiveRows(PandaLoadoutProbe probe, string raw)
        => typeof(PandaLoadoutProbe)
            .GetMethod("ApplyLiveRows", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(probe, new object?[] { raw, PandaLoadoutProbe.ParseResonanceSlotsLine(raw) });

    private static void UpdateDeepSlumberState(PandaLoadoutProbe probe, string raw)
        => typeof(PandaLoadoutProbe)
            .GetMethod("UpdateDeepSlumberState", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(probe, new object?[] { raw });

    private static void ResolvePerClassDetails(PandaLoadoutProbe probe)
    {
        typeof(PandaLoadoutProbe)
            .GetField("_resolvePending", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(probe, true);   // ParseLoadoutData re-arms this on every changed dump
        typeof(PandaLoadoutProbe)
            .GetMethod("TryResolvePerClassDetails", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(probe, Array.Empty<object?>());
    }

    /// <summary>One real drain tick over a changed dump, in the SAME order the probe runs it:
    /// <c>ParseLoadoutData</c> applies the live rows, then updates the Deep-Slumber state, and
    /// <c>DrainPendingCompletions</c> then resolves the per-class details — which is where any armed
    /// change is published.</summary>
    private static void ParsePass(PandaLoadoutProbe probe, string raw)
    {
        ApplyLiveRows(probe, raw);
        UpdateDeepSlumberState(probe, raw);
        ResolvePerClassDetails(probe);
    }

    private static bool ConsumeChange(PandaLoadoutProbe probe)
        => ((ILoadoutProbe)probe).ConsumeLiveStateChanged();

    // A probe already past its first live read (whose change is drained), with no Deep-Slumber state
    // yet — the state a session is in before the refresh chunk's DS section has run.
    private static PandaLoadoutProbe WarmProbe()
    {
        var probe = NewProbe();
        ParsePass(probe, DumpBase);
        ConsumeChange(probe);
        return probe;
    }

    /// <summary>THE OWNER SCENARIO end-to-end (staging run <c>sea/dXkw1PSyOG</c>): the first DS read is
    /// no-signal→signal (silent), the unequip raises the event ONCE, an identical re-parse raises
    /// nothing, and the re-equip raises it again. Nothing else in the dump moves in any of these
    /// passes — only the psychoscope.</summary>
    [Fact]
    public void ADeepSlumberEdit_RaisesTheChangeEventOnce_AndAnIdenticalReparseRaisesNothing()
    {
        var probe = WarmProbe();

        ParsePass(probe, DumpBase + DsRowFactorEquipped);
        Assert.False(ConsumeChange(probe));   // FIRST DS read: no-signal → signal is not a change

        ParsePass(probe, DumpBase + DsRowFactorRemoved);
        Assert.True(ConsumeChange(probe));    // the unequip IS a change …
        Assert.False(ConsumeChange(probe));   // … delivered exactly once

        ParsePass(probe, DumpBase + DsRowFactorRemoved);
        Assert.False(ConsumeChange(probe));   // identical re-parse: nothing

        ParsePass(probe, DumpBase + DsRowFactorEquipped);
        Assert.True(ConsumeChange(probe));    // the re-equip is a change again
    }

    /// <summary>PINNED: a dump with NO "DSLV" row (bridge unresolved / stale in-flight read) can never
    /// itself raise the event — in either direction.</summary>
    [Fact]
    public void ADumpWithNoDslvRow_RaisesNothing()
    {
        var probe = WarmProbe();

        ParsePass(probe, DumpBase + DsRowFactorEquipped);
        Assert.False(ConsumeChange(probe));   // null → state is no-signal, not a change

        ParsePass(probe, DumpBase);
        Assert.False(ConsumeChange(probe));   // state → null is no-signal too
    }

    /// <summary>PINNED, same rule the live rows follow: the DS change is ARMED by the parse and
    /// PUBLISHED by the per-class resolve, so <c>ILoadout.LiveStateChanged</c>'s promise ("the setup I
    /// can read right now is the new one") stays true by construction rather than by call ordering.</summary>
    [Fact]
    public void TheDeepSlumberChange_IsPublishedByTheResolve_NotByTheParse()
    {
        var probe = WarmProbe();
        ParsePass(probe, DumpBase + DsRowFactorEquipped);
        ConsumeChange(probe);

        ApplyLiveRows(probe, DumpBase + DsRowFactorRemoved);
        UpdateDeepSlumberState(probe, DumpBase + DsRowFactorRemoved);
        Assert.False(ConsumeChange(probe));   // armed, not published

        ResolvePerClassDetails(probe);
        Assert.True(ConsumeChange(probe));
    }

    /// <summary>DOCUMENTED, deliberate asymmetry with the CONSUMER's rule: a "DSLV"-but-no-"DSA" dump
    /// (the cultivate walk's pcall failed — a "DSERR" read) parses to a real, EMPTY state, so healing it
    /// DOES raise the framework event. That is the safe direction — an extra signal costs one local
    /// re-read, a missed one loses the player's setup — and the consumer absorbs it: CombatMeter's
    /// setup identity treats an empty Deep-Slumber read as NO-SIGNAL, so the heal re-captures and
    /// compares identical rather than minting a phantom setup (mirrors the imagine-sentinel rule).
    /// Only a truly ABSENT read (no "DSLV" row at all → null) is no-signal here.</summary>
    [Fact]
    public void AnEmptyWalkHealingIntoRealLines_DoesRaise_TheConsumerAbsorbsIt()
    {
        var probe = WarmProbe();

        ParsePass(probe, DumpBase + "DSLV\t2:100,3:65\nDSN\t0\nDSERR\tcultivateLines\tboom");
        Assert.False(ConsumeChange(probe));   // null → empty is still no-signal

        ParsePass(probe, DumpBase + DsRowFactorEquipped);
        Assert.True(ConsumeChange(probe));    // empty → populated is a real structural difference
    }

    /// <summary>A logout must not leak the previous character's psychoscope, nor an un-consumed change
    /// armed by it.</summary>
    [Fact]
    public void ClearSession_DropsTheStateAndAnyArmedChange()
    {
        var probe = WarmProbe();
        ParsePass(probe, DumpBase + DsRowFactorEquipped);
        ConsumeChange(probe);
        UpdateDeepSlumberState(probe, DumpBase + DsRowFactorRemoved);   // armed, not yet published

        probe.ClearSession();

        Assert.Null(((IDeepSlumberProbe)probe).Read());
        ResolvePerClassDetails(probe);
        Assert.False(ConsumeChange(probe));
    }
}
