using System;
using Stellar.Application.Abstractions;
namespace Stellar.Infrastructure.Rendering;

internal interface ICaptureLens
{
    float Aspect { get; set; }
    float FieldOfView { get; set; }
    bool UsePhysicalProperties { get; }
    LensGateFit GateFit { get; set; }
    float FocalLength { get; set; }
    float SensorWidth { get; }
    float SensorHeight { get; }
    void ResetAspect();
}

/// <summary>
/// Sets the camera's lens for ONE shaped render (<see cref="CaptureLensPlanner.Plan"/>) and puts back exactly what it
/// changed — aspect (back to automatic when it was the window's), fieldOfView, gate fit, focal length — once, even when
/// the apply itself fails half-way. Physical mode is never toggled (lens shift and sensor stay as the game set them).
/// </summary>
internal sealed class CaptureLensOverride : IDisposable
{
    private const double AutoAspectTolerance = 1e-3;
    private readonly ICaptureLens _lens;
    private readonly double _screenAspect;
    private bool _aspectSet, _fovSet, _fitSet, _focalSet, _restored;

    private CaptureLensOverride(ICaptureLens lens, double screenAspect, double targetAspect)
    {
        _lens = lens;
        _screenAspect = screenAspect;
        Previous = Read(lens);
        Plan = CaptureLensPlanner.Plan(Previous, screenAspect, targetAspect);
        try { Write(); }
        catch
        {
            Dispose();   // never leave a half-applied lens behind (e.g. the aspect set, the angle write threw)
            throw;
        }
    }

    public LensState Previous { get; }
    public LensPlan Plan { get; }

    public static CaptureLensOverride? Apply(ICaptureLens lens, GrabTarget target, int screenWidth, int screenHeight)
    {
        if (!target.Shaped || target.Size.IsEmpty || screenWidth <= 0 || screenHeight <= 0) return null;
        return new CaptureLensOverride(lens, (double)screenWidth / screenHeight, (double)target.Size.Width / target.Size.Height);
    }

    public static LensState Read(ICaptureLens lens) => new(lens.Aspect, lens.FieldOfView, lens.UsePhysicalProperties,
        lens.GateFit, lens.FocalLength, lens.SensorWidth, lens.SensorHeight);

    private void Write()
    {
        // Each flag is raised BEFORE its write: restoring an untouched property to its own value is harmless, missing
        // a property whose setter threw after taking effect is not.
        _aspectSet = true;
        _lens.Aspect = Plan.Aspect;
        if (Plan.Physical)
        {
            if (Plan.GateFit != Previous.GateFit) { _fitSet = true; _lens.GateFit = Plan.GateFit; }
            if (Plan.FocalLength != Previous.FocalLength) { _focalSet = true; _lens.FocalLength = Plan.FocalLength; }
        }
        else if (Plan.VerticalAngle != Previous.FieldOfView)
        {
            _fovSet = true;
            _lens.FieldOfView = Plan.VerticalAngle;
        }
    }

    public void Dispose()
    {
        if (_restored) return;
        _restored = true;
        Exception? failure = null;
        if (_fitSet) Step(RestoreGateFit, ref failure);
        if (_focalSet) Step(RestoreFocalLength, ref failure);   // a physical camera derives its fieldOfView from this
        if (_fovSet) Step(RestoreFieldOfView, ref failure);
        if (_aspectSet) Step(RestoreAspect, ref failure);
        if (failure is not null) throw failure;
    }

    // Every restore runs even when an earlier one throws; the first failure is reported after all of them.
    private static void Step(Action restore, ref Exception? failure)
    {
        try { restore(); }
        catch (Exception ex) { failure ??= ex; }
    }

    private void RestoreGateFit() => _lens.GateFit = Previous.GateFit;
    private void RestoreFocalLength() => _lens.FocalLength = Previous.FocalLength;
    private void RestoreFieldOfView() => _lens.FieldOfView = Previous.FieldOfView;

    private void RestoreAspect()
    {
        if (Math.Abs(Previous.Aspect - _screenAspect) < AutoAspectTolerance) _lens.ResetAspect();
        else _lens.Aspect = Previous.Aspect;
    }
}
