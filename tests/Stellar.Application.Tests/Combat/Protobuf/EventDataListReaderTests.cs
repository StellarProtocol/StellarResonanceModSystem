using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Combat.Protobuf;

public sealed class EventDataListReaderTests
{
    [Fact]
    public void TryRead_OneEventWithIntParams_Parses()
    {
        // Packed varint encoding is allowed for repeated scalar in proto3; many emitters use
        // length-delimited (wire-type 2) packed form. Test both: this one uses unpacked.
        var evt = new WireBytes()
            .Tag(1, 0).Varint(101)         // event_type = SkillEventSkillBegin
            .Tag(2, 0).Varint(12345)       // int_params[0]
            .Tag(2, 0).Varint(7)           // int_params[1]
            .ToArray();
        var payload = new WireBytes()
            .Tag(1, 0).Varint(123UL)
            .Tag(2, 2).LengthDelimited(evt)
            .ToArray();

        var ok = EventDataListReader.TryRead(payload, out var msg);

        Assert.True(ok);
        Assert.Equal(123L, msg.Uuid);
        Assert.Single(msg.Events);
        Assert.Equal(101, msg.Events[0].EventType);
        Assert.Equal(new[] { 12345, 7 }, msg.Events[0].IntParams);
    }

    [Fact]
    public void TryRead_PackedIntParams_Parses()
    {
        // Packed repeated varints: wire-type 2 followed by length-prefixed varint stream.
        var packedInts = new WireBytes().Varint(12345).Varint(7).ToArray();
        var evt = new WireBytes()
            .Tag(1, 0).Varint(101)
            .Tag(2, 2).LengthDelimited(packedInts)
            .ToArray();
        var payload = new WireBytes()
            .Tag(2, 2).LengthDelimited(evt)
            .ToArray();

        var ok = EventDataListReader.TryRead(payload, out var msg);

        Assert.True(ok);
        Assert.Equal(new[] { 12345, 7 }, msg.Events[0].IntParams);
    }

    [Fact]
    public void TryRead_EmptyEventsList_OK()
    {
        var payload = new WireBytes().Tag(1, 0).Varint(0).ToArray();
        var ok = EventDataListReader.TryRead(payload, out var msg);
        Assert.True(ok);
        Assert.Empty(msg.Events);
    }
}
