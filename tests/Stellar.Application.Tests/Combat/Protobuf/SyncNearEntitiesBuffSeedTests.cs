using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

/// <summary>
/// Pins the Appear buff seed (spec-from-talent-buffs, owner go 2026-09-26; evidence
/// docs/recon/spec-from-public-wire-data.md): <c>Entity.buff_infos</c> (field 7) =
/// <c>BuffInfoSync{uuid=1, buff_infos=2 repeated BuffInfo}</c> is decoded by the shared
/// <see cref="BuffInfoReader"/> so a nearby player's talent buffs (<c>SourceKind == 6</c>) are known
/// from the moment they appear. The BuffInfo layout mirrors the real wire (fields 1,2,3,5,6,7,8,10,11,12{1}).
/// </summary>
public sealed class SyncNearEntitiesBuffSeedTests
{
    private const ulong PlayerUuid = (1248014UL << 16) | 640UL;

    internal static byte[] TalentBuff(int buffUuid, int baseId, int talentId)
    {
        var source = new WireBytes().Tag(1, 0).Varint(6).Tag(2, 0).Varint((ulong)talentId).ToArray();
        return new WireBytes()
            .Tag(1, 0).Varint((ulong)buffUuid)
            .Tag(2, 0).Varint((ulong)baseId)
            .Tag(3, 0).Varint(1)
            .Tag(5, 0).Varint(1)                       // unparsed field — must be skipped
            .Tag(6, 0).Varint(1_758_000_000_000UL)
            .Tag(7, 0).Varint(PlayerUuid)
            .Tag(8, 0).Varint(1)
            .Tag(10, 0).Varint(1)
            .Tag(11, 0).Varint(0)
            .Tag(12, 2).LengthDelimited(source)
            .ToArray();
    }

    internal static byte[] BuffInfoSync(params byte[][] buffs)
    {
        var w = new WireBytes().Tag(1, 0).Varint(PlayerUuid);
        foreach (var b in buffs) w.Tag(2, 2).LengthDelimited(b);
        return w.ToArray();
    }

    private static byte[] Entity(byte[]? buffSync)
    {
        var w = new WireBytes().Tag(1, 0).Varint(PlayerUuid).Tag(2, 0).Varint(1);
        if (buffSync is not null) w.Tag(7, 2).LengthDelimited(buffSync);
        w.Tag(9, 0).Varint(0);
        return w.ToArray();
    }

    [Fact]
    public void Appear_WithField7_DecodesActiveBuffsIncludingFightSource()
    {
        var sync = BuffInfoSync(TalentBuff(11, 2202110, 510), TalentBuff(12, 2202111, 511));
        var payload = new WireBytes().Tag(1, 2).LengthDelimited(Entity(sync)).ToArray();

        Assert.True(SyncNearEntitiesReader.TryReadAppearAndDisappear(payload, out var appears, out _));

        var e = Assert.Single(appears);
        Assert.Equal((long)PlayerUuid, e.Uuid);
        Assert.NotNull(e.Buffs);
        Assert.Equal(2, e.Buffs!.Count);
        Assert.Equal(11, e.Buffs[0].BuffUuid);
        Assert.Equal(2202110, e.Buffs[0].BaseId);
        Assert.Equal(6, e.Buffs[0].SourceKind);
        Assert.Equal(510, e.Buffs[0].SourceId);
        Assert.Equal((long)PlayerUuid, e.Buffs[0].FirerId.Value);
        Assert.Equal(2202111, e.Buffs[1].BaseId);
    }

    [Fact]
    public void Appear_WithoutField7_HasNullBuffs()
    {
        // Absent field 7 = "no BuffInfoSync on this snapshot" (null); the consumer treats that as an
        // empty set so a re-appear can never keep stale buffs.
        var payload = new WireBytes().Tag(1, 2).LengthDelimited(Entity(null)).ToArray();
        Assert.True(SyncNearEntitiesReader.TryReadAppearAndDisappear(payload, out var appears, out _));
        Assert.Null(Assert.Single(appears).Buffs);
    }

    [Fact]
    public void Appear_MalformedBuffInfo_IsDroppedButSiblingsSurvive()
    {
        var bad = new byte[] { 0x08, 0x80 };   // truncated varint
        var sync = BuffInfoSync(TalentBuff(11, 2202110, 510), bad, TalentBuff(13, 2202112, 512));
        var payload = new WireBytes().Tag(1, 2).LengthDelimited(Entity(sync)).ToArray();

        Assert.True(SyncNearEntitiesReader.TryReadAppearAndDisappear(payload, out var appears, out _));
        var e = Assert.Single(appears);
        Assert.Equal(new[] { 2202110, 2202112 }, new[] { e.Buffs![0].BaseId, e.Buffs[1].BaseId });
    }

    [Fact]
    public void EnterScene_PlayerEnt_Field7_DecodesBuffs()
    {
        // The local player's entity comes through EnterScene and reuses TryReadEntity.
        var playerEnt = Entity(BuffInfoSync(TalentBuff(21, 2207180, 1317)));
        var sceneInfo = new WireBytes().Tag(2, 2).LengthDelimited(playerEnt).ToArray();
        var body = new WireBytes().Tag(1, 2).LengthDelimited(sceneInfo).ToArray();

        Assert.True(EnterSceneReader.TryReadPlayerEntity(body, out var self));
        var b = Assert.Single(self.Buffs!);
        Assert.Equal(2207180, b.BaseId);
        Assert.Equal(6, b.SourceKind);
    }
}
