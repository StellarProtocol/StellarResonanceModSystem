using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Game;

/// <summary>
/// PINNED — the EVENT-DRIVEN live-state re-read and its change gate (owner ruling 2026-08-23:
/// capture is event-driven at the right probe point; no polling / timer-based data gathering).
///
/// <para>Two properties are load-bearing and must never regress:</para>
/// <list type="number">
///   <item><b>Change-event ONLY on a real difference.</b> The re-read now fires on EVERY container
///   merge (field-agnostic — see <c>ContainerMergeSignalTests</c>), which is many times more often
///   than the old per-field allowlist. If an identical re-parse raised the event, every consumer
///   would re-snapshot the player's setup on every unrelated delta.</item>
///   <item><b>A failed read never blanks the latch.</b> A dump whose live section's pcall failed
///   carries no "LIVE" row; parsing that as an all-empty live loadout would wipe class + talents and
///   read as a change — the Deep-Slumber error-silent-empty-capture failure mode all over again.</item>
///   <item><b>The event is PUBLISHED FROM THE RESOLVE, never from the read.</b> The live read only
///   refreshes the slot→uuid LINE; the uuid→gear/module resolve is a second, separately-gated step.
///   Raising the change event out of the read makes <c>ILoadout.LiveStateChanged</c>'s documented
///   promise ("GetSlots() already describes the new setup") true only by call ordering — on every path
///   where the resolve bails, the consumer snapshots the PREVIOUS setup's gear, compares it equal, and
///   drops the edit. Root cause of the owner's un-minted ring/module Replace, staging run
///   <c>sea/P073ErzDAx</c> (2026-08-23).</item>
/// </list>
/// </summary>
public sealed class PandaLoadoutProbeLiveStateTests
{
    private sealed class FakeTypeRegistry : IGameTypeRegistry
    {
        public System.Type? FindType(string fullName) => null;   // bridge resolution not exercised here
    }

    private const string LiveRowA =
        "RES\t50101,50102\nRESSLOT\t7:50310003,8:50310010\n" +
        "LIVE\t200:2000835,201:2010937\t3:122,4:115\t4\t106\t69126,10442";

    // A probe whose per-class resolver produces data, i.e. the normal in-world case. Tests that want
    // the container-not-ready case flip this to false. Instance state — xUnit builds one instance per
    // test method, so nothing leaks between tests.
    private bool _resolverReady = true;

    private PandaLoadoutProbe NewProbe()
    {
        _resolverReady = true;
        var probe = new PandaLoadoutProbe(new StubLog(), new FakeTypeRegistry());
        probe.AttachGearResolver(plans =>
        {
            var results = new List<(IReadOnlyList<GearInstance>, IReadOnlyDictionary<int, ModuleInfo>)>(plans.Count);
            for (var i = 0; i < plans.Count; i++)
            {
                results.Add(_resolverReady
                    ? (new[] { new GearInstance(200, 11, 2000835, 5, 0, default, GearAttrRolls.Empty, null, 0) },
                       new Dictionary<int, ModuleInfo>())
                    : (System.Array.Empty<GearInstance>(), new Dictionary<int, ModuleInfo>()));
            }
            return results;
        });
        return probe;
    }

    private static void ApplyLiveRows(PandaLoadoutProbe probe, string raw)
    {
        var m = typeof(PandaLoadoutProbe).GetMethod("ApplyLiveRows", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(m);
        m!.Invoke(probe, new object?[] { raw, PandaLoadoutProbe.ParseResonanceSlotsLine(raw) });
    }

    // The second half of a real drain tick: turn the freshly-read slot→uuid maps into served
    // gear/modules. Nothing may publish a live-state change before this has run.
    private static void ResolvePerClassDetails(PandaLoadoutProbe probe)
    {
        var m = typeof(PandaLoadoutProbe).GetMethod("TryResolvePerClassDetails", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(m);
        m!.Invoke(probe, System.Array.Empty<object?>());
    }

    // One drain tick's worth of the live-state path: read, then resolve.
    private static void ApplyAndResolve(PandaLoadoutProbe probe, string raw)
    {
        ApplyLiveRows(probe, raw);
        ResolvePerClassDetails(probe);
    }

    private static bool ResolvePending(PandaLoadoutProbe probe)
        => (bool)typeof(PandaLoadoutProbe)
            .GetField("_resolvePending", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(probe)!;

    // ── The change gate ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void IdenticalReparse_RaisesNoChangeEvent()
    {
        var probe = NewProbe();

        ApplyAndResolve(probe, LiveRowA);
        Assert.True(((ILoadoutProbe)probe).ConsumeLiveStateChanged());   // first read IS a change

        // Byte-identical dump, applied again (the raw-string memo is bypassed here on purpose — this
        // pins the STRUCTURAL gate, which is what protects against Lua's unspecified `pairs` order).
        ApplyAndResolve(probe, LiveRowA);

        Assert.False(((ILoadoutProbe)probe).ConsumeLiveStateChanged());
    }

    [Fact]
    public void ConsumeLiveStateChanged_ReturnsTrueOnceThenFalse()
    {
        var probe = NewProbe();
        ApplyAndResolve(probe, LiveRowA);

        Assert.True(((ILoadoutProbe)probe).ConsumeLiveStateChanged());
        Assert.False(((ILoadoutProbe)probe).ConsumeLiveStateChanged());
    }

    // ── The event is published FROM the resolve, so "event ⇒ served data is fresh" holds ──────────

    /// <summary>PINNED (owner staging run sea/P073ErzDAx, 2026-08-23): reading the fresh slot→uuid line
    /// is NOT enough to raise the change event. Until the per-class resolve has turned those uuids into
    /// served gear/modules, <c>GetSlots()</c> still describes the PREVIOUS setup — publishing there is
    /// exactly what let a consumer compare stale gear, decide "same setup", and drop the edit.</summary>
    [Fact]
    public void ALiveRead_AloneDoesNotPublish_TheResolveDoes()
    {
        var probe = NewProbe();

        ApplyLiveRows(probe, LiveRowA);
        Assert.False(((ILoadoutProbe)probe).ConsumeLiveStateChanged());   // armed, not published

        ResolvePerClassDetails(probe);
        Assert.True(((ILoadoutProbe)probe).ConsumeLiveStateChanged());
    }

    /// <summary>PINNED: a resolve that CANNOT produce data (item container not synced yet) keeps the
    /// change ARMED rather than publishing it against stale gear — it is delivered LATE, on the tick the
    /// data actually lands, never WRONG. The container-merge signal re-arms <c>_resolvePending</c>, which
    /// is what drives that retry in-game.</summary>
    [Fact]
    public void AResolveThatIsNotReady_HoldsTheChangeUntilTheDataLands()
    {
        var probe = NewProbe();
        _resolverReady = false;

        ApplyAndResolve(probe, LiveRowA);
        Assert.False(((ILoadoutProbe)probe).ConsumeLiveStateChanged());

        _resolverReady = true;
        typeof(PandaLoadoutProbe).GetField("_resolvePending", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(probe, true);   // the next container merge re-arms the resolve
        ResolvePerClassDetails(probe);

        Assert.True(((ILoadoutProbe)probe).ConsumeLiveStateChanged());
    }

    [Theory]
    // A "Replace"-style gear swap: one equipped slot's item uuid changes.
    [InlineData("RES\t50101,50102\nRESSLOT\t7:50310003,8:50310010\n" +
                "LIVE\t200:2000835,201:9999999\t3:122,4:115\t4\t106\t69126,10442")]
    // A module swap in slot 4.
    [InlineData("RES\t50101,50102\nRESSLOT\t7:50310003,8:50310010\n" +
                "LIVE\t200:2000835,201:2010937\t3:122,4:777\t4\t106\t69126,10442")]
    // A class switch.
    [InlineData("RES\t50101,50102\nRESSLOT\t7:50310003,8:50310010\n" +
                "LIVE\t200:2000835,201:2010937\t3:122,4:115\t9\t106\t69126,10442")]
    // A talent STAGE switch.
    [InlineData("RES\t50101,50102\nRESSLOT\t7:50310003,8:50310010\n" +
                "LIVE\t200:2000835,201:2010937\t3:122,4:115\t4\t107\t69126,10442")]
    // A SINGLE talent node activated — the owner-visible miss that started this rework.
    [InlineData("RES\t50101,50102\nRESSLOT\t7:50310003,8:50310010\n" +
                "LIVE\t200:2000835,201:2010937\t3:122,4:115\t4\t106\t69126,10442,1497")]
    // A Battle Imagine swap ALONE (hotbar slot 8) — nothing in the LIVE row moves.
    [InlineData("RES\t50101,50102\nRESSLOT\t7:50310003,8:50319999\n" +
                "LIVE\t200:2000835,201:2010937\t3:122,4:115\t4\t106\t69126,10442")]
    public void ARealEdit_RaisesTheChangeEvent_AndReArmsThePerClassResolve(string edited)
    {
        var probe = NewProbe();
        ApplyAndResolve(probe, LiveRowA);
        ((ILoadoutProbe)probe).ConsumeLiveStateChanged();   // drain the first-read change
        typeof(PandaLoadoutProbe).GetField("_resolvePending", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(probe, false);

        ApplyLiveRows(probe, edited);
        Assert.True(ResolvePending(probe));   // the read re-arms the resolve …
        ResolvePerClassDetails(probe);        // … and the resolve publishes the change

        Assert.True(((ILoadoutProbe)probe).ConsumeLiveStateChanged());
    }

    // ── Never blank the latch on a failed read ────────────────────────────────────────────────

    [Fact]
    public void ADumpWithNoLiveRow_KeepsTheLatchedLiveState_AndRaisesNothing()
    {
        var probe = NewProbe();
        ApplyAndResolve(probe, LiveRowA);
        ((ILoadoutProbe)probe).ConsumeLiveStateChanged();
        var before = ((ILoadoutProbe)probe).ReadLiveState();
        Assert.NotNull(before);

        // The chunk's live section pcall failed — it appends LIVEERR INSTEAD of the LIVE row.
        ApplyAndResolve(probe,
            "RES\t50101,50102\nRESSLOT\t7:50310003,8:50310010\nLIVEERR\tattempt to index a nil value");

        var after = ((ILoadoutProbe)probe).ReadLiveState();
        Assert.NotNull(after);
        Assert.Equal(before!.ProfessionId, after!.ProfessionId);
        Assert.Equal(before.TalentStageId, after.TalentStageId);
        Assert.False(((ILoadoutProbe)probe).ConsumeLiveStateChanged());
    }

    [Fact]
    public void HasLiveRow_DistinguishesAFailedReadFromAnEmptySetup()
    {
        Assert.True(PandaLoadoutProbe.HasLiveRow("RES\t\nLIVE\t\t\t0\t0\t"));   // genuinely empty setup
        Assert.False(PandaLoadoutProbe.HasLiveRow("RES\t\nLIVEERR\tboom"));      // section failed
        Assert.False(PandaLoadoutProbe.HasLiveRow("CUR=1\n1\tAtk\t4\t106"));     // old dump, no live section
    }

    // ── The pure comparers ────────────────────────────────────────────────────────────────────

    [Fact]
    public void LiveStateDiffers_FalseForTwoIndependentParsesOfTheSameRow()
    {
        var a = PandaLoadoutProbe.ParseLiveLine(LiveRowA);
        var b = PandaLoadoutProbe.ParseLiveLine(LiveRowA);

        Assert.False(PandaLoadoutProbe.LiveStateDiffers(a, b));
    }

    [Fact]
    public void SameUuidMap_IsOrderInsensitive_BecauseLuaPairsOrderIsUnspecified()
    {
        var a = new Dictionary<int, long> { [200] = 111, [201] = 222 };
        var b = new Dictionary<int, long> { [201] = 222, [200] = 111 };

        Assert.True(PandaLoadoutProbe.SameUuidMap(a, b));
        Assert.False(PandaLoadoutProbe.SameUuidMap(a, new Dictionary<int, long> { [200] = 111 }));
        Assert.False(PandaLoadoutProbe.SameUuidMap(a, new Dictionary<int, long> { [200] = 111, [201] = 999 }));
    }

    [Fact]
    public void SameIntList_IsOrderSensitive_AndTreatsNullAsEmpty()
    {
        Assert.True(PandaLoadoutProbe.SameIntList(null, null));
        Assert.True(PandaLoadoutProbe.SameIntList(null, System.Array.Empty<int>()));
        Assert.True(PandaLoadoutProbe.SameIntList(new[] { 1, 2, 3 }, new[] { 1, 2, 3 }));
        Assert.False(PandaLoadoutProbe.SameIntList(new[] { 1, 2, 3 }, new[] { 3, 2, 1 }));
        Assert.False(PandaLoadoutProbe.SameIntList(new[] { 1, 2 }, new[] { 1, 2, 3 }));
    }
}
