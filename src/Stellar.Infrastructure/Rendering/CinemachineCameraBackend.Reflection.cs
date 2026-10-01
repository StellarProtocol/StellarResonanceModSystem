using System;
using System.Reflection;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Abstractions.Services;
using Stellar.Infrastructure.Game;
using UnityEngine;
namespace Stellar.Infrastructure.Rendering;

/// <summary>Cinemachine + CameraManager member resolution (no compile-time Cinemachine reference — CI builds on stubs).</summary>
internal sealed partial class CinemachineCameraBackend
{
    internal const string CameraManagerType = "Panda.ZGame.CameraManager";
    internal const string VcamType = "Cinemachine.CinemachineVirtualCamera";

    private readonly SingletonAccess _cameraManager = new();
    private PropertyInfo? _mainCamera, _lens, _priority;
    private FieldInfo? _fov, _dutch, _near, _far;
    private MethodInfo? _cast;
    private Il2CppSystem.Type? _vcamIl2Cpp;
    private bool _warnedResolve;

    private bool ResolveVcam()
    {
        if (_vcamIl2Cpp is not null) return true;
        var t = _types.FindType(VcamType);
        if (t is null) return false;
        _lens = StellarInterop.FindPropertyUp(t, "m_Lens");
        _priority = StellarInterop.FindPropertyUp(t, "Priority");
        var lensType = _lens?.PropertyType;
        _fov = lensType?.GetField("FieldOfView");
        _dutch = lensType?.GetField("Dutch");
        _near = lensType?.GetField("NearClipPlane");
        _far = lensType?.GetField("FarClipPlane");
        if (_lens is null || _priority is null || _fov is null || _dutch is null || _near is null || _far is null) return false;
        _cast = typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.Cast))!.MakeGenericMethod(t);
        _vcamIl2Cpp = Il2CppType.From(t);
        return true;
    }

    private object AddVcam(GameObject go)
    {
        var comp = go.AddComponent(_vcamIl2Cpp!) ?? throw new InvalidOperationException("AddComponent returned null");
        return _cast!.Invoke(comp, null) ?? throw new InvalidOperationException("cast to CinemachineVirtualCamera failed");
    }

    private void InitLens(float fov)
    {
        var lens = _lens!.GetValue(_vcam)!;   // boxed LensSettings; field writes mutate the box
        var cam = MainCamera();
        if (cam != null)
        {
            _near!.SetValue(lens, cam.nearClipPlane);
            _far!.SetValue(lens, cam.farClipPlane);
        }
        _fov!.SetValue(lens, fov);
        _dutch!.SetValue(lens, 0f);
        _lens.SetValue(_vcam, lens);
        _lensFov = fov;
        _lensRoll = 0f;
    }

    private void WriteLens(float fov, float roll)
    {
        var lens = _lens!.GetValue(_vcam)!;
        _fov!.SetValue(lens, fov);
        _dutch!.SetValue(lens, roll);
        _lens.SetValue(_vcam, lens);
        _lensFov = fov;
        _lensRoll = roll;
    }

    /// <summary>The game's main camera (<c>CameraManager.Instance.MainCamera</c>, else <c>Camera.main</c>).</summary>
    internal Camera? MainCamera()
    {
        var t = _types.FindType(CameraManagerType);
        if (t is not null && _cameraManager.Resolve(t))
        {
            _mainCamera ??= StellarInterop.FindPropertyUp(t, "MainCamera");
            if (_cameraManager.Get() is { } mgr && _mainCamera?.GetValue(mgr) is Camera cam && cam != null) return cam;
        }
        return Camera.main;
    }

    private void WarnResolveOnce()
    {
        if (_warnedResolve) return;
        _warnedResolve = true;
        _log.Warning(Tag + "free camera unavailable: the Cinemachine virtual camera type was not found on this client");
    }
}
