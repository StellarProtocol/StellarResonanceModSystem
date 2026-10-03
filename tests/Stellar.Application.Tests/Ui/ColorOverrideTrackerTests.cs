using Stellar.Infrastructure.Game;
using Xunit;
namespace Stellar.Application.Tests.Ui;

// Photo Studio fw fix round (measured in game): a Pill whose Color func went from a warning colour back to null
// kept the warning colour after unpinning. Every Text/Pill Color binding now restores the element's default colour
// on that flip — once, never on a null that was never overridden (that would stomp a reskin every poll).
public sealed class ColorOverrideTrackerTests
{
    [Fact]
    public void Null_without_a_prior_override_does_nothing()
    {
        var t = new ColorOverrideTracker();
        Assert.Equal(ColorStep.None, t.Next(hasOverride: false));
        Assert.Equal(ColorStep.None, t.Next(hasOverride: false));
    }

    [Fact]
    public void An_override_is_applied_every_poll()
    {
        var t = new ColorOverrideTracker();
        Assert.Equal(ColorStep.Override, t.Next(hasOverride: true));
        Assert.Equal(ColorStep.Override, t.Next(hasOverride: true));   // a reskin between polls must not win
    }

    [Fact]
    public void Colour_back_to_null_restores_the_default_exactly_once()
    {
        var t = new ColorOverrideTracker();
        t.Next(hasOverride: true);
        Assert.Equal(ColorStep.RestoreDefault, t.Next(hasOverride: false));
        Assert.Equal(ColorStep.None, t.Next(hasOverride: false));
    }

    [Fact]
    public void Override_again_after_a_restore_overrides_and_can_restore_again()
    {
        var t = new ColorOverrideTracker();
        t.Next(true); t.Next(false);
        Assert.Equal(ColorStep.Override, t.Next(true));
        Assert.Equal(ColorStep.RestoreDefault, t.Next(false));
    }
}
