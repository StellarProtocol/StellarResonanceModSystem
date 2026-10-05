using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

public sealed class BuffEffectSyncReaderTests
{
    // BuffInfo bytes (AddBuff RawData).
    private static byte[] BuffInfo(int buffUuid, int baseId, int dur, long create) =>
        new WireBytes()
            .Tag(1, 0).Varint((ulong)buffUuid)
            .Tag(2, 0).Varint((ulong)baseId)
            .Tag(6, 0).Varint((ulong)create)
            .Tag(8, 0).Varint(1)
            .Tag(11, 0).Varint((ulong)dur)
            .ToArray();

    // BuffChange bytes (BuffChange RawData).
    private static byte[] BuffChange(int layer, int dur, long create) =>
        new WireBytes().Tag(1, 0).Varint((ulong)layer).Tag(2, 0).Varint((ulong)dur).Tag(3, 0).Varint((ulong)create).ToArray();

    // BuffEffectLogicInfo { 1=EffectType, 2=RawData }.
    private static byte[] Logic(int effectType, byte[] raw) =>
        new WireBytes().Tag(1, 0).Varint((ulong)effectType).Tag(2, 2).LengthDelimited(raw).ToArray();

    // BuffEffect { 1=Type, 2=BuffUuid, 5=repeated LogicEffect }.
    private static byte[] Effect(int type, int buffUuid, params byte[][] logics)
    {
        var w = new WireBytes().Tag(1, 0).Varint((ulong)type).Tag(2, 0).Varint((ulong)buffUuid);
        foreach (var l in logics) w.Tag(5, 2).LengthDelimited(l);
        return w.ToArray();
    }

    // BuffEffectSync { 1=Uuid, 2=repeated BuffEffect }.
    private static byte[] Sync(params byte[][] effects)
    {
        var w = new WireBytes().Tag(1, 0).Varint(12345UL);
        foreach (var e in effects) w.Tag(2, 2).LengthDelimited(e);
        return w.ToArray();
    }

    [Fact]
    public void Add_WithBuffInfo_ProducesUpsertWithBaseId()
    {
        var payload = Sync(Effect(type: 1, buffUuid: 100,
            Logic(18, BuffInfo(buffUuid: 100, baseId: 2110056, dur: 6000, create: 1_700_000_000_000))));

        var batch = BuffEffectSyncReader.TryRead(payload);

        Assert.True(batch.Touched);
        Assert.Single(batch.Upserts);
        Assert.Equal(2110056, batch.Upserts[0].BaseId);
        Assert.Equal(6000,    batch.Upserts[0].DurationMs);
        Assert.Equal(100,     batch.Upserts[0].BuffUuid);
        Assert.Empty(batch.Removes);
    }

    [Fact]
    public void Remove_ProducesRemoveKey()
    {
        var payload = Sync(Effect(type: 2, buffUuid: 100,
            Logic(18, BuffInfo(100, 2110056, 6000, 1_700_000_000_000))));

        var batch = BuffEffectSyncReader.TryRead(payload);

        Assert.True(batch.Touched);
        Assert.Empty(batch.Upserts);
        Assert.Single(batch.Removes);
        Assert.Equal(100, batch.Removes[0]);
    }

    // REGRESSION (owner 2026-10-05): re-eating a potion/food keeps the SAME buff uuid and the server announces the
    // extended time as RemoveLayer(6) + a BuffChange. Treating it as a removal dropped the still-live buff from
    // BuffsFor (meter Buffs & Debuffs block). Measured on the owner's spool: Potion 2033179 uuid 47438 "removed" at
    // the re-eat, then the game expired it at create + 2×600 s. RemoveLayer WITH buff data is an update.
    [Fact]
    public void RemoveLayer_WithBuffChange_IsRefreshUpsert_NotRemoval()
    {
        var payload = Sync(Effect(type: 6, buffUuid: 47438,
            Logic(19, BuffChange(layer: 1, dur: 1_200_000, create: 1_791_177_508_724))));

        var batch = BuffEffectSyncReader.TryRead(payload);

        Assert.True(batch.Touched);
        Assert.Empty(batch.Removes);
        Assert.Single(batch.Upserts);
        Assert.Equal(47438,     batch.Upserts[0].BuffUuid);
        Assert.Equal(1_200_000, batch.Upserts[0].DurationMs);
        Assert.Equal(1_791_177_508_724, batch.Upserts[0].CreateTimeMs);
    }

    [Fact]
    public void RemoveLayer_WithBuffInfo_IsUpsert_NotRemoval()
    {
        var payload = Sync(Effect(type: 6, buffUuid: 100,
            Logic(18, BuffInfo(buffUuid: 100, baseId: 2033179, dur: 1_200_000, create: 1_700_000_000_000))));

        var batch = BuffEffectSyncReader.TryRead(payload);

        Assert.Empty(batch.Removes);
        Assert.Single(batch.Upserts);
        Assert.Equal(2033179, batch.Upserts[0].BaseId);
    }

    // REGRESSION (owner 2026-10-05, 2.19.0): the potion's final expiry ALSO arrives as RemoveLayer + payload, now with
    // no layers left. Treating every payload-carrying RemoveLayer as an update left the expired potion on the meter
    // forever. Zero layers left (proto3 omits 0, so the field is absent) = the buff is gone.
    [Fact]
    public void RemoveLayer_WithBuffChange_ZeroLayersLeft_Removes()
    {
        var payload = Sync(Effect(type: 6, buffUuid: 47438,
            Logic(19, BuffChange(layer: 0, dur: 1_200_000, create: 1_791_177_508_724))));

        var batch = BuffEffectSyncReader.TryRead(payload);

        Assert.Empty(batch.Upserts);
        Assert.Single(batch.Removes);
        Assert.Equal(47438, batch.Removes[0]);
    }

    [Fact]
    public void RemoveLayer_WithBuffInfo_ZeroLayersLeft_Removes()
    {
        var info = new WireBytes()
            .Tag(1, 0).Varint(100UL).Tag(2, 0).Varint(2033179UL).Tag(11, 0).Varint(600_000UL).ToArray();   // no layer field
        var batch = BuffEffectSyncReader.TryRead(Sync(Effect(type: 6, buffUuid: 100, Logic(18, info))));

        Assert.Empty(batch.Upserts);
        Assert.Single(batch.Removes);
    }

    // Diagnostics trace: every BuffEffect's raw type/uuid/payload, so a run with STELLAR_DIAGNOSTICS=1 shows exactly
    // what the server sent (the re-eat vs expiry RemoveLayer question was unanswerable without it).
    [Fact]
    public void Trace_RecordsEachEffect_OnlyWhenAsked()
    {
        var payload = Sync(
            Effect(type: 6, buffUuid: 47438, Logic(19, BuffChange(layer: 2, dur: 1_200_000, create: 5))),
            Effect(type: 2, buffUuid: 9));

        Assert.Null(BuffEffectSyncReader.TryRead(payload).Trace);
        var trace = BuffEffectSyncReader.TryRead(payload, trace: true).Trace!;

        Assert.Equal(2, trace.Count);
        Assert.Equal(new BuffEffectTrace(6, 47438, 19, 0, 2, 1_200_000, 5), trace[0]);
        Assert.Equal(new BuffEffectTrace(2, 9, 0, 0, 0, 0, 0), trace[1]);
    }

    // A bare RemoveLayer (no buff payload) keeps the pre-fix removal behaviour — nothing to update with, and keeping
    // it could strand a buff that never gets a Remove(2).
    [Fact]
    public void RemoveLayer_WithoutPayload_StillRemoves()
    {
        var batch = BuffEffectSyncReader.TryRead(Sync(Effect(type: 6, buffUuid: 100)));

        Assert.Empty(batch.Upserts);
        Assert.Single(batch.Removes);
        Assert.Equal(100, batch.Removes[0]);
    }

    [Fact]
    public void BuffChange_ProducesPartialUpsert_BaseIdZero_BuffUuidFromEffect()
    {
        var payload = Sync(Effect(type: 4, buffUuid: 100, Logic(19, BuffChange(layer: 3, dur: 8000, create: 1_700_000_000_500))));

        var batch = BuffEffectSyncReader.TryRead(payload);

        Assert.Single(batch.Upserts);
        Assert.Equal(0,    batch.Upserts[0].BaseId);     // BuffChange carries no BaseId
        Assert.Equal(100,  batch.Upserts[0].BuffUuid);   // filled from the BuffEffect
        Assert.Equal(8000, batch.Upserts[0].DurationMs);
        Assert.Equal(3,    batch.Upserts[0].Layer);
    }

    [Fact]
    public void UnknownLogicType_Skipped_NoUpsert()
    {
        // EffectType 0 (PlayEffect) carries no buff payload.
        var payload = Sync(Effect(type: 1, buffUuid: 100, Logic(0, new byte[] { 1, 2, 3 })));

        var batch = BuffEffectSyncReader.TryRead(payload);

        Assert.False(batch.Touched);
        Assert.Empty(batch.Upserts);
        Assert.Empty(batch.Removes);
    }

    [Fact]
    public void Empty_ReturnsNotTouched()
        => Assert.False(BuffEffectSyncReader.TryRead(System.ReadOnlySpan<byte>.Empty).Touched);
}
