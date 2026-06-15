using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

public sealed class AoiSyncToMeDeltaReaderTests
{
    [Fact]
    public void TryRead_UuidAndCooldowns_Parses()
    {
        var cd1 = new WireBytes()
            .Tag(1, 0).Varint(100)
            .Tag(3, 0).Varint(5000)
            .ToArray();
        var cd2 = new WireBytes()
            .Tag(1, 0).Varint(200)
            .Tag(3, 0).Varint(15000)
            .Tag(4, 0).Varint(1)   // Charge
            .ToArray();
        var payload = new WireBytes()
            .Tag(3, 2).LengthDelimited(cd1)
            .Tag(3, 2).LengthDelimited(cd2)
            .Tag(5, 0).Varint(0xABCDEF1234567890UL)
            .ToArray();

        var ok = AoiSyncToMeDeltaReader.TryRead(payload, out var msg);

        Assert.True(ok);
        Assert.Equal(unchecked((long)0xABCDEF1234567890), msg.Uuid);
        Assert.Equal(2, msg.Cooldowns.Count);
        Assert.Equal(100, msg.Cooldowns[0].SkillId);
        Assert.Equal(200, msg.Cooldowns[1].SkillId);
    }

    [Fact]
    public void TryRead_WithBaseDelta_PopulatesBoth()
    {
        var baseDelta = new WireBytes().Tag(1, 0).Varint(999UL).ToArray();
        var payload = new WireBytes()
            .Tag(1, 2).LengthDelimited(baseDelta)
            .Tag(5, 0).Varint(0UL)
            .ToArray();

        var ok = AoiSyncToMeDeltaReader.TryRead(payload, out var msg);
        Assert.True(ok);
        Assert.NotNull(msg.BaseDelta);
        Assert.Equal(999L, msg.BaseDelta!.Value.Uuid);
    }

    [Fact]
    public void TryReadOuter_WrapsInOuterMessage()
    {
        // SyncToMeDeltaInfo wraps AoiSyncToMeDelta at field 1.
        var inner = new WireBytes().Tag(5, 0).Varint(7UL).ToArray();
        var payload = new WireBytes().Tag(1, 2).LengthDelimited(inner).ToArray();

        var ok = AoiSyncToMeDeltaReader.TryReadOuter(payload, out var msg);
        Assert.True(ok);
        Assert.Equal(7L, msg.Uuid);
    }
}
