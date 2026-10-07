using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// Framework 2.20.0 per-character hides (owner go 2026-10-07, "go i want all"): Friends / Party / Guild become REAL hides
// and Weapon hides every player's weapon. The game's own model (static RE of ZEntityMgr.isCharVisibleBySource, release_3.7,
// devkit docs/recon/photo-hide-recon.md § Q2): the local player is always visible, Other adventurers (Nearby) is the
// catch-all, and a group switch only stops RESCUING its members. Our body rule adds the missing hide: a player in at
// least one group, every one of whose groups is hidden, is hidden — a still-shown group keeps rescuing them, so the
// owner-reported "Keep my party visible hides the party anyway" fix (2026-10-01) keeps a party member who is also a
// guild mate visible.
public sealed class CharacterHideRulesTests
{
    private const VisibilityLayers OA = VisibilityLayers.Strangers;     // "Other adventurers"
    private const VisibilityLayers P = VisibilityLayers.Party;
    private const VisibilityLayers F = VisibilityLayers.Friends;
    private const VisibilityLayers G = VisibilityLayers.Guild;
    private const CharacterRelation Party = CharacterRelation.Party;
    private const CharacterRelation Friend = CharacterRelation.Friend;
    private const CharacterRelation Guild = CharacterRelation.Guild;

    /// <summary>The game's own answer for one character under the photo source (isCharVisibleBySource steps 1-7; the
    /// master switch 11 and camera members aside): local → visible; in a group whose switch is NOT hidden → visible
    /// (rescued); otherwise visible iff Other adventurers is not hidden.</summary>
    private static bool GameVisible(VisibilityLayers req, CharacterRelation rel)
    {
        if ((rel & CharacterRelation.Local) != 0) return true;
        if ((rel & Party) != 0 && (req & P) == 0) return true;
        if ((rel & Guild) != 0 && (req & G) == 0) return true;
        if ((rel & Friend) != 0 && (req & F) == 0) return true;
        return (req & OA) == 0;
    }

    private static bool Visible(VisibilityLayers req, CharacterRelation rel) =>
        GameVisible(req, rel) && !CharacterHideRules.HideBody(req, rel);

    // The truth table the owner asked for, row by row: (request, who) → visible on screen (game AND framework).
    public static IEnumerable<object[]> Table() => new[]
    {
        // Guild hidden while Other adventurers is SHOWN: guild mates go, strangers stay (the 2026-10-07 MAIN report).
        new object[] { G, (int)(Guild), false },
        new object[] { G, (int)(CharacterRelation.None), true },
        new object[] { F, (int)(Friend), false },
        new object[] { P, (int)(Party), false },
        new object[] { F, (int)(CharacterRelation.None), true },
        // Other adventurers hidden + Party NOT hidden → the party stays visible (the game's rescue).
        new object[] { OA, (int)(Party), true },
        new object[] { OA, (int)(CharacterRelation.None), false },
        // Other adventurers hidden + Party hidden → the party is hidden too.
        new object[] { OA | P, (int)(Party), false },
        // A still-shown group keeps rescuing: party member + guild mate with only Guild hidden stays (keep-party fix).
        new object[] { G, (int)(Party | Guild), true },
        new object[] { OA | F | G, (int)(Party | Guild), true },     // "Keep my party visible" (1.6.0 migration) keeps them
        new object[] { OA | F | G, (int)(Guild), false },
        new object[] { P | G, (int)(Party | Guild), false },          // every group they are in is hidden
        new object[] { P | G, (int)(Party | Guild | Friend), true },  // ...but they are also a friend, and Friends is shown
        // Nothing requested: nobody is hidden.
        new object[] { VisibilityLayers.None, (int)(Party | Friend | Guild), true },
        // The local player is never hidden by these switches, whatever they are.
        new object[] { OA | P | F | G, (int)(CharacterRelation.Local | Party | Guild), true },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void Truth_table(VisibilityLayers request, int who, bool visible) =>   // int: CharacterRelation is internal
        Assert.Equal(visible, Visible(request, (CharacterRelation)who));

    [Fact]
    public void Strangers_are_never_hidden_by_the_framework_they_follow_other_adventurers()
    {
        foreach (var req in new[] { OA, P | F | G, OA | P | F | G, VisibilityLayers.None })
            Assert.False(CharacterHideRules.HideBody(req, CharacterRelation.None));
    }

    [Fact]
    public void Weapon_hides_every_other_player_but_leaves_the_local_one_to_the_games_own_switch()
    {
        var req = VisibilityLayers.Weapons;
        Assert.True(CharacterHideRules.HideWeapon(req, CharacterRelation.None));
        Assert.True(CharacterHideRules.HideWeapon(req, Party | Guild));
        Assert.False(CharacterHideRules.HideWeapon(req, CharacterRelation.Local));   // SetEntityShow(10) owns it
        Assert.False(CharacterHideRules.HideWeapon(OA | P | F | G, Party));         // no Weapon switch, no weapon hide
    }

    [Fact]
    public void Only_the_four_per_character_layers_need_a_pass()
    {
        Assert.True(CharacterHideRules.Needed(VisibilityLayers.Guild));
        Assert.True(CharacterHideRules.Needed(VisibilityLayers.Weapons));
        Assert.False(CharacterHideRules.Needed(OA | VisibilityLayers.OtherPlayers | VisibilityLayers.KeepParty |
                                               VisibilityLayers.SelfCharacter | VisibilityLayers.Enemies));
    }

    [Fact]
    public void Legacy_other_players_with_keep_party_hides_nobody_extra()
    {
        // 2.16-2.19 plugins send OtherPlayers | KeepParty (= Stranger + Chum + Union in the game) and never the new bits:
        // the per-character rule must add nothing, so they behave exactly as before.
        var legacy = VisibilityLayers.OtherPlayers | VisibilityLayers.KeepParty | VisibilityLayers.Self;
        foreach (var who in new[] { Party, Friend, Guild, Party | Guild, CharacterRelation.None })
            Assert.False(CharacterHideRules.HideBody(legacy, who));
        Assert.False(CharacterHideRules.Needed(legacy));
    }

    // Owner MAIN 2026-10-07: guild mate stayed visible with Guild ticked — the cache holds CHAR IDS, we asked by uuid only.
    [Fact]
    public void Membership_cache_matches_by_char_id_as_well_as_uuid()
    {
        var charIdSet = new HashSet<long> { 2066899 };
        Assert.True(CharacterHideRules.InCache(charIdSet.Contains, uuid: (2066899L << 16) | 640, charId: 2066899));
        var uuidSet = new HashSet<long> { (2066899L << 16) | 640 };
        Assert.True(CharacterHideRules.InCache(uuidSet.Contains, uuid: (2066899L << 16) | 640, charId: 2066899));
        Assert.False(CharacterHideRules.InCache(new HashSet<long> { 0 }.Contains, uuid: 5, charId: 0));
    }
}
