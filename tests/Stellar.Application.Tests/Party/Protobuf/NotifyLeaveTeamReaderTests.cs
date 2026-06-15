using Stellar.Application.Tests.Wire;
using Stellar.Infrastructure.Game.Protobuf;
using Xunit;

namespace Stellar.Application.Tests.Party.Protobuf;

public sealed class NotifyLeaveTeamReaderTests
{
    [Fact]
    public void TryRead_CharIdAndLeaveType_Extracted()
    {
        var payload = new WireBytes()
            .Tag(1, 0).Varint(12345UL)
            .Tag(2, 0).Varint(2UL)
            .ToArray();

        var ok = NotifyLeaveTeamReader.TryRead(payload, out var msg);

        Assert.True(ok);
        Assert.Equal(12345L, msg.CharId);
        Assert.Equal(2,      msg.LeaveTypeRaw);
    }
}
