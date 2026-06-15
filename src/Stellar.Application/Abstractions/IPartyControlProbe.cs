using Stellar.Abstractions.Domain;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound port for party control. Implemented in Infrastructure by the Lua
/// bridge (<c>PandaTeamControlProbe</c>); Application declares it so
/// <c>PartyControlService</c> stays IL2CPP-free.
/// </summary>
internal interface IPartyControlProbe
{
    bool IsResolved { get; }

    void CallSetMemberType(PartyType size);

    void CallMoveMember(long charId, int group, int slot);
}
