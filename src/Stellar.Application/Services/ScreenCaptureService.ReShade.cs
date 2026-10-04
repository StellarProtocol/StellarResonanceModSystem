using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>Which ReShade effects go into a capture, and the size-locked guard.</summary>
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
    /// The size-locked guard. ReShade 6.8.0 compiles a permutation of each effect per render-target size, but its named
    /// textures live in ONE global list: a texture sized from the screen (<see cref="EffectSizeLockScanner"/>) that the
    /// screen-size permutation already created is reused by a larger capture's permutation, which then reads only its
    /// top-left corner, magnified. So when the capture is not screen-sized and an ACTIVE technique is size-locked, ReShade
    /// never runs at that size: a window-shaped photo is taken at 1× (screen size, ReShade applied), and a shaped photo —
    /// never screen-sized — leaves the size-locked techniques out. Each says so in a note.
    /// <para>This also keeps a large-size compile failure off the screen: ReShade compiles a permutation only for
    /// effects it is asked to draw at that size, and a failed compile (AcerolaFX AutoExposure's
    /// <c>numthreads(3600,1,1)</c> at 4×) sets the effect's single <c>compiled</c> flag false and disables its techniques
    /// — on screen too (runtime.cpp:2164, 3763-3775). No size-locked effect ever gets a non-screen permutation here.</para>
    /// <para>Effects not scanned yet read as size-locked (fail-safe), like depth.</para>
    /// </summary>
    private (ReShadeCaptureOptions? Options, int Scale, string? Note) GuardSize(CaptureRequest r, ReShadeCaptureOptions? options, int scale)
    {
        if (options is null || !AnySizeLocked(options.Active)) return (options, scale, null);
        if (r.Aspect is null)
            return scale > 1 ? (options, 1, ReShadeCaptureNotes.ScreenSizeOnly) : (options, scale, null);
        var (w, h) = _grabber.ScreenSize;
        if (Target(r, scale, null).Size == new CaptureSize(w, h)) return (options, scale, null);
        return (options with { SkipSizeLocked = true }, scale, ReShadeCaptureNotes.ScreenSizeOnlySkipped);
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
