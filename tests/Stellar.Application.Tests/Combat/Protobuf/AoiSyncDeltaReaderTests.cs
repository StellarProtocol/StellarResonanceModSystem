using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

public sealed class AoiSyncDeltaReaderTests
{
    [Fact]
    public void TryReadDelta_UuidAndAttrsOnly_Parses()
    {
        var attrs = new WireBytes().Tag(1, 0).Varint(42UL).ToArray();
        var payload = new WireBytes()
            .Tag(1, 0).Varint(0xDEADBEEFUL)
            .Tag(2, 2).LengthDelimited(attrs)
            .ToArray();

        var ok = AoiSyncDeltaReader.TryReadDelta(payload, out var delta);

        Assert.True(ok);
        Assert.Equal(0xDEADBEEFL, delta.Uuid);
        Assert.NotNull(delta.Attrs);
        Assert.Equal(42L, delta.Attrs!.Value.Uuid);
    }

    [Fact]
    public void TryReadDelta_AllOptionalsMissing_OK()
    {
        var ok = AoiSyncDeltaReader.TryReadDelta(new byte[0], out var delta);
        Assert.True(ok);
        Assert.Equal(0L, delta.Uuid);
        Assert.Null(delta.Attrs);
        Assert.Null(delta.Events);
        Assert.Null(delta.BuffEvents);
    }

    [Fact]
    public void TryReadDeltaList_TwoEntries_PreservesOrder()
    {
        var d1 = new WireBytes().Tag(1, 0).Varint(1UL).ToArray();
        var d2 = new WireBytes().Tag(1, 0).Varint(2UL).ToArray();
        var payload = new WireBytes()
            .Tag(1, 2).LengthDelimited(d1)
            .Tag(1, 2).LengthDelimited(d2)
            .ToArray();

        var ok = AoiSyncDeltaReader.TryReadList(payload, out var deltas);

        Assert.True(ok);
        Assert.Equal(2, deltas.Count);
        Assert.Equal(1L, deltas[0].Uuid);
        Assert.Equal(2L, deltas[1].Uuid);
    }

    [Fact]
    public void TryReadDelta_DamagesMissing_DefaultsToEmpty()
    {
        var payload = new WireBytes().Tag(1, 0).Varint(7UL).ToArray();

        var ok = AoiSyncDeltaReader.TryReadDelta(payload, out var delta);

        Assert.True(ok);
        Assert.Equal(7L, delta.Uuid);
        Assert.NotNull(delta.Damages);
        Assert.Empty(delta.Damages);
    }

    [Fact]
    public void TryReadDelta_SkillEffectWithTwoDamages_PopulatesDamages()
    {
        var dmg1 = new WireBytes()
            .Tag(6, 0).Varint(123)                  // Value
            .Tag(9, 0).Varint(120)                  // HpLessenValue
            .Tag(11, 0).Varint(0xAAAAUL)            // AttackerUuid
            .Tag(12, 0).Varint(501)                 // OwnerId (skill_id)
            .ToArray();
        var dmg2 = new WireBytes()
            .Tag(6, 0).Varint(456)
            .Tag(9, 0).Varint(450)
            .Tag(11, 0).Varint(0xBBBBUL)
            .Tag(12, 0).Varint(502)
            .Tag(18, 0).Varint(2)                   // Property = Water
            .ToArray();
        var skillEffect = new WireBytes()
            .Tag(1, 0).Varint(0xCAFEUL)             // SkillEffect.Uuid — ignored
            .Tag(2, 2).LengthDelimited(dmg1)
            .Tag(2, 2).LengthDelimited(dmg2)
            .ToArray();
        var payload = new WireBytes()
            .Tag(1, 0).Varint(0x1234UL)             // AoiSyncDelta.Uuid
            .Tag(7, 2).LengthDelimited(skillEffect) // SkillEffect — NEW
            .ToArray();

        var ok = AoiSyncDeltaReader.TryReadDelta(payload, out var delta);

        Assert.True(ok);
        Assert.Equal(0x1234L, delta.Uuid);
        Assert.Equal(2, delta.Damages.Count);
        Assert.Equal(123, delta.Damages[0].Value);
        Assert.Equal(120, delta.Damages[0].HpLessenValue);
        Assert.Equal(0xAAAAL, delta.Damages[0].AttackerUuid);
        Assert.Equal(501, delta.Damages[0].OwnerId);
        Assert.Equal(456, delta.Damages[1].Value);
        Assert.Equal(450, delta.Damages[1].HpLessenValue);
        Assert.Equal(0xBBBBL, delta.Damages[1].AttackerUuid);
        Assert.Equal(502, delta.Damages[1].OwnerId);
        Assert.Equal(2, delta.Damages[1].Property);
    }
}
