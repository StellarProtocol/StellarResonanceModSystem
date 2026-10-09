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

    private static SocialLocation Loc(int map, float x) =>
        new(map, 0, 1, new Position3D(x, 0, 0), 0f, 0, 0, default);

    [Fact]
    public void Thin_reply_without_location_keeps_last_known_location()
    {
        // Only full-mask (ID-card) replies carry scene_data; a later nameplate/avatar reply must not wipe it.
        long now = 1_000;
        var cache = new SocialDataCache(() => now);
        cache.Push(Snap(4242, "Full") with { Location = Loc(8, 12f) });
        now = 5_000;
        cache.Push(Snap(4242, "Thin"));

        var got = cache.GetSocialSnapshot(Player(4242))!;
        Assert.Equal("Thin", got.Name);                          // the rest is still last-reply-wins
        Assert.Equal(8, got.Location!.Value.MapId);
        Assert.Equal(1_000, got.Location!.Value.ReceivedAtMs);   // age = the full reply, not the thin one
    }

    [Fact]
    public void Fresh_location_replaces_and_is_stamped_with_server_clock()
    {
        long now = 1_000;
        var cache = new SocialDataCache(() => now);
        cache.Push(Snap(4242, "A") with { Location = Loc(8, 12f) });
        now = 9_000;
        cache.Push(Snap(4242, "B") with { Location = Loc(7, 40f) });

        var loc = cache.GetSocialSnapshot(Player(4242))!.Location!.Value;
        Assert.Equal(7, loc.MapId);
        Assert.Equal(40f, loc.Pos.X);
        Assert.Equal(9_000, loc.ReceivedAtMs);
    }

    [Fact]
    public void Present_but_blank_location_replaces_rather_than_carries_forward()
    {
        // A zeroed (privacy-blanked) section is a real answer, not "absent" — it must not resurrect the old one.
        var cache = new SocialDataCache();
        cache.Push(Snap(4242, "A") with { Location = Loc(8, 12f) });
        cache.Push(Snap(4242, "B") with { Location = default(SocialLocation) });

        Assert.Equal(0, cache.GetSocialSnapshot(Player(4242))!.Location!.Value.MapId);
    }

    [Fact]
    public void No_clock_stamps_zero()
    {
        var cache = new SocialDataCache();
        cache.Push(Snap(4242, "A") with { Location = Loc(8, 12f) });
        Assert.Equal(0L, cache.GetSocialSnapshot(Player(4242))!.Location!.Value.ReceivedAtMs);
    }
}
