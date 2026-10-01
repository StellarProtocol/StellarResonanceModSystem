using System;
using Stellar.Infrastructure.Game;
using Xunit;

namespace Stellar.Application.Tests.FreeCamera;

// Recon item 2b / E: the free-camera mask is 0x13FFB66FF over release_3.7's EInputMask order. Emote/Action stay open.
public sealed class ShieldMaskTests
{
    // Panda.ZGame.EInputMask, release_3.7, in declaration order (value = index).
    private static readonly string[] Release37 =
    {
        "Move", "Zoom", "Rotation", "Jump", "Rush", "NormalAttack", "ClimbRush", "RecoverCamera", "Arrow", "Aim", "Skill",
        "Action", "Emote", "Flow", "Glide", "MultAction", "AutoMove", "LockTarget", "DoByFuncId", "QuickUseItem",
        "ChangeWeapon", "Interact", "Dimension", "Walk", "Run", "UseQuestItem", "Resonance1", "Resonance2", "RotationX",
        "RotationY", "Insight", "UIInteract", "SkillWithWhiteList",
    };

    private static int? ValueOf(string name)
    {
        var i = Array.IndexOf(Release37, name);
        return i < 0 ? null : i;
    }

    [Fact]
    public void Composes_the_probed_mask() => Assert.Equal(0x13FFB66FFUL, ShieldMask.Compose(ValueOf));

    [Fact]
    public void Emote_and_action_bits_stay_open()
    {
        var mask = ShieldMask.Compose(ValueOf);
        Assert.Equal(0UL, mask & (1UL << 12));
        Assert.Equal(0UL, mask & (1UL << 11));
    }

    [Fact]
    public void Missing_names_are_skipped_not_guessed() =>
        Assert.Equal(1UL, ShieldMask.Compose(n => n == "Move" ? 0 : null));
}
