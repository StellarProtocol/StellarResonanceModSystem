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

    // Review M-4 (2026-10-03) + release_3.7 ISIL: ZIgnoreMgr.SetInputIgnore(mask, ignore, source) adds ±1 to a per-bit,
    // per-source COUNTER (AddIgnore ignoreLayer = ignore ? 1 : -1) — a bit is ignored while its count is above 0. The one
    // source therefore moves between unions by the difference only: setting a bit twice would need two clears (movement stuck
    // after unfreeze). Replays every layer change the shield makes against that counter: each bit stays at 0 or 1, the union
    // is exactly what is masked, and both layers off leaves every count at 0. Do not weaken.
    [Fact]
    public void freeze_one_source_union_moves_by_difference_and_never_double_counts_a_bit()
    {
        var camera = ShieldMask.Compose(ValueOf, ShieldMask.Bits);
        var pause = ShieldMask.Compose(ValueOf, ShieldMask.PauseBits);
        Assert.Equal(pause, pause & camera);                    // the pause block is a subset of the camera's mask
        var counts = new int[64];
        ulong applied = 0;
        void Apply(bool cam, bool pz)
        {
            var next = (cam ? camera : 0UL) | (pz ? pause : 0UL);
            var (clear, add) = ShieldMask.Transition(applied, next);
            for (var i = 0; i < 64; i++)
            {
                if ((clear & (1UL << i)) != 0) counts[i]--;
                if ((add & (1UL << i)) != 0) counts[i]++;
            }
            applied = (applied & ~clear) | add;
            for (var i = 0; i < 64; i++)
            {
                Assert.InRange(counts[i], 0, 1);
                Assert.Equal((next & (1UL << i)) != 0, counts[i] == 1);
            }
        }
        foreach (var (cam, pz) in new[] { (false, true), (true, true), (false, true), (true, true), (true, false), (false, false),
                                          (true, false), (true, true), (false, false) })
            Apply(cam, pz);
        Assert.All(counts, c => Assert.Equal(0, c));
        Assert.Equal((0UL, 0UL), ShieldMask.Transition(camera, camera));          // no change: no call
        Assert.Equal((camera & ~pause, 0UL), ShieldMask.Transition(camera, pause)); // camera off, pause on: only camera-only bits drop
    }
}
