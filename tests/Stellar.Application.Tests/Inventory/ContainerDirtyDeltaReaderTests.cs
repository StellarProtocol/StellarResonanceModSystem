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
        // skipped via its BEGIN+size+trailing-END before the walk reaches field 57.
        // Wire-accurate: size EXCLUDES the trailing END tag (char_serialize.lua mergeData).
        var unknownPayload = new DeltaBytes().Int32(123).Int32(456).ToArray();
        var buffer = new DeltaBytes()
            .Begin(0)
                .FieldIndex(79)                         // unknown field
                .Begin(unknownPayload.Length).Int32(123).Int32(456).End()  // its container + END
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
                    .Begin(8).Int32(11).Int32(22).End()     // its container, size=8 (EXCLUSIVE) + END
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
                .Begin(4).Int32(1).End()   // wire-accurate: the non-empty body carries its own END
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
        // count; the reader must skip the unknown field (consuming its trailing
        // END tag + canary) and still reach mod_slots.
        var unknown = new DeltaBytes(guards: true).Int32(123).Int32(456).ToArray();
        var buffer = new DeltaBytes(guards: true)
            .Begin(0)
                .FieldIndex(79)                                       // unknown field
                .Begin(unknown.Length).Int32(123).Int32(456).End()   // its container + END
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
    public void TouchesTalents_TrueForProfessionListDelta()
        => Assert.True(ContainerDirtyDeltaReader.TouchesTalents(
            DeltaBytes.CharSerializeWithField(61)));

    [Fact]
    public void TouchesSeasonCultivate_TrueForSeasonCultivateDelta()
        => Assert.True(ContainerDirtyDeltaReader.TouchesSeasonCultivate(
            DeltaBytes.CharSerializeWithField(101)));

    [Fact]
    public void TouchesResonance_TrueForResonanceDelta()
        // PINNED: field 28 (CharSerialize.resonance — equipped Battle Imagines) must be in the
        // SelfGearChanged trigger set, so an in-session imagine swap re-fires the Lua refresh + the
        // plugin's recapture (owner staging run sea/445626427740520448, 2026-08-23).
        => Assert.True(ContainerDirtyDeltaReader.TouchesResonance(
            DeltaBytes.CharSerializeWithField(28)));

    [Fact]
    public void TouchesField_FalseForUntouchedField_AndMalformed()
    {
        Assert.False(ContainerDirtyDeltaReader.TouchesField(
            DeltaBytes.CharSerializeWithField(12), 61));
        Assert.False(ContainerDirtyDeltaReader.TouchesField(null, 61));
        Assert.False(ContainerDirtyDeltaReader.TouchesField(new byte[3], 61));
    }

    [Fact]
    public void TouchesField_MatchesAFieldAfterASkippedField()
    {
        // Field 12 (equip) carries a non-empty skippable body and comes first; field 61
        // (professionList / talents) follows it. TouchesTalents must skip past field 12's
        // container and still match field 61 — a positive case for the skip-then-continue path
        // that Read_SkipsUnknownTopLevelField_BeforeMod already covers for ContainerDirtyDeltaReader.Read.
        Assert.True(ContainerDirtyDeltaReader.TouchesTalents(
            DeltaBytes.CharSerializeWithFieldThenField(12, 61)));
    }

    [Fact]
    public void TouchesResonance_MatchesResonanceAfterASkippedNonEmptyField()
        // PINNED (owner run sea/pNhmVQvVmV, 2026-08-23): the exact suspected live shape — an
        // imagine swap co-dirties attr (field 16) AHEAD of resonance (field 28). The old skip
        // left the cursor on the skipped field's trailing END tag, the walk read -3 as the next
        // index and exited, so TouchesResonance only ever matched a FIRST-position field 28 and
        // the swap never re-fired the Lua refresh (segment 2 uploaded the stale pair).
        => Assert.True(ContainerDirtyDeltaReader.TouchesResonance(
            DeltaBytes.CharSerializeWithFieldThenField(16, 28)));

    [Fact]
    public void TouchesResonance_MatchesResonanceAfterTwoSkippedNonEmptyFields()
    {
        // Two consecutive skipped non-empty fields (16 then 7), then the target (28) —
        // each skip must consume ITS OWN trailing END tag or the second skip desyncs.
        // Wire shape per char_serialize.lua mergeData: [index][-2][size][body][-3], size exclusive.
        var buffer = new DeltaBytes()
            .Begin(0)                             // CharSerialize container
            .FieldIndex(16)
            .Begin(8).Int32(11).Int32(22).End()   // skipped body 1 (size 8, exclusive) + END
            .FieldIndex(7)
            .Begin(4).Int32(33).End()             // skipped body 2 (size 4, exclusive) + END
            .FieldIndex(28)
            .Begin(-3)                            // empty nested container for the target
            .End()
            .ToArray();

        Assert.True(ContainerDirtyDeltaReader.TouchesResonance(buffer));
    }

    [Fact]
    public void TouchesResonance_Guarded_MatchesResonanceAfterASkippedNonEmptyField()
    {
        // SEA-wire (guards) variant of the skip-then-match case: the skipped container's size
        // is a guard-inclusive byte count of its body, and the trailing END tag carries its own
        // canary — the skip must consume both before the walk reads the next field index.
        var body = new DeltaBytes(guards: true).Int32(11).Int32(22).ToArray();
        var buffer = new DeltaBytes(guards: true)
            .Begin(0)                                       // CharSerialize container
            .FieldIndex(16)
            .Begin(body.Length).Int32(11).Int32(22).End()   // skipped body + END (+canaries)
            .FieldIndex(28)
            .Begin(-3)                                      // empty nested container for the target
            .End()
            .ToArray();

        Assert.True(ContainerDirtyDeltaReader.TouchesResonance(buffer));
    }
}
