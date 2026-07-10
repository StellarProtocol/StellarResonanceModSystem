using Stellar.Application.Abstractions;

namespace Stellar.Application.Tests;

/// <summary>In-memory <see cref="ISocialRefreshRequester"/> that records the last requested
/// charId — used both as a plain no-op filler for unrelated <c>CombatService</c> tests and as
/// the fake for the refresh-forwarding assertion itself.</summary>
internal sealed class StubSocialRefreshRequester : ISocialRefreshRequester
{
    public long? LastCharId;
    public void RequestSelfSocialData(long charId) => LastCharId = charId;
}
