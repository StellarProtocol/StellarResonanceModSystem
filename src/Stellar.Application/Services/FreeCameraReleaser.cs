using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Services;

/// <summary>The framework's half of the free-camera release path (spec § 7): camera first, then the freeze, then the
/// input shield — each step isolated so a throw in one (e.g. a plugin's <c>Released</c> handler) never skips the
/// next, and each warned once. A zone change keeps the shield handles (plugins release them on <c>Released</c>) and
/// only re-asserts the mask; every other reason releases them. Idempotent: with nothing held every step is a no-op
/// (no backend call, no <c>Released</c> event), so the scene-leave prefix and the <c>SceneChanged</c> backstop can
/// both call it. Main thread.</summary>
internal sealed class FreeCameraReleaser
{
    private readonly CameraOverrideService _camera;
    private readonly SceneFreezeService _freeze;
    private readonly InputShieldService _shield;
    private readonly Action<string> _warn;
    private bool _warnedCamera, _warnedFreeze, _warnedShield;

    public FreeCameraReleaser(CameraOverrideService camera, SceneFreezeService freeze, InputShieldService shield, Action<string> warn)
    {
        _camera = camera;
        _freeze = freeze;
        _shield = shield;
        _warn = warn;
    }

    public void Release(CameraReleaseReason reason)
    {
        try { _camera.ReleaseAll(reason); }
        catch (Exception ex) { WarnOnce(ref _warnedCamera, "camera release threw: " + ex.Message); }
        try { _freeze.ReleaseAll(); }
        catch (Exception ex) { WarnOnce(ref _warnedFreeze, "freeze release threw: " + ex.Message); }
        try
        {
            if (reason == CameraReleaseReason.SceneChanged) _shield.Reassert();
            else _shield.ReleaseAll();
        }
        catch (Exception ex) { WarnOnce(ref _warnedShield, "input shield release threw: " + ex.Message); }
    }

    private void WarnOnce(ref bool warned, string message)
    {
        if (warned) return;
        warned = true;
        _warn(message);
    }
}
