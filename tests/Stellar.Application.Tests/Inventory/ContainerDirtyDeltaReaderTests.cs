using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Inventory;

/// <summary>
/// Unit tests for the pure <see cref="ContainerDirtyDeltaReader"/> — the
/// IL2CPP-free walker for the WorldNtf method-22 (SyncContainerDirtyData) binary
/// delta. Verifies the CharSerialize → field 57 (Mod) → field 1 (mod_slots)
/// descent and the scalar-valued <c>map&lt;int32,int64&gt;</c> add/update/remove
/// encoding that BPSR-B does not exercise.
/// </summary>
public sealed class ContainerDirtyDeltaReaderTests
{
    // CharSerialize.mod field number and Mod.mod_slots field number, per the
    // verified proto (stru_char_serialize.proto / stru_mod.proto).
    private const int FieldMod = 57;
    private const int FieldModSlots = 1;

    [Fact]
    public void Read_SingleAdd_ReturnsSlotUuid()
    {
        var buffer = new DeltaBytes()
            .Begin(0)                       // CharSerialize container
                .FieldIndex(FieldMod)
                .Begin(0)                   // Mod container
                    .FieldIndex(FieldModSlots)
                    .Int32(1).Int32(0).Int32(0)  // add=1, remove=0, update=0
                    .Int32(3).Int64(999)         // slot 3 → uuid 999
                .End()
            .End()
            .ToArray();

        var delta = ContainerDirtyDeltaReader.Read(buffer);

        Assert.True(delta.Touched);
        Assert.Single(delta.AddsAndUpdates);
        Assert.Equal(999L, delta.AddsAndUpdates[3]);
        Assert.Empty(delta.Removes);
    }

    [Fact]
    public void Read_UpdateAndRemove_BothApplied()
    {
        var buffer = new DeltaBytes()
            .Begin(0)
                .FieldIndex(FieldMod)
                .Begin(0)
                    .FieldIndex(FieldModSlots)
                    .Int32(0).Int32(1).Int32(1)   // add=0, remove=1, update=1
                    .Int32(5)                       // remove slot 5
                    .Int32(7).Int64(0xABCDEF01)     // update slot 7 → uuid
                .End()
            .End()
            .ToArray();

        var delta = ContainerDirtyDeltaReader.Read(buffer);

        Assert.True(delta.Touched);
        Assert.Single(delta.AddsAndUpdates);
        Assert.Equal(0xABCDEF01L, delta.AddsAndUpdates[7]);
        Assert.Single(delta.Removes);
        Assert.Equal(5, delta.Removes[0]);
    }

    [Fact]
    public void Read_AddOnlySentinel_ReReadsRealAddCount()
    {
        // addCount == -1 (MAP_ADD_ONLY) → the next i32 is the real addCount, and
        // there is NO remove/update count word.
        var buffer = new DeltaBytes()
            .Begin(0)
                .FieldIndex(FieldMod)
                .Begin(0)
                    .FieldIndex(FieldModSlots)
                    .Int32(-1)                  // MAP_ADD_ONLY
                    .Int32(2)                   // real addCount = 2
                    .Int32(1).Int64(100)
                    .Int32(2).Int64(200)
                .End()
            .End()
            .ToArray();

        var delta = ContainerDirtyDeltaReader.Read(buffer);

        Assert.True(delta.Touched);
        Assert.Equal(2, delta.AddsAndUpdates.Count);
        Assert.Equal(100L, delta.AddsAndUpdates[1]);
        Assert.Equal(200L, delta.AddsAndUpdates[2]);
        Assert.Empty(delta.Removes);
    }

    [Fact]
    public void Read_MapSkipSentinel_ReturnsUntouched()
    {
        // addCount == -4 (MAP_SKIP) → mod_slots is present but unchanged.
        var buffer = new DeltaBytes()
            .Begin(0)
                .FieldIndex(FieldMod)
                .Begin(0)
                    .FieldIndex(FieldModSlots)
                    .Int32(-4)                  // MAP_SKIP
                .End()
            .End()
            .ToArray();

        var delta = ContainerDirtyDeltaReader.Read(buffer);

        Assert.False(delta.Touched);
    }

    [Fact]
    public void Read_SkipsUnknownTopLevelField_BeforeMod()
    {
        // A leading unknown container field (index 79 = itemCurrency) must be
        // skipped via its BEGIN+size before the walk reaches field 57.
        var unknownPayload = new DeltaBytes().Int32(123).Int32(456).ToArray();
        var buffer = new DeltaBytes()
            .Begin(0)
                .FieldIndex(79)                         // unknown field
                .Begin(unknownPayload.Length).Int32(123).Int32(456)  // its container
                .FieldIndex(FieldMod)
                .Begin(0)
                    .FieldIndex(FieldModSlots)
                    .Int32(1).Int32(0).Int32(0)
                    .Int32(4).Int64(42)
                .End()
            .End()
            .ToArray();

        var delta = ContainerDirtyDeltaReader.Read(buffer);

        Assert.True(delta.Touched);
        Assert.Equal(42L, delta.AddsAndUpdates[4]);
    }

    [Fact]
    public void Read_SkipsUnknownModInnerField_BeforeModSlots()
    {
        // Inside Mod, an unknown inner field (index 2 = mod_infos) preceding
        // mod_slots (index 1) must be skipped.
        var buffer = new DeltaBytes()
            .Begin(0)
                .FieldIndex(FieldMod)
                .Begin(0)
                    .FieldIndex(2)                          // mod_infos (unknown to us)
                    .Begin(8).Int32(11).Int32(22)           // its container, size=8
                    .FieldIndex(FieldModSlots)
                    .Int32(1).Int32(0).Int32(0)
                    .Int32(9).Int64(900)
                .End()
            .End()
            .ToArray();

        var delta = ContainerDirtyDeltaReader.Read(buffer);

        Assert.True(delta.Touched);
        Assert.Equal(900L, delta.AddsAndUpdates[9]);
    }

    [Fact]
    public void Read_NoModField_ReturnsUntouched()
    {
        var buffer = new DeltaBytes()
            .Begin(0)
                .FieldIndex(79)
                .Begin(4).Int32(1)
            .End()
            .ToArray();

        var delta = ContainerDirtyDeltaReader.Read(buffer);

        Assert.False(delta.Touched);
    }

    [Fact]
    public void Read_NullOrTooShort_ReturnsUntouched()
    {
        Assert.False(ContainerDirtyDeltaReader.Read(null).Touched);
        Assert.False(ContainerDirtyDeltaReader.Read(new byte[] { 1, 2, 3 }).Touched);
    }

    [Fact]
    public void Read_TruncatedSlotEntry_ReturnsUntouched_NoThrow()
    {
        // addCount=1 but the entry is truncated (only the slot key, no i64 uuid).
        var buffer = new DeltaBytes()
            .Begin(0)
                .FieldIndex(FieldMod)
                .Begin(0)
                    .FieldIndex(FieldModSlots)
                    .Int32(1).Int32(0).Int32(0)
                    .Int32(3)                  // slot, then buffer ends
            .ToArray();

        var delta = ContainerDirtyDeltaReader.Read(buffer);

        // CountsAreSane rejects (needs 12 bytes for the entry, only 4 remain).
        Assert.False(delta.Touched);
    }

    [Fact]
    public void Read_EmptyCharSerializeContainer_ReturnsUntouched()
    {
        // size == END marks an empty container.
        var buffer = new DeltaBytes().Begin(-3).ToArray();
        var delta = ContainerDirtyDeltaReader.Read(buffer);
        Assert.False(delta.Touched);
    }

    [Fact]
    public void Read_Guarded_SingleAdd_SkipsCanary_ReturnsSlotUuid()
    {
        // The SEA build embeds a 4-byte 0xDEADBEEF canary after every value; the
        // reader must skip it. Same structure as Read_SingleAdd, guard-encoded.
        var buffer = new DeltaBytes(guards: true)
            .Begin(0)
                .FieldIndex(FieldMod)
                .Begin(0)
                    .FieldIndex(FieldModSlots)
                    .Int32(1).Int32(0).Int32(0)   // add=1, remove=0, update=0
                    .Int32(3).Int64(999)          // slot 3 → uuid 999 (i64 + ONE guard)
                .End()
            .End()
            .ToArray();

        var delta = ContainerDirtyDeltaReader.Read(buffer);

        Assert.True(delta.Touched);
        Assert.Single(delta.AddsAndUpdates);
        Assert.Equal(999L, delta.AddsAndUpdates[3]);
        Assert.Empty(delta.Removes);
    }

    [Fact]
    public void Read_Guarded_SkipsUnknownField_GuardInclusiveSize()
    {
        // With guards, the skipped container's size is a guard-inclusive byte
        // count; the reader must skip the unknown field and still reach mod_slots.
        var unknown = new DeltaBytes(guards: true).Int32(123).Int32(456).ToArray();
        var buffer = new DeltaBytes(guards: true)
            .Begin(0)
                .FieldIndex(79)                                       // unknown field
                .Begin(unknown.Length).Int32(123).Int32(456)         // its container
                .FieldIndex(FieldMod)
                .Begin(0)
                    .FieldIndex(FieldModSlots)
                    .Int32(1).Int32(0).Int32(0)
                    .Int32(4).Int64(42)
                .End()
            .End()
            .ToArray();

        var delta = ContainerDirtyDeltaReader.Read(buffer);

        Assert.True(delta.Touched);
        Assert.Equal(42L, delta.AddsAndUpdates[4]);
    }
}
