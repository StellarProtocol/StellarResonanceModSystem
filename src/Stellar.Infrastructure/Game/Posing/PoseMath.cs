using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>The numbers behind the photo panel's controls (recon § 1 / probe run 4): the Free-look joystick point,
/// moment ↔ persist time, the face id for a model's gender. Unity-free, so it is unit-tested.</summary>
internal static class PoseMath
{
    /// <summary>Metres right of the head at full deflection (probe run 4 used ±0.6 m).</summary>
    public const float RightReach = 0.6f;
    /// <summary>Metres above the head at full deflection (probe run 4 used 0.5 m).</summary>
    public const float UpReach = 0.5f;
    /// <summary>The look point's local depth the panel forces (camerasys_main_pc_view coordinateTransformation).</summary>
    public const float LocalDepth = 0.2f;
    /// <summary>Below this an action's own length readback is "not set yet".</summary>
    public const float MinTotal = 0.1f;

    public static (float Right, float Up) AimOffset(float x, float y) =>
        (Math.Clamp(x, -1f, 1f) * RightReach, Math.Clamp(y, -1f, 1f) * UpReach);

    /// <summary><c>SetActionPersistTime</c> argument: −1 plays; otherwise the held time in seconds.</summary>
    public static float PersistTime(float fraction, float total) => fraction < 0f ? -1f : Math.Clamp(fraction, 0f, 1f) * total;

    /// <summary>How far an action has played (0–1), −1 when its length is unknown.</summary>
    public static float Fraction(float passed, float total) => total <= MinTotal ? -1f : Math.Clamp(passed / total, 0f, 1f);

    /// <summary>The model's own readback (<c>GetLuaAttrActionInfoTotalTime</c>) when set, else the action table's length.</summary>
    public static float Total(float readback, float table) => readback > MinTotal ? readback : table;

    /// <summary><c>FaceDataId[2]</c> for a female model (gender 2), else <c>[1]</c> (recon: a wrong pick renders, silently).</summary>
    public static int FaceId(ExpressionInfo e, int gender) => gender == 2 ? e.FemaleFaceId : e.MaleFaceId;
}
