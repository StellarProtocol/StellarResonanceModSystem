using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Infrastructure.Rendering;

/// <summary>Unity's <c>Camera.GateFitMode</c>, mapped by name in <see cref="UnityCaptureLens"/>.</summary>
internal enum LensGateFit { None, Vertical, Horizontal, Fill, Overscan }

/// <summary>The camera's lens as the player frames with it (read before a shaped render).</summary>
internal readonly record struct LensState(
    float Aspect, float FieldOfView, bool Physical, LensGateFit GateFit, float FocalLength, float SensorWidth, float SensorHeight)
{
    /// <summary>A physical lens whose sensor and focal length can be reasoned about (else Unity's own fieldOfView rules).</summary>
    public bool PhysicalUsable => Physical && SensorWidth > 0f && SensorHeight > 0f && FocalLength > 0f;
}

/// <summary>
/// The lens for one shaped render. <see cref="VerticalAngle"/> is the vertical view angle the photo is rendered with.
/// A non-physical camera gets it as <c>fieldOfView</c>; a physical camera stays physical (lens shift kept) and gets an
/// explicit Vertical gate fit plus the focal length that gives that angle — the on-screen gate fit (Horizontal, Fill,
/// Overscan) would otherwise frame a different shape (a 9:16 shot under Horizontal fit keeps the screen WIDTH).
/// </summary>
internal readonly record struct LensPlan(float Aspect, float VerticalAngle, bool Physical, LensGateFit GateFit, float FocalLength);

/// <summary>
/// Pure framing maths for a shaped capture (portrait-capture spec 2026-10-03 § 3 + amendment): the photo frames exactly
/// <see cref="CaptureSizing.GuideRect"/> — the on-screen VERTICAL angle for shapes as narrow as the view or narrower
/// (portrait / square), the on-screen HORIZONTAL angle for wider ones (21:9) — whatever the camera's physical mode.
/// </summary>
internal static class CaptureLensPlanner
{
    private const double FocalTolerance = 1e-5;

    /// <summary>The vertical angle the player actually sees: <c>fieldOfView</c> for a normal camera; for a physical one,
    /// the angle Unity's gate fit derives from the sensor, the focal length and the view's aspect.</summary>
    public static double OnScreenVerticalAngle(in LensState s, double viewAspect)
    {
        if (!s.PhysicalUsable || viewAspect <= 0d) return s.FieldOfView;
        var fit = ResolveFit(s.GateFit, viewAspect, (double)s.SensorWidth / s.SensorHeight);
        var halfTan = fit == LensGateFit.Horizontal
            ? s.SensorWidth / (2d * s.FocalLength) / viewAspect
            : s.SensorHeight / (2d * s.FocalLength);
        return Math.Atan(halfTan) * 360d / Math.PI;
    }

    /// <summary>The lens for a render of <paramref name="targetAspect"/> framing what the player sees on a
    /// <paramref name="screenAspect"/> window.</summary>
    public static LensPlan Plan(in LensState s, double screenAspect, double targetAspect)
    {
        var view = s.Aspect > 0f ? s.Aspect : screenAspect;   // the view the player is framing in
        if (!s.PhysicalUsable)
            return new LensPlan((float)targetAspect, CaptureSizing.VerticalFieldOfView(s.FieldOfView, view, targetAspect),
                s.Physical, s.GateFit, s.FocalLength);
        var angle = CaptureSizing.VerticalFieldOfView((float)OnScreenVerticalAngle(s, view), view, targetAspect);
        var focal = (float)(s.SensorHeight / (2d * Math.Tan(angle * Math.PI / 360d)));
        if (Math.Abs(focal - s.FocalLength) <= FocalTolerance * s.FocalLength) focal = s.FocalLength;   // float round trip only
        return new LensPlan((float)targetAspect, angle, true, LensGateFit.Vertical, focal);
    }

    /// <summary>Unity's gate fit for a view: Fill crops (fits the side that keeps the view inside the sensor), Overscan
    /// shows more (the other side); None stretches the sensor, so its vertical angle is the sensor's.</summary>
    public static LensGateFit ResolveFit(LensGateFit fit, double viewAspect, double sensorAspect) => fit switch
    {
        LensGateFit.Horizontal => LensGateFit.Horizontal,
        LensGateFit.Fill => viewAspect > sensorAspect ? LensGateFit.Horizontal : LensGateFit.Vertical,
        LensGateFit.Overscan => viewAspect > sensorAspect ? LensGateFit.Vertical : LensGateFit.Horizontal,
        _ => LensGateFit.Vertical,
    };

    /// <summary>True when the lens read after the restore is the lens read before the capture (the next-frame proof).</summary>
    public static bool SameLens(in LensState before, in LensState after) =>
        Math.Abs(before.Aspect - after.Aspect) < 1e-3f
        && Math.Abs(before.FieldOfView - after.FieldOfView) < 1e-3f
        && before.Physical == after.Physical
        && before.GateFit == after.GateFit
        && Math.Abs(before.FocalLength - after.FocalLength) < 1e-3f;
}
