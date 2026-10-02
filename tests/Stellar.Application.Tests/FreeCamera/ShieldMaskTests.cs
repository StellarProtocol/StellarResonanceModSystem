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

    // Owner report 2026-10-02 (TEST window, run 10): pressing WASD while frozen played the run in place — run 12 measured that one
    // PlayBaseState(ERun) re-poses the local model with the clock stopped (45 % of its region changed). The pause masks the
    // local player's movement and combat (live: IsInputIgnore(Move)=True, (Rotation)=False while paused), leaving the camera,
    // emotes and actions open. Mask 0x10FFB6679 measured in game. Do not weaken.
    [Fact]
    public void freeze_pause_input_block_masks_movement_and_combat_but_never_the_camera()
    {
        var mask = ShieldMask.Compose(ValueOf, ShieldMask.PauseBits);
        Assert.Equal(0x10FFB6679UL, mask);
        foreach (var bit in new[] { "Move", "Jump", "Rush", "NormalAttack", "Skill", "Walk", "Run", "AutoMove", "Interact" })
            Assert.NotEqual(0UL, mask & (1UL << ValueOf(bit)!.Value));
        foreach (var bit in new[] { "Zoom", "Rotation", "RecoverCamera", "RotationX", "RotationY", "Emote", "Action" })
            Assert.Equal(0UL, mask & (1UL << ValueOf(bit)!.Value));
    }
}
