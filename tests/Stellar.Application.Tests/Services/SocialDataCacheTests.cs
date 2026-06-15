using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

public class SocialDataCacheTests
{
    // The world server encodes a player's EntityId as (charId << 16) | 640 — see PartyMember.EntityId.
    private static EntityId Player(long charId) => new((charId << 16) | 640);

    private static SocialSnapshot Snap(long charId, string name) =>
        new(charId, name, 60, 1000, 1, System.Array.Empty<GearSlotRef>(), System.Array.Empty<FashionEntry>(),
            SocialIdentity.None);

    [Fact]
    public void Push_then_Get_returns_snapshot_by_entity()
    {
        var cache = new SocialDataCache();
        cache.Push(Snap(4242, "Eiori"));
        Assert.Equal("Eiori", cache.GetSocialSnapshot(Player(4242))!.Name);
    }

    [Fact]
    public void Push_replaces_previous_and_never_evicts()
    {
        var cache = new SocialDataCache();
        cache.Push(Snap(4242, "Old"));
        cache.Push(Snap(4242, "New"));
        Assert.Equal("New", cache.GetSocialSnapshot(Player(4242))!.Name);
    }

    [Fact]
    public void Get_unknown_entity_returns_null()
        => Assert.Null(new SocialDataCache().GetSocialSnapshot(Player(999)));

    [Fact]
    public void Get_non_player_entity_returns_null()
    {
        var cache = new SocialDataCache();
        cache.Push(Snap(4242, "Eiori"));
        // A monster entity-type (low 16 bits = 64) must never resolve a social snapshot.
        Assert.Null(cache.GetSocialSnapshot(new EntityId((4242L << 16) | 64)));
    }

    [Fact]
    public void Get_large_charId_does_not_truncate()
    {
        // charIds exceed int range; keying on Value >> 16 (long) must survive the full id.
        const long bigCharId = 5_000_000_000L;
        var cache = new SocialDataCache();
        cache.Push(Snap(bigCharId, "Far"));
        Assert.Equal("Far", cache.GetSocialSnapshot(Player(bigCharId))!.Name);
    }
}
