using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

/// <summary><see cref="ICaptureLens"/> over a live Unity camera (main thread only).</summary>
internal sealed class UnityCaptureLens : ICaptureLens
{
    private readonly Camera _cam;
    public UnityCaptureLens(Camera cam) => _cam = cam;

    public float Aspect { get => _cam.aspect; set => _cam.aspect = value; }
    public float FieldOfView { get => _cam.fieldOfView; set => _cam.fieldOfView = value; }
    public void ResetAspect() => _cam.ResetAspect();
}
