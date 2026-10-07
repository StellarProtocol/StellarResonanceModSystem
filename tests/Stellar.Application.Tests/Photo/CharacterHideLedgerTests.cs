using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;
namespace Stellar.Application.Tests.Photo;

// The per-character hides write per-source BITS (ZEntityHelper.setVisible bts/btr; the weapon's EPhoto alpha), not
// counters: a wanted hide is re-issued on every pass (idempotent, and it re-applies after the game rebuilt a weapon), a
// show only for a character WE hid — never for someone the game or another source hid.
public sealed class CharacterHideLedgerTests
{
    [Fact]
    public void A_wanted_hide_is_issued_every_time_and_held_once()
    {
        var l = new CharacterHideLedger();
        Assert.True(l.Step(7, want: true));
        Assert.True(l.Step(7, want: true));   // re-issued: a bit, re-set is a no-op in game
        Assert.Equal(1, l.Count);
    }

    [Fact]
    public void A_show_is_issued_only_for_a_character_we_hid()
    {
        var l = new CharacterHideLedger();
        Assert.Null(l.Step(7, want: false));   // never hid it: no write at all
        l.Step(8, want: true);
        Assert.False(l.Step(8, want: false));
        Assert.Null(l.Step(8, want: false));   // shown once
        Assert.Equal(0, l.Count);
    }

    [Fact]
    public void A_failed_hide_is_not_held_and_a_failed_show_stays_held_for_the_next_pass()
    {
        var l = new CharacterHideLedger();
        l.Step(1, want: true);
        l.Failed(1, hide: true);
        Assert.False(l.Holds(1));
        l.Step(2, want: true);
        Assert.False(l.Step(2, want: false));
        l.Failed(2, hide: false);
        Assert.True(l.Holds(2));
        Assert.False(l.Step(2, want: false));   // retried
    }

    [Fact]
    public void Prune_forgets_characters_that_left_view_so_a_returning_one_starts_clean()
    {
        var l = new CharacterHideLedger();
        l.Step(1, want: true);
        l.Step(2, want: true);
        l.Prune(new HashSet<long> { 2 });
        Assert.False(l.Holds(1));
        Assert.True(l.Holds(2));
        Assert.Null(l.Step(1, want: false));   // nothing to show back for someone who is gone
    }
}
