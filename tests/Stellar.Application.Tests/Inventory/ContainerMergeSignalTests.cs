using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Inventory;

/// <summary>
/// PINNED — the FIELD-AGNOSTIC container-merge signal (owner ruling 2026-08-23: capture is
/// event-driven at the right probe point, no polling).
///
/// <para>The regression these exist for: the framework used to re-read the player's live build state
/// only when a method-22 delta's top-level fields intersected an allowlist (CharSerialize 12 equip /
/// 28 resonance / 57 mod / 61 professionList / 101 seasonCultivate). The gear UI's <b>"Replace"</b>
/// button emits a delta whose top-level fields are <b>2 / 55 / 96 / 104</b> — none of those five — so
/// the edit was invisible: no re-read, no change event, and the next archive carried the PRE-replace
/// setup. <see cref="ContainerDirtyDeltaReader.IsMergeSignal"/> replaced the allowlist with "a
/// structurally-valid, non-empty CharSerialize delta arrived", because every CharSerialize update
/// funnels through one merge service, so the ARRIVAL is the event — not the field list.</para>
///
/// <para>The <c>Touches*</c> assertions below are the load-bearing half: they prove the measured
/// Replace delta really would have been dropped by the old gate, so this file fails if anyone
/// re-narrows the trigger back to a per-field allowlist.</para>
/// </summary>
public sealed class ContainerMergeSignalTests
{
    // The measured top-level field list of the owner's gear-"Replace" delta (2026-08-23).
    // 104 (saveSerial) is a RAW i64 scalar on this wire — every other field is a nested container.
    private static byte[] MeasuredReplaceDelta() =>
        new DeltaBytes()
            .Begin(0)                                       // CharSerialize container
            .FieldIndex(2).Begin(8).Int32(11).Int32(22).End()
            .FieldIndex(55).Begin(8).Int32(33).Int32(44).End()
            .FieldIndex(96).Begin(8).Int32(55).Int32(66).End()
            .FieldIndex(104).Int64(9_000_000_001L)          // saveSerial — raw scalar, no container
            .End()
            .ToArray();

    [Fact]
    public void IsMergeSignal_TrueForTheMeasuredReplaceDelta_WhichCarriesNoAllowlistedField()
    {
        var buffer = MeasuredReplaceDelta();

        Assert.True(ContainerDirtyDeltaReader.IsMergeSignal(buffer));

        // …and this is exactly why the old per-field gate lost the owner's edit.
        Assert.False(ContainerDirtyDeltaReader.TouchesEquip(buffer));            // 12
        Assert.False(ContainerDirtyDeltaReader.TouchesResonance(buffer));        // 28
        Assert.False(ContainerDirtyDeltaReader.TouchesTalents(buffer));          // 61
        Assert.False(ContainerDirtyDeltaReader.TouchesSeasonCultivate(buffer));  // 101
        Assert.False(ContainerDirtyDeltaReader.TouchesField(buffer, 57));        // mod
    }

    [Fact]
    public void TopLevelFields_CensusesTheMeasuredReplaceDeltaInWireOrder()
    {
        Assert.Equal(new[] { 2, 55, 96, 104 }, ContainerDirtyDeltaReader.TopLevelFields(MeasuredReplaceDelta()));
    }

    [Fact]
    public void IsMergeSignal_StillTrueForTheClassicAllowlistedDeltas()
    {
        Assert.True(ContainerDirtyDeltaReader.IsMergeSignal(DeltaBytes.CharSerializeWithField(12)));
        Assert.True(ContainerDirtyDeltaReader.IsMergeSignal(DeltaBytes.CharSerializeWithField(61)));
        Assert.True(ContainerDirtyDeltaReader.IsMergeSignal(DeltaBytes.CharSerializeWithFieldThenField(104, 28)));
    }

    [Fact]
    public void IsMergeSignal_FalseForAnEmptyContainer_TheGameMergedNothing()
    {
        // [BEGIN][END] — an empty CharSerialize container: there is nothing fresh to re-read, so this
        // must NOT wake the live-state read.
        Assert.False(ContainerDirtyDeltaReader.IsMergeSignal(new DeltaBytes().Begin(-3).ToArray()));
    }

    [Fact]
    public void IsMergeSignal_FalseAndNeverThrowsOnMalformedInput()
    {
        Assert.False(ContainerDirtyDeltaReader.IsMergeSignal(null));
        Assert.False(ContainerDirtyDeltaReader.IsMergeSignal(System.Array.Empty<byte>()));
        Assert.False(ContainerDirtyDeltaReader.IsMergeSignal(new byte[] { 1, 2, 3 }));
        Assert.False(ContainerDirtyDeltaReader.IsMergeSignal(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 }));  // bad BEGIN tag
    }

    [Fact]
    public void TopLevelFields_ReturnsEmptyAndNeverThrowsOnMalformedInput()
    {
        Assert.Empty(ContainerDirtyDeltaReader.TopLevelFields(null));
        Assert.Empty(ContainerDirtyDeltaReader.TopLevelFields(new byte[] { 1, 2, 3 }));
        Assert.Empty(ContainerDirtyDeltaReader.TopLevelFields(new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 }));
    }

    [Fact]
    public void TopLevelFields_IsBoundedSoACorruptBufferCannotDriveAnUnboundedList()
    {
        var builder = new DeltaBytes().Begin(0);
        for (var i = 1; i <= 40; i++) builder.FieldIndex(i).Begin(-3);   // 40 empty nested containers
        var census = ContainerDirtyDeltaReader.TopLevelFields(builder.End().ToArray(), max: 8);

        Assert.Equal(8, census.Count);
    }
}
