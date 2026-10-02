using System;
using Stellar.Abstractions.Domain;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Rendering;

/// <summary>The camera properties a shaped capture changes for its one render (a seam over <c>UnityEngine.Camera</c>).</summary>
internal interface ICaptureLens
{
    float Aspect { get; set; }
    float FieldOfView { get; set; }
    /// <summary>Back to Unity's automatic aspect (follows the window).</summary>
    void ResetAspect();
}

/// <summary>
/// Sets the camera's aspect to the photo's shape — and, for a shape wider than the screen, narrows the vertical view
/// angle so the horizontal one is kept (<see cref="CaptureSizing.VerticalFieldOfView"/>) — for ONE render, and puts
/// both back on <see cref="Dispose"/>: the aspect returns to automatic when it was the window's (Unity's default),
/// else to its previous value; the view angle is written back only if it was changed. Property writes only — no
/// HarmonyX patch is involved (devkit docs/il2cpp-probing-safety.md § "A third failure class").
/// </summary>
internal sealed class CaptureLensOverride : IDisposable
{
    private const double AutoAspectTolerance = 1e-3;
    private readonly ICaptureLens _lens;
    private readonly float _aspect;
    private readonly float _fov;
    private readonly bool _fovChanged;
    private readonly double _screenAspect;
    private bool _restored;

    private CaptureLensOverride(ICaptureLens lens, double screenAspect, double targetAspect)
    {
        _lens = lens;
        _aspect = lens.Aspect;
        _fov = lens.FieldOfView;
        _screenAspect = screenAspect;
        var onScreen = _aspect > 0f ? _aspect : screenAspect;   // the view the player is framing in
        var fov = CaptureSizing.VerticalFieldOfView(_fov, onScreen, targetAspect);
        TargetAspect = (float)targetAspect;
        TargetFieldOfView = fov;
        lens.Aspect = TargetAspect;
        _fovChanged = fov != _fov;
        if (_fovChanged) lens.FieldOfView = fov;
    }

    /// <summary>The aspect set for the render (diagnostics).</summary>
    public float TargetAspect { get; }
    /// <summary>The vertical view angle used for the render (diagnostics).</summary>
    public float TargetFieldOfView { get; }
    /// <summary>The aspect before the render (diagnostics).</summary>
    public float PreviousAspect => _aspect;

    /// <summary>Null (nothing touched) for a window-shaped grab, an empty size or an empty screen.</summary>
    public static CaptureLensOverride? Apply(ICaptureLens lens, GrabTarget target, int screenWidth, int screenHeight)
    {
        if (!target.Shaped || target.Size.IsEmpty || screenWidth <= 0 || screenHeight <= 0) return null;
        return new CaptureLensOverride(lens, (double)screenWidth / screenHeight, (double)target.Size.Width / target.Size.Height);
    }

    public void Dispose()
    {
        if (_restored) return;
        _restored = true;
        if (_fovChanged) _lens.FieldOfView = _fov;
        if (Math.Abs(_aspect - _screenAspect) < AutoAspectTolerance) _lens.ResetAspect();
        else _lens.Aspect = _aspect;
    }
}
