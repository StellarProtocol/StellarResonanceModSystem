using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

/// <summary>
/// Pins the AttrPos(52)/AttrDir(50) payload decode. Field layout VERIFIED against
/// StarResonanceData/proto/zproto/stru_position.proto (Position{ float x=1;y=2;z=3;dir=4 }).
/// </summary>
public sealed class WirePositionReaderTests
{
    // fixed32 float fields: tag = (field << 3) | 5.
    [Fact]
    public void TryReadPosition_TaggedXyz_Decodes()
    {
        var raw = new WireBytes()
            .Tag(1, 5).Fixed32(178.4f)
            .Tag(2, 5).Fixed32(100.2f)
            .Tag(3, 5).Fixed32(-303.7f)
            .ToArray();

        var ok = WirePositionReader.TryReadPosition(raw, out var pos);

        Assert.True(ok);
        Assert.Equal(178.4f, pos.X, 3);
        Assert.Equal(100.2f, pos.Y, 3);
        Assert.Equal(-303.7f, pos.Z, 3);
        Assert.False(pos.HasDir);
    }

    [Fact]
    public void TryReadPosition_TaggedWithDir_SetsHasDir()
    {
        var raw = new WireBytes()
            .Tag(1, 5).Fixed32(10f)
            .Tag(2, 5).Fixed32(100f)
            .Tag(3, 5).Fixed32(20f)
            .Tag(4, 5).Fixed32(1.57f)
            .ToArray();

        var ok = WirePositionReader.TryReadPosition(raw, out var pos);

        Assert.True(ok);
        Assert.True(pos.HasDir);
        Assert.Equal(1.57f, pos.Dir, 3);
    }

    [Fact]
    public void TryReadPosition_Packed12_DecodesXyzNoDir()
    {
        var raw = new WireBytes().Fixed32(1f).Fixed32(2f).Fixed32(3f).ToArray();
        Assert.Equal(12, raw.Length);

        var ok = WirePositionReader.TryReadPosition(raw, out var pos);

        Assert.True(ok);
        Assert.Equal(1f, pos.X, 3);
        Assert.Equal(2f, pos.Y, 3);
        Assert.Equal(3f, pos.Z, 3);
        Assert.False(pos.HasDir);
    }

    [Fact]
    public void TryReadPosition_Packed16_DecodesXyzDir()
    {
        var raw = new WireBytes().Fixed32(1f).Fixed32(2f).Fixed32(3f).Fixed32(4f).ToArray();
        Assert.Equal(16, raw.Length);

        var ok = WirePositionReader.TryReadPosition(raw, out var pos);

        Assert.True(ok);
        Assert.True(pos.HasDir);
        Assert.Equal(4f, pos.Dir, 3);
    }

    [Fact]
    public void TryReadPosition_Empty_ReturnsFalse()
        => Assert.False(WirePositionReader.TryReadPosition(System.Array.Empty<byte>(), out _));

    [Fact]
    public void TryReadPosition_DirOnlyTagged_ReturnsFalse()
    {
        // Field 4 (dir) only, no x/y/z — not a real position.
        var raw = new WireBytes().Tag(4, 5).Fixed32(1.0f).ToArray();
        Assert.False(WirePositionReader.TryReadPosition(raw, out _));
    }

    [Fact]
    public void TryReadDir_FourBytes_Decodes()
    {
        var raw = new WireBytes().Fixed32(2.5f).ToArray();
        var ok = WirePositionReader.TryReadDir(raw, out var dir);
        Assert.True(ok);
        Assert.Equal(2.5f, dir, 3);
    }

    [Fact]
    public void TryReadDir_WrongLength_ReturnsFalse()
        => Assert.False(WirePositionReader.TryReadDir(new byte[] { 1, 2, 3 }, out _));
}
