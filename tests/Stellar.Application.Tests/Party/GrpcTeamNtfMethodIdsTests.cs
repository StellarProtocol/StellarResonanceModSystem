using Stellar.Wire;
using Xunit;

namespace Stellar.Application.Tests.Party;

/// <summary>
/// Guards against the placeholder <c>0</c> values shipping by accident.
/// If you're seeing this fail because you intentionally haven't filled
/// the bring-up values yet, this means you're trying to ship Phase 4
/// before observing the wire — that's the bug, not the test.
/// </summary>
public sealed class GrpcTeamNtfMethodIdsTests
{
    [Fact]
    public void Service_uuid_is_nonzero()
    {
        Assert.NotEqual(0UL, BPSRServiceIds.GrpcTeamNtf);
    }

    [Fact]
    public void All_method_ids_are_nonzero()
    {
        Assert.NotEqual(0u, GrpcTeamNtfMethodIds.NotifyJoinTeam);
        Assert.NotEqual(0u, GrpcTeamNtfMethodIds.NotifyLeaveTeam);
        Assert.NotEqual(0u, GrpcTeamNtfMethodIds.NoticeUpdateTeamInfo);
        Assert.NotEqual(0u, GrpcTeamNtfMethodIds.NoticeUpdateTeamMemberInfo);
        Assert.NotEqual(0u, GrpcTeamNtfMethodIds.NoticeTeamDissolve);
    }
}
