using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game;
using Xunit;
namespace Stellar.Application.Tests.Photo;

public sealed class EffectHideLedgerTests
{
    private const VisibilityLayers Mine = VisibilityLayers.EffectsMine, Mon = VisibilityLayers.EffectsMonsters;

    [Fact]
    public void Hides_only_wanted_visible_owned_effects_not_already_ours()
    {
        var l = new EffectHideLedger();
        Assert.True(l.ShouldHide(1, Mine, Mine | Mon, currentlyVisible: true));
        Assert.False(l.ShouldHide(2, VisibilityLayers.None, Mine | Mon, true));     // scenery / unresolved
        Assert.False(l.ShouldHide(3, VisibilityLayers.EffectsParty, Mine, true));   // not wanted
        Assert.False(l.ShouldHide(4, Mine, Mine, currentlyVisible: false));          // the game hid it — not ours
        l.MarkHidden(1, Mine);
        Assert.False(l.ShouldHide(1, Mine, Mine, true));                              // already ours
    }

    [Fact]
    public void Release_shows_only_ours_whose_group_is_no_longer_wanted_and_drops_ended()
    {
        var l = new EffectHideLedger();
        l.MarkHidden(1, Mine);
        l.MarkHidden(2, Mon);
        l.MarkHidden(3, Mine);
        var alive = new HashSet<long> { 1, 2 };   // 3 ended
        var show = l.TakeReleasable(wanted: Mon, alive.Contains);
        Assert.Equal(new long[] { 1 }, show);
        Assert.True(l.Contains(2));
        Assert.False(l.Contains(3));
        Assert.Equal(new long[] { 2 }, l.TakeReleasable(VisibilityLayers.None, alive.Contains));
        Assert.Equal(0, l.Count);
    }

    [Fact]
    public void Zero_uid_is_never_recorded()
    {
        var l = new EffectHideLedger();
        l.MarkHidden(0, Mine);
        Assert.Equal(0, l.Count);
    }
}
