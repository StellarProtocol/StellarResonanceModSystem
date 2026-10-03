using System;
using Stellar.Abstractions.Domain;
using UnityEngine;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>Runs <see cref="LookPlan"/> on one model with the photo panel's calls (recon § 1, run 4 (c)/(d)): Lens =
/// <c>SetLookAtTransform(m, camera, false, main)</c>; a locked Lens = <c>SetLookAtPos(m, LuaWorldPosToLocal(m, camera), main)</c>;
/// Free = a point right/up of the head as the camera sees it, local z forced to 0.2 (coordinateTransformation). The head
/// uses <c>main: true</c>, the eyes <c>main: false</c>. Main thread.</summary>
internal sealed class LookDriver
{
    private readonly PoseModelCalls _models;
    private readonly Func<Camera?> _camera;
    private readonly object _model;

    public LookDriver(PoseModelCalls models, Func<Camera?> camera, object model)
    {
        _models = models;
        _camera = camera;
        _model = model;
    }

    public void Apply(LookPart part, LookMode mode, bool locked)
    {
        foreach (var step in LookPlan.For(part, mode, locked)) Run(step, 0f, 0f);
    }

    public void Aim(LookPart part, float x, float y) => Run(LookPlan.AimStep(part), x, y);

    /// <summary>The probe's release recipe (run 4 (f) "look release": head/eye flags back to the pre-state).</summary>
    public void Restore(LookAtSnapshot? pre)
    {
        _models.IkReset(_model);
        _models.HeadClose(_model, pre?.Head != true);
        _models.EyeOpen(_model, pre?.Eye == true);
        _models.LookTransform(_model, null, main: true);
        _models.LookTransform(_model, null, main: false);
    }

    private void Run(LookStep step, float x, float y)
    {
        switch (step)
        {
            case LookStep.IkPhoto: _models.IkPhoto(_model); break;
            case LookStep.IkReset: _models.IkReset(_model); break;
            case LookStep.HeadOpen: _models.HeadClose(_model, false); break;
            case LookStep.HeadClose: _models.HeadClose(_model, true); break;
            case LookStep.EyesOpen: _models.EyeOpen(_model, true); break;
            case LookStep.EyesClose: _models.EyeOpen(_model, false); break;
            case LookStep.HeadToCamera: ToCamera(main: true); break;
            case LookStep.EyesToCamera: ToCamera(main: false); break;
            case LookStep.HeadToNothing: _models.LookTransform(_model, null, main: true); break;
            case LookStep.EyesToNothing: _models.LookTransform(_model, null, main: false); break;
            case LookStep.HeadPinCamera: PinCamera(main: true); break;
            case LookStep.EyesPinCamera: PinCamera(main: false); break;
            case LookStep.HeadToAim: AimAt(x, y, main: true); break;
            case LookStep.EyesToAim: AimAt(x, y, main: false); break;
        }
    }

    private void ToCamera(bool main)
    {
        var cam = _camera();
        if (cam != null) _models.LookTransform(_model, cam.transform, main);
    }

    private void PinCamera(bool main)
    {
        var cam = _camera();
        if (cam != null && _models.ToLocal(_model, cam.transform.position) is Vector3 local) _models.LookPos(_model, local, main);
    }

    private void AimAt(float x, float y, bool main)
    {
        var cam = _camera();
        if (cam == null || _models.Head(_model) is not Vector3 head) return;
        var (right, up) = PoseMath.AimOffset(x, y);
        var t = cam.transform;
        if (_models.ToLocal(_model, head + t.right * right + t.up * up) is not Vector3 local) return;
        local.z = PoseMath.LocalDepth;
        _models.LookPos(_model, local, main);
    }
}
