using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>Which ReShade effects go into a capture, and how size-locked effects are drawn (isolated, or the guard).</summary>
internal sealed partial class ScreenCaptureService
{
    // ReShade draws into the photo only when the request asks for it, ReShade is available, its effects are on, and at
    // least one technique is active — otherwise the capture is exactly the pre-ReShade one (no options at all).
    private ReShadeCaptureOptions? ReShadeOptions(CaptureRequest r)
    {
        if (!r.ApplyReShade || _reShade is null) return null;
        if (_reShade is IReShadeLiveRead live) live.RefreshNow();   // a toggle made just before the shutter counts
        if (_reShade is not { State: ReShadeState.Ready, Enabled: true } reShade) return null;
        List<ReShadeTechnique>? active = null;
        foreach (var technique in reShade.Techniques)
        {
            if (technique.Enabled) (active ??= new List<ReShadeTechnique>()).Add(technique);
        }
        return active is null ? null : new ReShadeCaptureOptions(Shaped: r.Aspect is not null, active);
    }


    /// <summary>
    /// The capture target, with the isolated capture planned when it is needed. ReShade 6.8.0 compiles a permutation of
    /// each effect per render-target size, but its named textures live in ONE global list: a texture sized from the screen
    /// (<see cref="EffectSizeLockScanner"/>) that the screen-size permutation already created is reused by a larger
    /// capture's permutation, which then reads only its top-left corner, magnified. So when the capture is not
    /// screen-sized and an ACTIVE technique is size-locked, the game's runtime never draws at that size: the photo is
    /// drawn in a separate runtime of its own size (bridge 1.1.0, <see cref="IsolatedCapture"/>), and when that is not
    /// possible the grabber takes the size guard planned here as its fallback — a window-shaped photo at 1× (screen size,
    /// ReShade applied), a shaped photo (never screen-sized) without its size-locked techniques — each with its note.
    /// <para>This also keeps a large-size compile failure off the screen: ReShade compiles a permutation only for
    /// effects it is asked to draw at that size, and a failed compile (AcerolaFX AutoExposure's
    /// <c>numthreads(3600,1,1)</c> at 4×) sets the effect's single <c>compiled</c> flag false and disables its techniques
    /// — on screen too (runtime.cpp:2164, 3763-3775). The game's runtime never gets a non-screen permutation of a
    /// size-locked effect here; the isolated runtime's failures stay in the isolated runtime.</para>
    /// <para>Effects not scanned yet read as size-locked (fail-safe), like depth.</para>
    /// </summary>
    private GrabTarget PlannedTarget(CaptureRequest r, int scale, ReShadeCaptureOptions? options)
    {
        var target = Target(r, scale, options);
        var (w, h) = _grabber.ScreenSize;
        if (options is null || !AnySizeLocked(options.Active) || target.Size == new CaptureSize(w, h)) return target;
        var isolated = r.Aspect is null
            ? new IsolatedCapture(Target(r, 1, options), ReShadeCaptureNotes.ScreenSizeOnly)
            : new IsolatedCapture(Target(r, scale, options with { SkipSizeLocked = true }), ReShadeCaptureNotes.ScreenSizeOnlySkipped);
        return target with { ReShade = options with { Isolated = isolated with { WarmUp = AnyTemporal(options.Active) } } };
    }

    // Unknown (no traits) = temporal: a needless warm-up costs time, a missing one a wrong photo.
    private bool AnyTemporal(IReadOnlyList<ReShadeTechnique> active)
    {
        if (_reShade is not IReShadeEffectTraits traits) return true;
        foreach (var technique in active)
        {
            if (traits.IsTemporal(technique.EffectFile)) return true;
        }
        return false;
    }

    private static bool AnySizeLocked(IReadOnlyList<ReShadeTechnique> active)
    {
        foreach (var technique in active)
        {
            if (technique.SizeLocked) return true;
        }
        return false;
    }
}
