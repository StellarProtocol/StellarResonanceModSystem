using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.Services;

// Per-section merge: a thin reply (basic-only, or nameplate basic+avatar+personal_zone) must not wipe what a
// full ID-card reply already told us, and a reply without presence must not flip a known online state.
public class SocialDataCacheMergeTests
{
    private const long CharId = 4242;

    private static SocialSnapshot Full() =>
        new(CharId, "Full", 60, 47_597, 7, new[] { new GearSlotRef(200, 1001) },
            new[] { new FashionEntry(1, 2, System.Array.Empty<ColorRgba>()) },
            new SocialIdentity("Guild", 5, 2868, 301, 10, 20, 30), "https://p", "https://h")
        {
            Presence = new SocialPresence(0, 1_000, new[] { 1 }),
            Sections = SocialSections.Basic | SocialSections.Avatar | SocialSections.Profession | SocialSections.Equip
                     | SocialSections.Fashion | SocialSections.Attr | SocialSections.Team | SocialSections.Union
                     | SocialSections.PersonalZone | SocialSections.MasterMode,
        };

    // What the reader produces for a mask-1 (basic only) reply: every other member at its default.
    private static SocialSnapshot BasicOnly(string name, SocialPresence? presence) =>
        new(CharId, name, 61, 0, 0, System.Array.Empty<GearSlotRef>(), System.Array.Empty<FashionEntry>(), SocialIdentity.None)
        { Presence = presence, Sections = SocialSections.Basic };

    [Fact]
    public void Basic_only_reply_keeps_full_sections_and_takes_its_own()
    {
        var cache = new SocialDataCache();
        cache.Push(Full());
        cache.Push(BasicOnly("Renamed", new SocialPresence(9_000, 1_000, System.Array.Empty<int>())));

        var got = cache.GetByCharId(CharId)!;
        Assert.Equal("Renamed", got.Name);                     // basic_data was carried → replaced
        Assert.Equal(61, got.Level);
        Assert.False(got.IsOnline);
        Assert.Equal(47_597, got.FightPoint);                  // everything else kept from the full reply
        Assert.Equal(7, got.ProfessionId);
        Assert.Single(got.Gear);
        Assert.Single(got.Fashion);
        Assert.Equal(new SocialIdentity("Guild", 5, 2868, 301, 10, 20, 30), got.Identity);
        Assert.Equal("https://p", got.ProfileUrl);
        Assert.Equal(Full().Sections, got.Sections);
    }

    [Fact]
    public void Nameplate_reply_replaces_only_its_own_identity_parts()
    {
        var cache = new SocialDataCache();
        cache.Push(Full());
        var nameplate = BasicOnly("Full", null) with
        {
            Identity = new SocialIdentity("", 0, 0, 999, 11, 21, 31),
            ProfileUrl = "https://p2",
            Sections = SocialSections.Basic | SocialSections.Avatar | SocialSections.PersonalZone,
        };
        cache.Push(nameplate);

        var got = cache.GetByCharId(CharId)!;
        Assert.Equal(new SocialIdentity("Guild", 5, 2868, 999, 11, 21, 31), got.Identity);
        Assert.Equal("https://p2", got.ProfileUrl);
        Assert.Equal("", got.HalfBodyUrl);                     // avatar section carried → its empty value is real
    }

    [Fact]
    public void Reply_without_presence_keeps_known_online_state()
    {
        long now = 1_000;
        var cache = new SocialDataCache(() => now);
        cache.Push(Full());
        now = 5_000;
        cache.Push(BasicOnly("Full", null));

        var got = cache.GetByCharId(CharId)!;
        Assert.True(got.IsOnline);
        Assert.Equal(1_000, got.Presence!.Value.ReceivedAtMs);  // age = the reply that carried it
    }

    [Fact]
    public void Fresh_presence_replaces_and_is_stamped()
    {
        long now = 1_000;
        var cache = new SocialDataCache(() => now);
        cache.Push(Full());
        now = 7_000;
        cache.Push(BasicOnly("Full", new SocialPresence(6_500, 1_000, new[] { 6 })));

        var p = cache.GetByCharId(CharId)!.Presence!.Value;
        Assert.False(p.IsOnline);
        Assert.Equal(6_500, p.OfflineTimeMs);
        Assert.Equal(new[] { 6 }, p.PersonalStates);
        Assert.Equal(7_000, p.ReceivedAtMs);
    }

    [Fact]
    public void Carried_section_with_empty_value_replaces()
    {
        // equip_data present but empty = the player really has nothing equipped; don't resurrect the old gear.
        var cache = new SocialDataCache();
        cache.Push(Full());
        cache.Push(BasicOnly("Full", null) with { Sections = SocialSections.Basic | SocialSections.Equip });
        Assert.Empty(cache.GetByCharId(CharId)!.Gear);
    }

    [Fact]
    public void Hand_built_snapshot_without_sections_replaces_everything()
    {
        // Sections == None (not decoded) keeps the pre-merge last-reply-wins behaviour for value sections.
        var cache = new SocialDataCache();
        cache.Push(Full());
        cache.Push(BasicOnly("Plain", null) with { Sections = SocialSections.None });

        var got = cache.GetByCharId(CharId)!;
        Assert.Equal(0, got.FightPoint);
        Assert.Empty(got.Gear);
        Assert.True(got.IsOnline);                             // presence is still null-carried
    }
}
