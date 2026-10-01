using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Recon run 2 A/B, run 3 R3-1: unfreeze every effect uid we touched; restore the anim factor and the drawn speed we read
// — but never stomp a value the game wrote while we were frozen (a skill can set its own stage factor).
public sealed class FreezeLedgerTests
{
    [Fact]
    public void Restores_the_prior_factor_when_ours_is_still_in_place() =>
        Assert.Equal(1f, FreezeLedger.RestoreValue(prior: 1f, current: 0f));

    [Fact]
    public void Leaves_a_value_the_game_wrote_since() =>
        Assert.Null(FreezeLedger.RestoreValue(prior: 1f, current: 1.25f));

    [Fact]
    public void First_saved_factor_wins_and_effects_are_deduped()
    {
        var l = new FreezeLedger();
        l.SaveFactor(7, 1f);
        l.SaveFactor(7, 0f);
        l.TouchEffect(5);
        l.TouchEffect(5);
        Assert.Equal(1f, l.Factors[7]);
        Assert.Single(l.Effects);
        l.Clear();
        Assert.Empty(l.Factors);
        Assert.Empty(l.Effects);
    }

    [Fact]
    public void Restores_a_drawn_speed_only_while_ours_is_still_zero()
    {
        Assert.Equal(1f, FreezeLedger.RestoreSpeed(prior: 1f, current: 0f));
        Assert.Null(FreezeLedger.RestoreSpeed(prior: 1f, current: 1.1f));
    }

    [Fact]
    public void First_saved_speed_wins_and_Clear_drops_speeds()
    {
        var l = new FreezeLedger();
        l.SaveSpeed(3, 1f);
        l.SaveSpeed(3, 0f);
        Assert.Equal(1f, l.Speeds[3]);
        l.Clear();
        Assert.Empty(l.Speeds);
    }
}
