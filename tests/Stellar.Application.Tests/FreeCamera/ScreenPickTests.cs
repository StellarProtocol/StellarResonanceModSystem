using Stellar.Application.Services;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Spec § 3 "click a character": project each character's chest to the screen, pick the nearest within a radius that
// stands for ~0.6 m at that depth (18..160 px).
public sealed class ScreenPickTests
{
    [Fact]
    public void Radius_is_0_6_m_projected_and_clamped()
    {
        Assert.InRange(ScreenPick.RadiusPx(10f, 1080f, 60f), 56.0f, 56.2f);
        Assert.Equal(ScreenPick.MinRadiusPx, ScreenPick.RadiusPx(100f, 1080f, 60f));
        Assert.Equal(ScreenPick.MaxRadiusPx, ScreenPick.RadiusPx(1f, 1080f, 60f));
    }

    [Fact]
    public void The_nearest_candidate_in_pixels_wins()
    {
        var c = new[] { new PickCandidate(1, 500, 500, 10), new PickCandidate(2, 520, 500, 10) };
        Assert.Equal(2L, ScreenPick.Nearest(c, 515, 500, 1080, 60));
    }

    [Fact]
    public void Candidates_behind_the_camera_or_outside_the_radius_are_ignored()
    {
        var c = new[] { new PickCandidate(1, 500, 500, -3), new PickCandidate(2, 700, 500, 10) };
        Assert.Null(ScreenPick.Nearest(c, 500, 500, 1080, 60));
    }

    [Fact]
    public void Equal_pixel_distance_prefers_the_closer_character()
    {
        var c = new[] { new PickCandidate(1, 490, 500, 20), new PickCandidate(2, 510, 500, 8) };
        Assert.Equal(2L, ScreenPick.Nearest(c, 500, 500, 1080, 60));
    }

    [Fact]
    public void No_candidates_picks_nothing() => Assert.Null(ScreenPick.Nearest(new PickCandidate[0], 1, 1, 1080, 60));
}
