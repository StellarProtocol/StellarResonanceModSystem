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

    internal static ulong Compose(Func<string, int?> valueOf)
    {
        ulong mask = 0;
        foreach (var name in Bits)
            if (valueOf(name) is int v && v is >= 0 and < 64) mask |= 1UL << v;
        return mask;
    }
}
