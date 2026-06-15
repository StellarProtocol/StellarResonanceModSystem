using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

public sealed class SkillEffectReaderTests
{
    [Fact]
    public void TryRead_EmptyPayload_ZeroDamages()
    {
        var ok = SkillEffectReader.TryRead(new byte[0], out var damages);

        Assert.True(ok);
        Assert.Empty(damages);
    }

    [Fact]
    public void TryRead_UuidOnly_DamagesEmpty()
    {
        // SkillEffect.Uuid (field 1) is present but no Damages — reader
        // should silently skip field 1 and return an empty list.
        var payload = new WireBytes().Tag(1, 0).Varint(0xFEED1234UL).ToArray();

        var ok = SkillEffectReader.TryRead(payload, out var damages);

        Assert.True(ok);
        Assert.Empty(damages);
    }

    [Fact]
    public void TryRead_SingleDamage_ExtractsFields()
    {
        var inner = new WireBytes()
            .Tag(6, 0).Varint(500)                  // Value
            .Tag(9, 0).Varint(450)                  // HpLessenValue
            .Tag(11, 0).Varint(0xABCDUL)            // AttackerUuid
            .Tag(12, 0).Varint(101)                 // OwnerId (skill_id)
            .Tag(18, 0).Varint(1)                   // Property = Fire
            .ToArray();
        var payload = new WireBytes()
            .Tag(2, 2).LengthDelimited(inner)
            .ToArray();

        var ok = SkillEffectReader.TryRead(payload, out var damages);

        Assert.True(ok);
        Assert.Single(damages);
        Assert.Equal(500, damages[0].Value);
        Assert.Equal(450, damages[0].HpLessenValue);
        Assert.Equal(0xABCDL, damages[0].AttackerUuid);
        Assert.Equal(101, damages[0].OwnerId);
        Assert.Equal(1, damages[0].Property);
    }

    [Fact]
    public void TryRead_MultipleDamages_PreservesOrder()
    {
        var d1 = new WireBytes().Tag(6, 0).Varint(100).Tag(12, 0).Varint(1).ToArray();
        var d2 = new WireBytes().Tag(6, 0).Varint(200).Tag(12, 0).Varint(2).ToArray();
        var d3 = new WireBytes().Tag(6, 0).Varint(300).Tag(12, 0).Varint(3).ToArray();
        var payload = new WireBytes()
            .Tag(2, 2).LengthDelimited(d1)
            .Tag(2, 2).LengthDelimited(d2)
            .Tag(2, 2).LengthDelimited(d3)
            .ToArray();

        var ok = SkillEffectReader.TryRead(payload, out var damages);

        Assert.True(ok);
        Assert.Equal(3, damages.Count);
        Assert.Equal(100, damages[0].Value);
        Assert.Equal(1, damages[0].OwnerId);
        Assert.Equal(200, damages[1].Value);
        Assert.Equal(2, damages[1].OwnerId);
        Assert.Equal(300, damages[2].Value);
        Assert.Equal(3, damages[2].OwnerId);
    }

    [Fact]
    public void TryRead_UuidThenDamages_BothHandled()
    {
        // Mixed-order: Uuid (skipped) then a Damage entry. The reader needs
        // to handle Uuid via SkipField, not assert any field order.
        var inner = new WireBytes().Tag(6, 0).Varint(999).ToArray();
        var payload = new WireBytes()
            .Tag(1, 0).Varint(0x1234UL)
            .Tag(2, 2).LengthDelimited(inner)
            .ToArray();

        var ok = SkillEffectReader.TryRead(payload, out var damages);

        Assert.True(ok);
        Assert.Single(damages);
        Assert.Equal(999, damages[0].Value);
    }

    [Fact]
    public void TryRead_MalformedInner_ReturnsFalse()
    {
        // Inner SyncDamageInfo bytes that contain a malformed tag — the inner
        // reader returns false, which the wrapper must propagate.
        var brokenInner = new byte[] { 0xFF };  // unterminated varint
        var payload = new WireBytes()
            .Tag(2, 2).LengthDelimited(brokenInner)
            .ToArray();

        var ok = SkillEffectReader.TryRead(payload, out var damages);

        Assert.False(ok);
        Assert.Empty(damages);
    }

    [Fact]
    public void TryRead_TruncatedOuterLength_ReturnsFalse()
    {
        // Tag for field 2 (length-delimited), length varint says 5, but
        // only 2 bytes follow — TryReadLengthDelimited bails.
        var payload = new byte[] { 0x12, 0x05, 0x00, 0x00 };

        var ok = SkillEffectReader.TryRead(payload, out var damages);

        Assert.False(ok);
        Assert.Empty(damages);
    }

    [Fact]
    public void TryRead_UnknownFieldNumber_SkippedSilently()
    {
        // Field 99 wire-type 0 — must be skipped, not fail. Schema
        // evolution on the server should not break our reader.
        var inner = new WireBytes().Tag(6, 0).Varint(42).ToArray();
        var payload = new WireBytes()
            .Tag(99, 0).Varint(0xDEADUL)
            .Tag(2, 2).LengthDelimited(inner)
            .ToArray();

        var ok = SkillEffectReader.TryRead(payload, out var damages);

        Assert.True(ok);
        Assert.Single(damages);
        Assert.Equal(42, damages[0].Value);
    }
}
