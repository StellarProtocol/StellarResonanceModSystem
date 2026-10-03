using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

internal sealed class UnityCaptureLens : ICaptureLens
{
    private readonly Camera _cam;
    public UnityCaptureLens(Camera cam) => _cam = cam;

    public float Aspect { get => _cam.aspect; set => _cam.aspect = value; }
    public float FieldOfView { get => _cam.fieldOfView; set => _cam.fieldOfView = value; }
    public bool UsePhysicalProperties => _cam.usePhysicalProperties;
    public float FocalLength { get => _cam.focalLength; set => _cam.focalLength = value; }
    public float SensorWidth => _cam.sensorSize.x;
    public float SensorHeight => _cam.sensorSize.y;
    public void ResetAspect() => _cam.ResetAspect();

    public LensGateFit GateFit
    {
        get => _cam.gateFit switch
        {
            Camera.GateFitMode.Vertical => LensGateFit.Vertical,
            Camera.GateFitMode.Horizontal => LensGateFit.Horizontal,
            Camera.GateFitMode.Fill => LensGateFit.Fill,
            Camera.GateFitMode.Overscan => LensGateFit.Overscan,
            _ => LensGateFit.None,
        };
        set => _cam.gateFit = value switch
        {
            LensGateFit.Vertical => Camera.GateFitMode.Vertical,
            LensGateFit.Horizontal => Camera.GateFitMode.Horizontal,
            LensGateFit.Fill => Camera.GateFitMode.Fill,
            LensGateFit.Overscan => Camera.GateFitMode.Overscan,
            _ => Camera.GateFitMode.None,
        };
    }
}
