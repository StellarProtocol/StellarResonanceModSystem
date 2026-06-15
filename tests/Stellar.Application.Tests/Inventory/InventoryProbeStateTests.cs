using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.Inventory;

/// <summary>
/// Characterization tests for <see cref="InventoryProbeState"/> — the small
/// IL2CPP-free state object introduced by C-14 step 1 to hold the three fields
/// the pull-read and stub-capture concerns share across threads
/// (<c>_equippedSnapshot</c>, <c>_capturedCharSerialize</c>,
/// <c>_captureHookActive</c>).
///
/// <para>These pin the EXACT pre-existing sync semantics that the relocation
/// must preserve: copy-on-write whole-dictionary replacement for the equipped
/// snapshot (never an in-place mutation of a published dict), reference-latch
/// for the captured CharSerialize, and a set-once latch for the capture-hook
/// flag. The thread-visibility itself (volatile across the wire/game-thread
/// boundary) is not exercisable in a single-threaded unit test and remains
/// guarded by the in-world verify; what is pinned here is the field-by-field
/// read/publish contract that the C-14 split routes through this object.</para>
/// </summary>
public sealed class InventoryProbeStateTests
{
    [Fact]
    public void EquippedSnapshot_DefaultsToNull_BeforeFirstSync()
    {
        var state = new InventoryProbeState();
        Assert.Null(state.EquippedSnapshot);
    }

    [Fact]
    public void PublishEquippedSnapshot_ReplacesWholeDictionary_NotInPlaceMutation()
    {
        var state = new InventoryProbeState();

        var first = new Dictionary<int, long> { [1] = 100L };
        state.PublishEquippedSnapshot(first);
        Assert.Same(first, state.EquippedSnapshot);

        // A second publish swaps the reference outright (copy-on-write) — the
        // previously published dict is left untouched, exactly as
        // ApplyModSlotDelta / ReseedEquippedFromSync build a fresh dict and
        // assign it.
        var second = new Dictionary<int, long> { [1] = 100L, [2] = 200L };
        state.PublishEquippedSnapshot(second);

        Assert.Same(second, state.EquippedSnapshot);
        Assert.Single(first); // the old snapshot was never mutated
    }

    [Fact]
    public void PublishEquippedSnapshot_AcceptsNull()
    {
        var state = new InventoryProbeState();
        state.PublishEquippedSnapshot(new Dictionary<int, long> { [1] = 1L });
        state.PublishEquippedSnapshot(null);
        Assert.Null(state.EquippedSnapshot);
    }

    [Fact]
    public void CapturedCharSerialize_DefaultsToNull_AndLatchesLastWrite()
    {
        var state = new InventoryProbeState();
        Assert.Null(state.CapturedCharSerialize);

        var firstDecoded = new object();
        state.CapturedCharSerialize = firstDecoded;
        Assert.Same(firstDecoded, state.CapturedCharSerialize);

        // Method-21 full syncs overwrite the latch with the freshest decode.
        var secondDecoded = new object();
        state.CapturedCharSerialize = secondDecoded;
        Assert.Same(secondDecoded, state.CapturedCharSerialize);
    }

    [Fact]
    public void CaptureHookActive_DefaultsFalse_AndIsSetOnceToTrue()
    {
        var state = new InventoryProbeState();
        Assert.False(state.CaptureHookActive);

        state.CaptureHookActive = true;
        Assert.True(state.CaptureHookActive);
    }
}
