using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
namespace Stellar.Application.Services;

/// <summary>The framework's forced-release path (free-camera spec § 7, scene-stays spec § 2): camera first, then every
/// posed person, then the freeze, then the input shield — each step isolated so a throw in one (e.g. a plugin's
/// <c>Released</c> or <c>Changed</c> handler) never skips the next, and each warned once. Its reasons (zone change / leave
/// scene, cutscene, game photo mode, disconnect, framework unload) END THE SCENE: posing ends on exactly the reasons the
/// freeze ends on because this one call releases both. A plain free-camera release (the plugin's own exit, an error, the
/// leash) never comes through here and leaves the freeze and the poses alone. A zone change keeps the shield handles
/// (plugins release them on <c>Released</c>) and only re-asserts the mask; every other reason releases them. Idempotent:
/// with nothing held every step is a no-op (no backend call, no <c>Released</c>/<c>Changed</c> event), so the
/// scene-leave prefix and the <c>SceneChanged</c> backstop can both call it. Main thread.</summary>
internal sealed class FreeCameraReleaser
{
    private readonly CameraOverrideService _camera;
    private readonly IPosing _posing;
    private readonly SceneFreezeService _freeze;
    private readonly InputShieldService _shield;
    private readonly Action<string> _warn;
    private bool _warnedCamera, _warnedPosing, _warnedFreeze, _warnedShield;

    public FreeCameraReleaser(CameraOverrideService camera, IPosing posing, SceneFreezeService freeze, InputShieldService shield,
        Action<string> warn)
    {
        _camera = camera;
        _posing = posing;
        _freeze = freeze;
        _shield = shield;
        _warn = warn;
    }

    public void Release(CameraReleaseReason reason)
    {
        try { _camera.ReleaseAll(reason); }
        catch (Exception ex) { WarnOnce(ref _warnedCamera, "camera release threw: " + ex.Message); }
        try { _posing.ResetAll(); }
        catch (Exception ex) { WarnOnce(ref _warnedPosing, "posing reset threw: " + ex.Message); }
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
