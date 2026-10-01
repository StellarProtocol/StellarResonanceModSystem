using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.Unity;
using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

// Reflection: CinemachineCameraBackend.Reflection.cs. Diagnostics: CinemachineCameraBackend.Diagnostics.cs.

/// <summary>
/// The free camera as the game's own Cinemachine sees it (recon item 1, PASS): a runtime-created
/// <c>CinemachineVirtualCamera</c> at a priority above the game's <c>CM FreeLook</c> (10) takes the brain with the
/// game's default blend; pose = the vcam's transform; FOV and roll = its lens (<c>FieldOfView</c>, <c>Dutch</c>), which the
/// brain copies to Main Camera every frame without fighting <c>CameraManager.UpdateResetFov</c>. Destroying the
/// GameObject blends back to the game camera. Main thread.
/// </summary>
internal sealed partial class CinemachineCameraBackend : ICameraBackend
{
    private const string Tag = "[FreeCam] ";
    private const int TopPriority = 10_000;

    private readonly IGameTypeRegistry _types;
    private readonly GameEntityAccess _entities;
    private readonly FrameDriverHost _driver;
    private readonly IPluginLog _log;
    private GameObject? _go;
    private object? _vcam;
    private float _lensFov = float.NaN;
    private float _lensRoll = float.NaN;

    public CinemachineCameraBackend(IGameTypeRegistry types, GameEntityAccess entities, FrameDriverHost driver, IPluginLog log)
    {
        _types = types;
        _entities = entities;
        _driver = driver;
        _log = log;
    }

    public event Action<float>? Frame;

    public CameraPose? ReadGamePose()
    {
        var cam = MainCamera();
        if (cam == null) return null;
        var t = cam.transform;
        var p = t.position;
        var e = t.rotation.eulerAngles;
        return new CameraPose(new Position3D(p.x, p.y, p.z), e.y, Signed(e.x), Signed(e.z), cam.fieldOfView);
    }

    public Position3D? ReadLocalPlayerPosition()
    {
        var model = _entities.LiveModel(_entities.LocalEntity());
        return model is not null && _entities.AttrPosition(model) is Vector3 v ? new Position3D(v.x, v.y, v.z) : null;
    }

    public bool TryBegin(CameraPose start)
    {
        if (!ResolveVcam()) { WarnResolveOnce(); return false; }
        try
        {
            _go = new GameObject("StellarFreeCamera") { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(_go);
            _vcam = AddVcam(_go);
            InitLens(start.Fov);
            _priority!.SetValue(_vcam, TopPriority);
            Apply(start);
            _driver.SetFrame(OnFrame);
            OnBegun(start);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning(Tag + "could not create the free camera: " + (ex.InnerException ?? ex).Message);
            DestroyCamera();
            return false;
        }
    }

    public void Apply(CameraPose pose)
    {
        if (_go == null || _vcam is null) return;
        _go.transform.SetPositionAndRotation(
            new Vector3(pose.Position.X, pose.Position.Y, pose.Position.Z), Quaternion.Euler(pose.Pitch, pose.Yaw, 0f));
        if (pose.Fov != _lensFov || pose.Roll != _lensRoll) WriteLens(pose.Fov, pose.Roll);
    }

    public void End()
    {
        _driver.SetFrame(null);
        DestroyCamera();
        OnEnded();
    }

    private void OnFrame(float dt) => Frame?.Invoke(dt);

    private void DestroyCamera()
    {
        if (_go != null) UnityEngine.Object.Destroy(_go);
        _go = null;
        _vcam = null;
        _lensFov = float.NaN;
        _lensRoll = float.NaN;
    }

    private static float Signed(float deg) => deg > 180f ? deg - 360f : deg;

    partial void OnBegun(CameraPose start);
    partial void OnEnded();
}
