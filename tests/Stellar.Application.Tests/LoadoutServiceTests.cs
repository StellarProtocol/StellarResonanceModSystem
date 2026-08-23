using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain.Inventory;
using Stellar.Abstractions.Domain.Loadout;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests;

public sealed class LoadoutServiceTests
{
    private sealed class FakeProbe : ILoadoutProbe
    {
        public bool Resolved = true;
        public List<LoadoutEntry> Entries = new();
        public int? Current;
        public int AppliedIndex = -1;
        public LoadoutResult ApplyReturns = LoadoutResult.Success;
        public LiveLoadoutState? LiveState;
        public bool LiveStateChangedFlag;
        public int ConsumeCalls;

        public bool IsResolved => Resolved;
        public IReadOnlyList<LoadoutEntry> ReadLoadouts() => Entries;
        public int? ReadCurrentIndex() => Current;
        public LiveLoadoutState? ReadLiveState() => LiveState;
        public bool ConsumeLiveStateChanged()
        {
            ConsumeCalls++;
            if (!LiveStateChangedFlag) return false;
            LiveStateChangedFlag = false;
            return true;
        }
        public Task<LoadoutResult> CallApplyAsync(int index, CancellationToken ct)
        {
            AppliedIndex = index;
            return Task.FromResult(ApplyReturns);
        }
    }

    [Fact]
    public void Tick_marks_current_slot_and_raises_changed_once()
    {
        var probe = new FakeProbe
        {
            Entries = new() { new(0, "Ici-LF"), new(1, "Tank") },
            Current = 1,
        };
        var svc = new LoadoutService(probe);
        var raised = 0;
        svc.LoadoutsChanged += () => raised++;

        svc.Tick();
        svc.Tick();   // unchanged -> no second raise

        Assert.Equal(1, raised);
        var slots = svc.GetSlots();
        Assert.Equal(2, slots.Count);
        Assert.False(slots[0].IsCurrent);
        Assert.True(slots[1].IsCurrent);
        Assert.Equal(1, svc.CurrentIndex);
    }

    [Fact]
    public void Tick_raises_again_when_selection_changes()
    {
        var probe = new FakeProbe { Entries = new() { new(0, "A"), new(1, "B") }, Current = 0 };
        var svc = new LoadoutService(probe);
        var raised = 0;
        svc.LoadoutsChanged += () => raised++;

        svc.Tick();
        probe.Current = 1;
        svc.Tick();

        Assert.Equal(2, raised);
        Assert.Equal(1, svc.CurrentIndex);
    }

    [Fact]
    public async Task ApplyAsync_passes_index_to_probe_and_returns_result()
    {
        var probe = new FakeProbe { ApplyReturns = LoadoutResult.InCombat };
        var svc = new LoadoutService(probe);

        var result = await svc.ApplyAsync(3);

        Assert.Equal(3, probe.AppliedIndex);
        Assert.Equal(LoadoutResult.InCombat, result);
    }

    [Fact]
    public void IsAvailable_reflects_probe_resolution()
    {
        var probe = new FakeProbe { Resolved = false };
        var svc = new LoadoutService(probe);
        Assert.False(svc.IsAvailable);
    }

    /// <summary>PINNED (owner ruling 2026-08-23, event-driven capture): the post-parse
    /// <c>LiveStateChanged</c> event fires ONLY when the probe reports a real difference. The
    /// container-merge signal that drives the re-read is field-agnostic, so it fires on every merge —
    /// if an identical re-read raised this event too, every consumer would re-snapshot the player's
    /// setup on every unrelated delta.</summary>
    [Fact]
    public void Tick_raises_LiveStateChanged_only_when_the_probe_reports_a_change()
    {
        var probe = new FakeProbe { Entries = new() { new(0, "A") }, Current = 0 };
        var svc = new LoadoutService(probe);
        var raised = 0;
        svc.LiveStateChanged += () => raised++;

        svc.Tick();                          // probe reports no change
        Assert.Equal(0, raised);

        probe.LiveStateChangedFlag = true;
        svc.Tick();                          // one real change -> exactly one raise
        svc.Tick();                          // flag consumed -> silent again

        Assert.Equal(1, raised);
        Assert.Equal(3, probe.ConsumeCalls);
    }

    /// <summary>PINNED: the event is raised LAST, after the slot snapshot is rebuilt — a handler that
    /// reads GetSlots()/CurrentIndex() must already see the changed setup, which is the whole point of
    /// a POST-parse event (IInventory.SelfGearChanged fires pre-parse, on the network thread, and
    /// racing it is what lost the owner's talent edit / imagine swap / setup revert).</summary>
    [Fact]
    public void Tick_raises_LiveStateChanged_after_the_slot_snapshot_is_rebuilt()
    {
        var probe = new FakeProbe { Entries = new() { new(0, "A") }, Current = 0 };
        var svc = new LoadoutService(probe);
        svc.Tick();

        probe.Entries = new() { new(0, "A"), new(1, "B") };
        probe.Current = 1;
        probe.LiveStateChangedFlag = true;

        var slotsSeenByHandler = -1;
        int? currentSeenByHandler = null;
        svc.LiveStateChanged += () =>
        {
            slotsSeenByHandler = svc.GetSlots().Count;
            currentSeenByHandler = svc.CurrentIndex;
        };

        svc.Tick();

        Assert.Equal(2, slotsSeenByHandler);
        Assert.Equal(1, currentSeenByHandler);
    }

    /// <summary>PINNED — THE owner-visible defect of staging run <c>sea/P073ErzDAx</c> (2026-08-23): a
    /// gear "Replace" swaps ONE item for another, so every count the notification signature folds in is
    /// unchanged (11 gear, 5 modules, same plan list, same selection). The served snapshot must still
    /// follow the probe, or <c>GetSlots()</c> keeps handing out the PRE-Replace ring for the rest of the
    /// session — which is exactly how the CombatMeter compared the old gear, decided "same setup", and
    /// never minted the new one (measured: probe `207:2070912` vs plugin `207:2071330`, log lines
    /// 9819/9829). Same shape for a module Replace.</summary>
    [Fact]
    public void GetSlots_follows_a_same_count_item_swap_that_the_notification_signature_cannot_see()
    {
        var oldRing = new GearInstance(207, 18598, 2071330, 5, 0, default, GearAttrRolls.Empty, null, 0);
        var newRing = new GearInstance(207, 10370, 2070912, 5, 0, default, GearAttrRolls.Empty, null, 0);
        var probe = new FakeProbe
        {
            Entries = new() { new(2, "Frost", 2, 105, new[] { 1, 2 }, new[] { oldRing }) },
            Current = 2,
        };
        var svc = new LoadoutService(probe);
        svc.Tick();
        Assert.Equal(2071330, svc.GetSlots()[0].Gear![0].ConfigId);

        // The probe re-resolved and now serves the NEW ring — same slot, same count, same everything the
        // signature looks at.
        probe.Entries = new() { new(2, "Frost", 2, 105, new[] { 1, 2 }, new[] { newRing }) };
        var raised = 0;
        svc.LoadoutsChanged += () => raised++;

        svc.Tick();

        Assert.Equal(2070912, svc.GetSlots()[0].Gear![0].ConfigId);
        Assert.Equal(0, raised);   // and it is NOT announced as a saved-list/selection change
    }

    /// <summary>PINNED: the <c>LiveStateChanged</c> handler must observe the swapped gear — the event's
    /// documented promise ("by the time it fires, GetSlots() … already describe the new setup"). This is
    /// the seam the CombatMeter's capture runs on.</summary>
    [Fact]
    public void LiveStateChanged_handler_observes_the_swapped_gear()
    {
        var probe = new FakeProbe
        {
            Entries = new() { new(2, "Frost", 2, 105, null, new[] { new GearInstance(207, 18598, 2071330, 5, 0, default, GearAttrRolls.Empty, null, 0) }) },
            Current = 2,
        };
        var svc = new LoadoutService(probe);
        svc.Tick();

        probe.Entries = new() { new(2, "Frost", 2, 105, null, new[] { new GearInstance(207, 10370, 2070912, 5, 0, default, GearAttrRolls.Empty, null, 0) }) };
        probe.LiveStateChangedFlag = true;
        var seen = 0;
        svc.LiveStateChanged += () => seen = svc.GetSlots()[0].Gear![0].ConfigId;

        svc.Tick();

        Assert.Equal(2070912, seen);
    }

    [Fact]
    public void LiveState_PassesThroughProbe_AndNullWhenUnresolved()
    {
        var probe = new FakeProbe();
        var service = new LoadoutService(probe);
        Assert.Null(service.LiveState);
        probe.LiveState = new LiveLoadoutState(5, 500, new[] { 1, 2, 3 });
        Assert.Same(probe.LiveState, service.LiveState);
    }
}
