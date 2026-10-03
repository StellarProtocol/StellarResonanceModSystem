using System.Collections.Generic;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>One call of the photo panel's look recipes (recon § 1 table).</summary>
internal enum LookStep
{
    IkPhoto,        // ZModelHelper.SetLookAtIKParam(m, 1)
    IkReset,        // ZModelHelper.ResetLookAtIKParam(m)
    HeadOpen,       // m.SetLuaAttrLookAtHeadClose(false)
    HeadClose,      // m.SetLuaAttrLookAtHeadClose(true)
    EyesOpen,       // m.SetLuaAttrLookAtEyeOpen(true)
    EyesClose,      // m.SetLuaAttrLookAtEyeOpen(false)
    HeadToCamera,   // SetLookAtTransform(m, camera, false, main: true)
    HeadToNothing,  // SetLookAtTransform(m, null, false, main: true)
    HeadPinCamera,  // SetLookAtPos(m, LuaWorldPosToLocal(m, camera position), main: true)
    HeadToAim,      // SetLookAtPos(m, joystick local (z = 0.2), main: true)
    EyesToCamera,   // SetLookAtTransform(m, camera, false, main: false)
    EyesToNothing,  // SetLookAtTransform(m, null, false, main: false)
    EyesPinCamera,  // SetLookAtPos(m, LuaWorldPosToLocal(m, camera position), main: false)
    EyesToAim,      // SetLookAtPos(m, joystick local (z = 0.2), main: false)
}

/// <summary>The photo panel's head / eye recipes as data (camerasys_team_edit_tpl setHeadLookAt / setEyesLookAt /
/// setLockPos — recon § 1): Default releases, Lens follows the camera, a locked Lens pins the camera's position now,
/// Free opens the look and leaves the point to <see cref="AimStep"/>. Eye recipes never touch the head IK.</summary>
internal static class LookPlan
{
    private static readonly LookStep[] HeadDefault = { LookStep.HeadClose, LookStep.IkReset, LookStep.HeadToNothing };
    private static readonly LookStep[] HeadLens = { LookStep.IkPhoto, LookStep.HeadOpen, LookStep.HeadToCamera };
    private static readonly LookStep[] HeadLensLocked = { LookStep.IkPhoto, LookStep.HeadOpen, LookStep.HeadPinCamera };
    private static readonly LookStep[] HeadFree = { LookStep.IkPhoto, LookStep.HeadOpen };
    private static readonly LookStep[] EyesDefault = { LookStep.EyesClose, LookStep.EyesToNothing };
    private static readonly LookStep[] EyesLens = { LookStep.EyesOpen, LookStep.EyesToCamera };
    private static readonly LookStep[] EyesLensLocked = { LookStep.EyesOpen, LookStep.EyesPinCamera };
    private static readonly LookStep[] EyesFree = { LookStep.EyesOpen };

    public static IReadOnlyList<LookStep> For(LookPart part, LookMode mode, bool locked) => (part, mode) switch
    {
        (LookPart.Head, LookMode.Default) => HeadDefault,
        (LookPart.Head, LookMode.Lens) => locked ? HeadLensLocked : HeadLens,
        (LookPart.Head, _) => HeadFree,
        (_, LookMode.Default) => EyesDefault,
        (_, LookMode.Lens) => locked ? EyesLensLocked : EyesLens,
        _ => EyesFree,
    };

    public static LookStep AimStep(LookPart part) => part == LookPart.Head ? LookStep.HeadToAim : LookStep.EyesToAim;
}
