using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game.Capture;
using Stellar.Application.Tests.Wire;
using Xunit;

namespace Stellar.Application.Tests.Wire.Capture;

public sealed class TypedReaderRegistryTests
{
    // Helpers to build real wrapped shapes.
    private static byte[] MemberBytes(long charId, string name, int level)
    {
        var basic  = new WireBytes().Tag(3, 2).String(name).Tag(6, 0).Varint((ulong)level).ToArray();
        var social = new WireBytes().Tag(1, 2).LengthDelimited(basic).ToArray();
        return new WireBytes()
            .Tag(1, 0).Varint((ulong)charId)
            .Tag(9, 2).LengthDelimited(social)
            .ToArray();
    }

    private static byte[] WrappedReply(long teamId, long leader, byte[] member)
    {
        var baseInfo = new WireBytes().Tag(1, 0).Varint((ulong)teamId).Tag(3, 0).Varint((ulong)leader).ToArray();
        var reply    = new WireBytes().Tag(1, 2).LengthDelimited(baseInfo).Tag(2, 2).LengthDelimited(member).ToArray();
        return new WireBytes().Tag(1, 2).LengthDelimited(reply).ToArray();   // GetTeamInfo_Ret wrapper
    }

    [Fact]
    public void Return_ThatParsesAsGetTeamInfoReply_IsNamed()
    {
        // Build a wrapped GetTeamInfo_Ret{ GetTeamInfoReply{ base_info, member("Ribery",60) } }
        var payload = WrappedReply(777, 1, MemberBytes(1, "Ribery", 60));

        var reg   = new TypedReaderRegistry();
        var typed = reg.TryDecode(0, 0, WireMessageKind.Return, payload);

        Assert.NotNull(typed);
        Assert.Equal("GetTeamInfoReply", typed!.TypeName);
        Assert.Contains("PartyId",      typed.Fields.Keys);
        Assert.Equal(777L,              typed.Fields["PartyId"]);
        Assert.Contains("MemberCount",  typed.Fields.Keys);
        Assert.Equal(1,                 typed.Fields["MemberCount"]);
    }

    [Fact]
    public void Return_ThatIsNotTeamInfo_FallsBackToNull()
    {
        // A payload that has no field 1 (base_info) — GetTeamInfoReplyReader rejects it.
        var junk = new WireBytes().Tag(5, 0).Varint(1UL).ToArray();

        var reg = new TypedReaderRegistry();
        Assert.Null(reg.TryDecode(0, 0, WireMessageKind.Return, junk));
    }

    [Fact]
    public void NonReturn_Kind_AlwaysReturnsNull()
    {
        // Even a valid wrapped GetTeamInfoReply shape should not be decoded for non-Return frames.
        var payload = WrappedReply(42, 1, MemberBytes(1, "Solo", 60));

        var reg = new TypedReaderRegistry();
        Assert.Null(reg.TryDecode(0, 0, WireMessageKind.Call,   payload));
        Assert.Null(reg.TryDecode(0, 0, WireMessageKind.Notify, payload));
    }
}
