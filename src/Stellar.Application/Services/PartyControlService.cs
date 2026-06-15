using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Thin pass-through implementation of <see cref="IPartyControl"/>. The IL2CPP-/
/// Lua-bound work lives in <c>PandaTeamControlProbe</c> (Infrastructure).
/// </summary>
internal sealed class PartyControlService : IPartyControl
{
    private readonly IPartyControlProbe _probe;

    public PartyControlService(IPartyControlProbe probe) => _probe = probe;

    public bool IsAvailable => _probe.IsResolved;

    public void SetMemberType(PartyType size) => _probe.CallSetMemberType(size);

    public void MoveMember(long charId, int group, int slot) => _probe.CallMoveMember(charId, group, slot);
}
