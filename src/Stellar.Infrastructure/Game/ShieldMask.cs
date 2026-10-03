using System;
namespace Stellar.Infrastructure.Game;

/// <summary>The free-camera input mask (recon item 2b): movement, camera, combat and interaction bits, by name.</summary>
internal static class ShieldMask
{
    internal static readonly string[] Bits =
    {
        "Move", "Zoom", "Rotation", "Jump", "Rush", "NormalAttack", "ClimbRush", "RecoverCamera", "Aim", "Skill", "Flow",
        "Glide", "AutoMove", "LockTarget", "QuickUseItem", "ChangeWeapon", "Interact", "Dimension", "Walk", "Run",
        "UseQuestItem", "Resonance1", "Resonance2", "RotationX", "RotationY", "SkillWithWhiteList",
    };

    /// <summary>The scene freeze's pause input block (owner report 2026-10-02: WASD while frozen played the run in place):
    /// the local player's movement, combat and interaction — <see cref="Bits"/> without the camera bits (Zoom, Rotation,
    /// RotationX/Y, RecoverCamera), so the game camera still turns while paused. A subset of <see cref="Bits"/>; both ride the
    /// same <c>EGm</c> source as a union (<see cref="ZIgnoreShieldBackend"/>).</summary>
    internal static readonly string[] PauseBits =
    {
        "Move", "Jump", "Rush", "NormalAttack", "ClimbRush", "Aim", "Skill", "Flow", "Glide", "AutoMove", "LockTarget",
        "QuickUseItem", "ChangeWeapon", "Interact", "Dimension", "Walk", "Run", "UseQuestItem", "Resonance1", "Resonance2",
        "SkillWithWhiteList",
    };

    /// <summary>Moving the one source from the mask <paramref name="applied"/> to <paramref name="next"/>: the bits to drop (−1
    /// each) and the bits to add (+1 each). The game counts every bit per source, so a bit already set is never set again
    /// (it would need two clears) and a bit both masks share is never touched — dropping the free camera keeps the pause
    /// block's bits, and the other way round (qa M-4).</summary>
    internal static (ulong Clear, ulong Add) Transition(ulong applied, ulong next) => (applied & ~next, next & ~applied);

    internal static ulong Compose(Func<string, int?> valueOf) => Compose(valueOf, Bits);

    internal static ulong Compose(Func<string, int?> valueOf, string[] bits)
    {
        ulong mask = 0;
        foreach (var name in bits)
            if (valueOf(name) is int v && v is >= 0 and < 64) mask |= 1UL << v;
        return mask;
    }
}
