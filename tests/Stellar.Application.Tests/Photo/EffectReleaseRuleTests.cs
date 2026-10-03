using System.Collections.Generic;
using Stellar.Infrastructure.Game;
using Xunit;
namespace Stellar.Application.Tests.Photo;

public sealed class EffectReleaseRuleTests
{
    private static HashSet<long> Listed(params long[] uids) => new(uids);

    [Fact]
    public void Null_listing_is_unknown_regardless_of_instance_state()
    {
        Assert.Equal(EffectReleaseState.Unknown, EffectReleaseRule.Resolve(null, 1, null, false));
        Assert.Equal(EffectReleaseState.Unknown, EffectReleaseRule.Resolve(null, 1, 1, true));
    }

    [Fact]
    public void Listed_uid_shows_through_the_manager()
    {
        Assert.Equal(EffectReleaseState.Manager, EffectReleaseRule.Resolve(Listed(1, 2), 1, null, false));
    }

    [Fact]
    public void Unlisted_with_matching_instance_uid_shows_through_the_instance()
    {
        Assert.Equal(EffectReleaseState.Instance, EffectReleaseRule.Resolve(Listed(2), 1, 1, false));
    }

    [Fact]
    public void Unlisted_with_mismatched_instance_uid_is_ended()
    {
        // Pooled ZEffect recycled into a different effect before we got around to showing it back.
        Assert.Equal(EffectReleaseState.Ended, EffectReleaseRule.Resolve(Listed(2), 1, 99, false));
    }

    [Fact]
    public void Unlisted_with_destroyed_instance_is_ended_even_if_uid_would_match()
    {
        Assert.Equal(EffectReleaseState.Ended, EffectReleaseRule.Resolve(Listed(2), 1, 1, true));
    }

    [Fact]
    public void Unlisted_with_no_instance_is_ended()
    {
        Assert.Equal(EffectReleaseState.Ended, EffectReleaseRule.Resolve(Listed(2), 1, null, false));
    }
}
