using Stellar.Abstractions.Domain;
using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

public sealed class SkillCDInfoReaderTests
{
    // Reference: stru_skill_c_d_info.proto
    //   field 1 = skill_level_id (int32)
    //   field 2 = skill_begin_time (int64)
    //   field 3 = duration (int32)
    //   field 4 = skill_cd_type (uint32)
    //   field 6 = profession_hold_begin_time (int64) [we don't expose]
    //   field 7 = charge_count (int32)
    //   field 8 = valid_cd_time (int32)

    [Fact]
    public void TryRead_AllFieldsPresent_PopulatesEveryField()
    {
        var bytes = new WireBytes()
            .Tag(1, 0).Varint(12345)        // skill_level_id
            .Tag(2, 0).Varint(1_700_000_000_000UL)  // skill_begin_time
            .Tag(3, 0).Varint(15_000)       // duration
            .Tag(4, 0).Varint(1)            // skill_cd_type = Charge
            .Tag(7, 0).Varint(3)            // charge_count
            .Tag(8, 0).Varint(14_500)       // valid_cd_time
            .ToArray();

        var ok = SkillCDInfoReader.TryRead(bytes, out var cd);

        Assert.True(ok);
        Assert.Equal(12345,          cd.SkillId);
        Assert.Equal(1_700_000_000_000L, cd.BeginTimeMs);
        Assert.Equal(15_000,         cd.DurationMs);
        Assert.Equal(SkillCooldownKind.Charge, cd.Kind);
        Assert.Equal(3,              cd.ChargeCount);
        Assert.Equal(14_500,         cd.ValidCdTimeMs);
    }

    [Fact]
    public void TryRead_MissingOptionalFields_LeavesDefaults()
    {
        var bytes = new WireBytes()
            .Tag(1, 0).Varint(7)
            .Tag(3, 0).Varint(8_000)
            .ToArray();

        var ok = SkillCDInfoReader.TryRead(bytes, out var cd);

        Assert.True(ok);
        Assert.Equal(7, cd.SkillId);
        Assert.Equal(0L, cd.BeginTimeMs);
        Assert.Equal(8_000, cd.DurationMs);
        Assert.Equal(SkillCooldownKind.Normal, cd.Kind);  // default 0
        Assert.Equal(0, cd.ChargeCount);
    }

    [Fact]
    public void TryRead_UnknownField_IsSkipped()
    {
        var bytes = new WireBytes()
            .Tag(1, 0).Varint(99)
            .Tag(99, 0).Varint(0xDEAD)   // unknown — must skip
            .Tag(3, 0).Varint(1000)
            .ToArray();

        var ok = SkillCDInfoReader.TryRead(bytes, out var cd);

        Assert.True(ok);
        Assert.Equal(99, cd.SkillId);
        Assert.Equal(1000, cd.DurationMs);
    }

    [Fact]
    public void TryRead_TruncatedInput_ReturnsFalse()
    {
        // Tag for field 2 (varint) but no value bytes follow.
        var bytes = new byte[] { 0x10 };
        var ok = SkillCDInfoReader.TryRead(bytes, out _);
        Assert.False(ok);
    }
}
