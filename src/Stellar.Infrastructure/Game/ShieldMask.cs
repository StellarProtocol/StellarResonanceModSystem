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
    /// RotationX/Y, RecoverCamera), so the game camera still turns while paused.</summary>
    internal static readonly string[] PauseBits =
    {
        "Move", "Jump", "Rush", "NormalAttack", "ClimbRush", "Aim", "Skill", "Flow", "Glide", "AutoMove", "LockTarget",
        "QuickUseItem", "ChangeWeapon", "Interact", "Dimension", "Walk", "Run", "UseQuestItem", "Resonance1", "Resonance2",
        "SkillWithWhiteList",
    };

    internal static ulong Compose(Func<string, int?> valueOf) => Compose(valueOf, Bits);

    internal static ulong Compose(Func<string, int?> valueOf, string[] bits)
    {
        ulong mask = 0;
        foreach (var name in bits)
            if (valueOf(name) is int v && v is >= 0 and < 64) mask |= 1UL << v;
        return mask;
    }
}
