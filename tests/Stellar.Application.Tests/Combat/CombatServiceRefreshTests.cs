using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Combat;

public sealed class CombatServiceRefreshTests
{
    [Fact]
    public void RefreshSocialSnapshot_forwards_charId_from_entity()
    {
        var requester = new StubSocialRefreshRequester();
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), requester);

        // charId 1248014 -> entity (charId<<16 | 640), matching SocialDataCache's charId decode.
        var entity = new EntityId((1248014L << 16) | 640);
        svc.RefreshSocialSnapshot(entity);

        Assert.Equal(1248014, requester.LastCharId);
    }

    [Fact]
    public void RefreshSocialSnapshot_is_noop_for_non_player_entity()
    {
        var requester = new StubSocialRefreshRequester();
        var svc = new CombatService(new StubLog(), new CombatEntityTracker(), new SocialDataCache(), requester);

        // low 16 bits 64 -> monster marker, not player.
        var entity = new EntityId((1L << 16) | 64);
        svc.RefreshSocialSnapshot(entity);

        Assert.Null(requester.LastCharId);
    }
}
