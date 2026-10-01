using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Infrastructure.Game.Posing;
using Xunit;

namespace Stellar.Application.Tests.Posing;

// Recon photo-posing-recon.md § 1 table (camerasys_team_edit_tpl setHeadLookAt / setEyesLookAt / setLockPos): each look
// mode is exactly the photo panel's recipe. Free opens the look; the point itself is placed by Aim.
public sealed class LookPlanTests
{
    [Theory]
    [InlineData(LookPart.Head, LookMode.Default, false, "HeadClose,IkReset,HeadToNothing")]
    [InlineData(LookPart.Head, LookMode.Lens, false, "IkPhoto,HeadOpen,HeadToCamera")]
    [InlineData(LookPart.Head, LookMode.Lens, true, "IkPhoto,HeadOpen,HeadPinCamera")]
    [InlineData(LookPart.Head, LookMode.Free, false, "IkPhoto,HeadOpen")]
    [InlineData(LookPart.Head, LookMode.Free, true, "IkPhoto,HeadOpen")]
    [InlineData(LookPart.Eyes, LookMode.Default, false, "EyesClose,EyesToNothing")]
    [InlineData(LookPart.Eyes, LookMode.Lens, false, "EyesOpen,EyesToCamera")]
    [InlineData(LookPart.Eyes, LookMode.Lens, true, "EyesOpen,EyesPinCamera")]
    [InlineData(LookPart.Eyes, LookMode.Free, false, "EyesOpen")]
    public void Each_mode_is_the_photo_panels_recipe(LookPart part, LookMode mode, bool locked, string expected) =>
        Assert.Equal(expected, string.Join(",", LookPlan.For(part, mode, locked)));

    [Fact]
    public void Aim_targets_the_part()
    {
        Assert.Equal(LookStep.HeadToAim, LookPlan.AimStep(LookPart.Head));
        Assert.Equal(LookStep.EyesToAim, LookPlan.AimStep(LookPart.Eyes));
    }

    [Fact]
    public void Eye_steps_never_touch_the_head()
    {
        foreach (var mode in new[] { LookMode.Default, LookMode.Lens, LookMode.Free })
        foreach (var locked in new[] { false, true })
            Assert.DoesNotContain(LookPlan.For(LookPart.Eyes, mode, locked),
                s => s.ToString().StartsWith("Head") || s.ToString().StartsWith("Ik"));
    }
}
