using Stellar.Application.Abstractions;
using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

public sealed class SyncDamageInfoReaderTests
{
    [Fact]
    public void TryRead_AllFieldsPresent_ParsesEach()
    {
        // All 15 fields we read, in numeric order. Values picked to be
        // distinct + sign-safe so any field-confusion bug surfaces.
        var payload = new WireBytes()
            .Tag(1, 0).Varint(0)                // DamageSource = Skill
            .Tag(2, 0).Varint(0)                // IsMiss = false
            .Tag(3, 0).Varint(1)                // IsCrit = true
            .Tag(4, 0).Varint(1)                // Type = Heal (just a non-zero discriminator)
            .Tag(5, 0).Varint(5)                // TypeFlag = crit (bit 0) + lucky (bit 2)
            .Tag(6, 0).Varint(1234)             // Value
            .Tag(7, 0).Varint(1100)             // ActualValue
            .Tag(8, 0).Varint(50)               // LuckyValue
            .Tag(9, 0).Varint(900)              // HpLessenValue
            .Tag(10, 0).Varint(100)             // ShieldLessenValue
            .Tag(11, 0).Varint(0xDEADBEEFUL)    // AttackerUuid
            .Tag(12, 0).Varint(7777)            // OwnerId (skill_id)
            .Tag(17, 0).Varint(1)               // IsDead = true
            .Tag(18, 0).Varint(1)               // Property = Fire
            .Tag(21, 0).Varint(0xCAFEBABEUL)    // TopSummonerId
            .ToArray();

        var ok = SyncDamageInfoReader.TryRead(payload, out var msg);

        Assert.True(ok);
        Assert.Equal(0, msg.DamageSource);
        Assert.False(msg.IsMiss);
        Assert.True(msg.IsCrit);
        Assert.Equal(1, msg.Type);
        Assert.Equal(5, msg.TypeFlag);
        Assert.Equal(1234, msg.Value);
        Assert.Equal(1100, msg.ActualValue);
        Assert.Equal(50, msg.LuckyValue);
        Assert.Equal(900, msg.HpLessenValue);
        Assert.Equal(100, msg.ShieldLessenValue);
        Assert.Equal(unchecked((long)0xDEADBEEFL), msg.AttackerUuid);
        Assert.Equal(7777, msg.OwnerId);
        Assert.True(msg.IsDead);
        Assert.Equal(1, msg.Property);
        Assert.Equal(unchecked((long)0xCAFEBABEL), msg.TopSummonerId);
    }

    [Fact]
    public void TryRead_EmptyPayload_AllDefaults()
    {
        var ok = SyncDamageInfoReader.TryRead(new byte[0], out var msg);

        Assert.True(ok);
        Assert.Equal(0, msg.DamageSource);
        Assert.Equal(0, msg.Type);
        Assert.Equal(0, msg.TypeFlag);
        Assert.Equal(0, msg.Value);
        Assert.Equal(0, msg.ActualValue);
        Assert.Equal(0, msg.LuckyValue);
        Assert.Equal(0, msg.HpLessenValue);
        Assert.Equal(0, msg.ShieldLessenValue);
        Assert.Equal(0L, msg.AttackerUuid);
        Assert.Equal(0L, msg.TopSummonerId);
        Assert.Equal(0, msg.OwnerId);
        Assert.False(msg.IsMiss);
        Assert.False(msg.IsCrit);
        Assert.False(msg.IsDead);
        Assert.Equal(0, msg.Property);
    }

    [Fact]
    public void TryRead_OnlyValueAndAttacker_OtherFieldsAreDefault()
    {
        var payload = new WireBytes()
            .Tag(6, 0).Varint(500)
            .Tag(11, 0).Varint(42UL)
            .ToArray();

        var ok = SyncDamageInfoReader.TryRead(payload, out var msg);

        Assert.True(ok);
        Assert.Equal(500, msg.Value);
        Assert.Equal(42L, msg.AttackerUuid);
        // Everything else stays at zero.
        Assert.Equal(0, msg.HpLessenValue);
        Assert.Equal(0, msg.OwnerId);
        Assert.Equal(0, msg.Property);
        Assert.False(msg.IsCrit);
    }

    [Fact]
    public void TryRead_TypeFlagCritBit_DecodedLiteral()
    {
        // The reader doesn't decode bits — it surfaces TypeFlag as-is and
        // leaves bit testing to CombatService. This test pins that contract:
        // TypeFlag is the literal int, not a decoded bool.
        var payload = new WireBytes().Tag(5, 0).Varint(1).ToArray();   // bit 0 set
        Assert.True(SyncDamageInfoReader.TryRead(payload, out var m1));
        Assert.Equal(1, m1.TypeFlag);

        payload = new WireBytes().Tag(5, 0).Varint(4).ToArray();        // bit 2 set
        Assert.True(SyncDamageInfoReader.TryRead(payload, out var m2));
        Assert.Equal(4, m2.TypeFlag);

        payload = new WireBytes().Tag(5, 0).Varint(5).ToArray();        // bits 0 + 2
        Assert.True(SyncDamageInfoReader.TryRead(payload, out var m3));
        Assert.Equal(5, m3.TypeFlag);

        // And bit-test logic stays correct:
        Assert.True((m3.TypeFlag & 0x1) != 0);
        Assert.True((m3.TypeFlag & 0x4) != 0);
        Assert.False((m1.TypeFlag & 0x4) != 0);
    }

    [Fact]
    public void TryRead_SkippedFieldsDoNotPollute()
    {
        // Emit fields the reader is supposed to SkipField over — verify that
        // the surfaced struct is unchanged and the cursor still advances.
        var damagePos = new WireBytes()
            .Tag(1, 5).Raw(0).Raw(0).Raw(0x80).Raw(0x3F)  // float 1.0
            .ToArray();
        var partInfo = new WireBytes().Tag(1, 0).Varint(7).ToArray();
        var damageWeight = new WireBytes()
            .Tag(1, 5).Raw(0).Raw(0).Raw(0).Raw(0x40)
            .ToArray();

        var payload = new WireBytes()
            .Tag(6, 0).Varint(111)                       // Value (read)
            .Tag(13, 0).Varint(99)                       // OwnerLevel (skipped)
            .Tag(14, 0).Varint(2)                        // OwnerStage (skipped)
            .Tag(15, 0).Varint(31337)                    // HitEventId (skipped)
            .Tag(16, 0).Varint(1)                        // IsNormal (skipped)
            .Tag(19, 2).LengthDelimited(damagePos)       // DamagePos (skipped)
            .Tag(20, 2).LengthDelimited(partInfo)        // PartInfos (skipped)
            .Tag(22, 2).LengthDelimited(damageWeight)    // DamageWeight (skipped)
            .Tag(23, 0).Varint(123)                      // PassiveUuid (skipped)
            .Tag(24, 0).Varint(1)                        // IsRainbow (skipped)
            .Tag(25, 0).Varint(2)                        // DamageMode (skipped)
            .Tag(9, 0).Varint(222)                       // HpLessenValue (read; appears after skipped block)
            .ToArray();

        var ok = SyncDamageInfoReader.TryRead(payload, out var msg);

        Assert.True(ok);
        Assert.Equal(111, msg.Value);
        Assert.Equal(222, msg.HpLessenValue);
    }

    [Fact]
    public void TryRead_NegativeNumbersRoundTripViaUnchecked()
    {
        // AttackerUuid is int64 with full sign range. The reader uses
        // unchecked((long)varint) so values with the high bit set come
        // through as negative. Critical for entity-uuid attribution.
        ulong wireValue = unchecked((ulong)-1L);  // 0xFFFF_FFFF_FFFF_FFFF
        var payload = new WireBytes().Tag(11, 0).Varint(wireValue).ToArray();

        var ok = SyncDamageInfoReader.TryRead(payload, out var msg);

        Assert.True(ok);
        Assert.Equal(-1L, msg.AttackerUuid);
    }

    [Fact]
    public void TryRead_MalformedTagBytes_ReturnsFalse()
    {
        // 0xFF without continuation EOFs the varint reader.
        var payload = new byte[] { 0xFF };
        var ok = SyncDamageInfoReader.TryRead(payload, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryRead_TruncatedVarintAfterTag_ReturnsFalse()
    {
        // Tag for field 6 (Value, varint) but value bytes never terminate.
        var payload = new byte[] { 0x30, 0xFF, 0xFF };
        var ok = SyncDamageInfoReader.TryRead(payload, out _);
        Assert.False(ok);
    }

    [Fact]
    public void TryRead_PropertyElement_PassedThrough()
    {
        // Plugin-side enum is decoded later; the reader surfaces the raw int.
        var payload = new WireBytes().Tag(18, 0).Varint(8).ToArray();  // Dark
        var ok = SyncDamageInfoReader.TryRead(payload, out var msg);
        Assert.True(ok);
        Assert.Equal(8, msg.Property);
    }

    [Fact]
    public void TryRead_DamageSourceKind_PassedThrough()
    {
        // Skill=0 / Bullet=1 / Buff=2 / Fall=3 / FakeBullet=4
        foreach (var (wire, expected) in new[] { (0UL, 0), (1UL, 1), (2UL, 2), (3UL, 3), (4UL, 4) })
        {
            var payload = new WireBytes().Tag(1, 0).Varint(wire).ToArray();
            Assert.True(SyncDamageInfoReader.TryRead(payload, out var msg));
            Assert.Equal(expected, msg.DamageSource);
        }
    }

    [Fact]
    public void TryRead_IsDeadFlag_DecodesBool()
    {
        var payload = new WireBytes().Tag(17, 0).Varint(1).ToArray();
        Assert.True(SyncDamageInfoReader.TryRead(payload, out var msg));
        Assert.True(msg.IsDead);

        payload = new WireBytes().Tag(17, 0).Varint(0).ToArray();
        Assert.True(SyncDamageInfoReader.TryRead(payload, out msg));
        Assert.False(msg.IsDead);
    }
}
