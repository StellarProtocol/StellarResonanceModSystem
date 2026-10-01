using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Recon run 2 F: the game's own release recipe leaves head=False where it was True; restore = snapshot + corrective
// writes. Pinned so a later "simplification" back to fixed off-values fails here.
public sealed class LookAtRestorePlanTests
{
    [Fact]
    public void Head_left_off_by_the_game_recipe_is_switched_back_on()
    {
        var w = LookAtRestorePlan.Corrections(new LookAtSnapshot(true, true, false), new LookAtSnapshot(true, false, false));
        Assert.Equal(new[] { new LookAtWrite(LookAtWriteKind.HeadClose, false) }, w);
    }

    [Fact]
    public void Eye_and_enable_drift_is_written_back()
    {
        var w = LookAtRestorePlan.Corrections(new LookAtSnapshot(false, true, false), new LookAtSnapshot(true, true, true));
        Assert.Equal(new[] { new LookAtWrite(LookAtWriteKind.EyeOpen, false), new LookAtWrite(LookAtWriteKind.Enable, false) }, w);
    }

    [Fact]
    public void Matching_state_needs_no_writes() =>
        Assert.Empty(LookAtRestorePlan.Corrections(new LookAtSnapshot(true, true, false), new LookAtSnapshot(true, true, false)));

    [Fact]
    public void Unreadable_after_state_writes_every_known_field()
    {
        var w = LookAtRestorePlan.Corrections(new LookAtSnapshot(true, true, null), null);
        Assert.Equal(new[] { new LookAtWrite(LookAtWriteKind.HeadClose, false), new LookAtWrite(LookAtWriteKind.Enable, true) }, w);
    }
}
