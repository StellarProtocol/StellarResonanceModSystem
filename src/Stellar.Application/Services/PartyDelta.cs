using System.Collections.Generic;

namespace Stellar.Application.Services;

/// <summary>One raid group's membership from NotifyTeamGroupUpdate: the group id + its ordered char ids
/// (index = the member's slot within the group).</summary>
public readonly record struct TeamGroupInfo(int GroupId, IReadOnlyList<long> CharIds);

/// <summary>
/// Discriminated queue payload between <c>PandaPartyStubProbe</c> (writer)
/// and <c>PartyService.Drain</c> (reader). Single-allocation per delta.
/// </summary>
internal abstract record PartyDelta
{
    internal sealed record FullSnapshot(PartyWireSnapshot Data, bool Authoritative) : PartyDelta;
    internal sealed record MemberFastSync(long CharId, PartyMemberFastSync Data) : PartyDelta;
    internal sealed record MemberSocialSync(long CharId, PartyMemberSocialSync Data) : PartyDelta;
    internal sealed record MemberLeft(long CharId, int LeaveTypeRaw) : PartyDelta;
    internal sealed record GroupLayout(IReadOnlyList<TeamGroupInfo> Groups) : PartyDelta;
    internal sealed record Dissolve : PartyDelta;
    internal sealed record ReadyCheckResponse(long CharId, string? Name, bool IsReady) : PartyDelta;
    internal sealed record ReadyCheckPhase(bool IsOpen) : PartyDelta;
    internal sealed record MicStatus(long CharId, int Raw) : PartyDelta;
    internal sealed record SpeakStatus(long CharId, int Raw) : PartyDelta;
}
