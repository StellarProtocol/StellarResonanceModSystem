using System.Linq;
using Stellar.Application.Abstractions;
using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

/// <summary>
/// Buff effect ids off the wire (BuffEffectSync / BuffInfo field 13 / appear field 8) — the rules of resonance-logs-cn
/// decoder.rs and the Experiment McBuffEffectPatch they replace: PlayEffect ids count only next to an
/// AddBuff/BuffChange entry, Customize(1002) values replace them, Remove drops them, an AddBuff payload's own
/// BuffInfo PlayEffect ids come first.
/// </summary>
public sealed class BuffEffectIdsReaderTests
{
    private const long SyncUuid = (77L << 16) | 640L;

    private static byte[] PlayEffect(int effectId) => new WireBytes().Tag(1, 0).Varint((ulong)effectId).ToArray();

    private static byte[] Logic(int effectType, byte[]? raw)
    {
        var w = new WireBytes().Tag(1, 0).Varint((ulong)effectType);
        if (raw != null) w.Tag(2, 2).LengthDelimited(raw);
        return w.ToArray();
    }

    private static byte[] BuffInfo(int buffUuid, int baseId, long host = 0, params byte[][] logics)
    {
        var w = new WireBytes().Tag(1, 0).Varint((ulong)buffUuid).Tag(2, 0).Varint((ulong)baseId);
        if (host != 0) w.Tag(4, 0).Varint((ulong)host);
        w.Tag(8, 0).Varint(1);
        foreach (var l in logics) w.Tag(13, 2).LengthDelimited(l);
        return w.ToArray();
    }

    private static byte[] Effect(int type, int buffUuid, long host, params byte[][] logics)
    {
        var w = new WireBytes().Tag(1, 0).Varint((ulong)type).Tag(2, 0).Varint((ulong)buffUuid);
        if (host != 0) w.Tag(3, 0).Varint((ulong)host);
        foreach (var l in logics) w.Tag(5, 2).LengthDelimited(l);
        return w.ToArray();
    }

    private static byte[] Sync(params byte[][] effects)
    {
        var w = new WireBytes().Tag(1, 0).Varint((ulong)SyncUuid);
        foreach (var e in effects) w.Tag(2, 2).LengthDelimited(e);
        return w.ToArray();
    }

    private static BuffEffectIdUpdate Last(BuffEventBatch b) => b.EffectIds!.Last();

    [Fact]
    public void PlayEffect_WithAddBuff_YieldsIds_HostFallsBackToSyncUuid()
    {
        var batch = BuffEffectSyncReader.TryRead(Sync(Effect(1, 100, 0,
            Logic(18, BuffInfo(100, 841509)), Logic(0, PlayEffect(4)), Logic(0, PlayEffect(9)))));

        var u = Assert.Single(batch.EffectIds!);
        Assert.Equal(new[] { 4, 9 }, u.EffectIds);
        Assert.Equal(100, u.BuffUuid);
        Assert.Equal(SyncUuid, u.HostUuid);
        Assert.True(batch.Touched);
    }

    [Fact]
    public void PlayEffect_WithoutAddOrChange_YieldsNothing()
    {
        var batch = BuffEffectSyncReader.TryRead(Sync(Effect(3, 100, 0, Logic(0, PlayEffect(4)))));
        Assert.Null(batch.EffectIds);
    }

    [Fact]
    public void PlayEffect_ExplicitHostUuid_Wins()
    {
        const long host = (5L << 16) | 640L;
        var batch = BuffEffectSyncReader.TryRead(Sync(Effect(1, 100, host,
            Logic(19, new WireBytes().Tag(1, 0).Varint(2).ToArray()), Logic(0, PlayEffect(7)))));
        Assert.Equal(host, Assert.Single(batch.EffectIds!).HostUuid);
    }

    [Fact]
    public void PlayEffect_WithoutRawData_IsIdZero()
    {
        // proto3 elides effect_id 0 (and then the empty RawData); the game reads that as id 0 — Tina's first slasher.
        var batch = BuffEffectSyncReader.TryRead(Sync(Effect(1, 100, 0,
            Logic(18, BuffInfo(100, 841509)), Logic(0, null))));
        Assert.Equal(new[] { 0 }, Assert.Single(batch.EffectIds!).EffectIds);
    }

    [Fact]
    public void CustomizeOnly_YieldsValues_WithoutTouchingBuffs()
    {
        // logicIdx 1-4: two PlayEffect messages, a 1-byte blob and a 2-byte LE i16; StopAll(15) is skipped.
        var batch = BuffEffectSyncReader.TryRead(Sync(Effect(1002, 100, 0,
            Logic(15, PlayEffect(99)),
            Logic(0, PlayEffect(3)), Logic(0, PlayEffect(1)),
            Logic(0, new byte[] { 5 }), Logic(0, new byte[] { 0x09, 0x00 }))));

        Assert.False(batch.Touched);
        Assert.Empty(batch.Upserts);
        Assert.Equal(new[] { 3, 1, 5, 9 }, Assert.Single(batch.EffectIds!).EffectIds);
    }

    [Fact]
    public void Customize_ReplacesPlayEffectIds_InTheSameEffect()
    {
        var batch = BuffEffectSyncReader.TryRead(Sync(Effect(1002, 100, 0,
            Logic(19, new WireBytes().Tag(1, 0).Varint(1).ToArray()), Logic(0, PlayEffect(42)))));
        // PlayEffect ids (AddOrChange present) first, then the customize values replace them.
        Assert.Equal(2, batch.EffectIds!.Count);
        Assert.Equal(new[] { 42 }, batch.EffectIds[0].EffectIds);
        Assert.Equal(new[] { 1, 42 }, Last(batch).EffectIds);   // BuffChange blob {1:1} decodes as effect_id 1
    }

    [Fact]
    public void Remove_YieldsNullIds()
    {
        var batch = BuffEffectSyncReader.TryRead(Sync(Effect(2, 100, 0, Logic(0, PlayEffect(4)))));
        var u = Assert.Single(batch.EffectIds!);
        Assert.Null(u.EffectIds);
        Assert.Equal(100, u.BuffUuid);
        Assert.Equal(new[] { 100 }, batch.Removes);
    }

    [Fact]
    public void AddBuffPayload_Field13Ids_ThenOuterOverride()
    {
        const long innerHost = (9L << 16) | 640L;
        var inner = BuffInfo(100, 884659, innerHost, Logic(0, PlayEffect(1)), Logic(0, PlayEffect(5)), Logic(0, PlayEffect(3)));

        var only = BuffEffectSyncReader.TryRead(Sync(Effect(1, 100, 0, Logic(18, inner))));
        var u = Assert.Single(only.EffectIds!);
        Assert.Equal(new[] { 1, 5, 3 }, u.EffectIds);
        Assert.Equal(innerHost, u.HostUuid);
        Assert.Equal(884659, Assert.Single(only.Upserts).BaseId);   // buff decode unchanged

        var both = BuffEffectSyncReader.TryRead(Sync(Effect(1, 100, 0, Logic(18, inner), Logic(0, PlayEffect(8)))));
        Assert.Equal(new[] { 1, 5, 3 }, both.EffectIds![0].EffectIds);
        Assert.Equal(new[] { 8 }, Last(both).EffectIds);              // outer ids win (decoder.rs order)
    }

    [Fact]
    public void BuffInfoReader_ReturnsHostAndField13Ids_NullWhenNone()
    {
        Assert.True(BuffInfoReader.TryRead(BuffInfo(1, 2, 0, Logic(18, BuffInfo(3, 4))), out _, out var h0, out var none));
        Assert.Equal(0, h0);
        Assert.Null(none);   // non-PlayEffect logic entries carry no ids

        Assert.True(BuffInfoReader.TryRead(BuffInfo(1, 2, 0x1234, Logic(0, PlayEffect(6))), out var b, out var h, out var ids));
        Assert.Equal(0x1234, h);
        Assert.Equal(new[] { 6 }, ids);
        Assert.Equal(2, b.BaseId);
    }

    [Fact]
    public void Appear_Field7AndField8_IdsInOrder()
    {
        var buffSync = new WireBytes().Tag(1, 0).Varint((ulong)SyncUuid)
            .Tag(2, 2).LengthDelimited(BuffInfo(11, 841509, 0, Logic(0, PlayEffect(2)))).ToArray();
        var effSync = Sync(Effect(1002, 11, 0, Logic(0, new byte[] { 7 })));
        var entity = new WireBytes().Tag(1, 0).Varint((ulong)SyncUuid)
            .Tag(7, 2).LengthDelimited(buffSync).Tag(8, 2).LengthDelimited(effSync).ToArray();
        var payload = new WireBytes().Tag(1, 2).LengthDelimited(entity).ToArray();

        Assert.True(SyncNearEntitiesReader.TryReadAppearAndDisappear(payload, out var appears, out _));
        var a = Assert.Single(appears);
        Assert.Equal(11, Assert.Single(a.Buffs!).BuffUuid);           // field 8 never adds buffs
        Assert.Equal(2, a.EffectIds!.Count);
        Assert.Equal(new[] { 2 }, a.EffectIds[0].EffectIds);
        Assert.Equal(new[] { 7 }, a.EffectIds[1].EffectIds);
    }

    [Fact]
    public void NoIds_AllocatesNoUpdateList()
    {
        var batch = BuffEffectSyncReader.TryRead(Sync(Effect(1, 100, 0, Logic(18, BuffInfo(100, 1)))));
        Assert.Null(batch.EffectIds);
    }
}
