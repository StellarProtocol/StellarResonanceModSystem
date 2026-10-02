using System;
using System.Reflection;
using Stellar.Abstractions.Services;
namespace Stellar.Infrastructure.Rendering;

/// <summary>Cut blends while the game's clock is paused (the scene freeze's time pause; devkit recon
/// <c>free-camera-recon.md</c> § Run 9 R9-4): the brain blends on SCALED time, so at <c>Time.timeScale = 0</c> a free-camera
/// acquire or release through the game's default blend never advances (measured 0.000 m toward the new pose; neither
/// <c>m_IgnoreTimeScale</c> nor <c>CinemachineCore.UniformDeltaTimeOverride</c> moves it). With
/// <c>CinemachineBrain.m_DefaultBlend.m_Style = Cut</c> the re-acquire followed at once (1.000 m). So the brain's default
/// blend is Cut for the whole pause and put back as it was when the clock runs again. A blend already running when the
/// pause starts stays where it is until the resume. Fails soft (one warning): the camera then blends as the game does.</summary>
internal sealed partial class CinemachineCameraBackend
{
    private PropertyInfo? _brain, _defaultBlend, _blendStyle;
    private object? _savedBlend;
    private object? _cutBrain;
    private bool _warnedBlend;

    /// <summary>The game's clock stopped (true: default blend → Cut, the game's kept) or runs again (false: put back).</summary>
    public void SetCutBlend(bool paused)
    {
        try
        {
            if (paused) CutOn();
            else CutOff();
        }
        catch (Exception ex)
        {
            _savedBlend = _cutBrain = null;
            if (_warnedBlend) return;
            _warnedBlend = true;
            _log.Warning(Tag + "could not switch the camera blend for the time pause: " + (ex.InnerException ?? ex).Message);
        }
    }

    private void CutOn()
    {
        if (_savedBlend is not null || Brain() is not { } brain || !ResolveBlend(brain)) return;
        var saved = _defaultBlend!.GetValue(brain);   // a boxed copy of the struct: kept as is for the restore
        var cut = _defaultBlend.GetValue(brain);      // a second copy, edited and written back
        if (saved is null || cut is null) return;
        _blendStyle!.SetValue(cut, Enum.Parse(_blendStyle.PropertyType, "Cut"));
        _defaultBlend.SetValue(brain, cut);
        _savedBlend = saved;
        _cutBrain = brain;
    }

    private void CutOff()
    {
        var saved = _savedBlend;
        var brain = _cutBrain;
        _savedBlend = _cutBrain = null;
        if (saved is null || brain is null) return;
        if (!SameObject(Brain(), brain)) return;   // a new brain since: it has its own blend
        _defaultBlend!.SetValue(brain, saved);
    }

    /// <summary><c>CameraManager.Instance.Brain</c>, or null.</summary>
    private object? Brain()
    {
        var t = _types.FindType(CameraManagerType);
        if (t is null || !_cameraManager.Resolve(t) || _cameraManager.Get() is not { } mgr) return null;
        _brain ??= StellarInterop.FindPropertyUp(t, "Brain");
        return _brain?.GetValue(mgr);
    }

    private bool ResolveBlend(object brain)
    {
        if (_blendStyle is not null) return true;
        _defaultBlend = StellarInterop.FindPropertyUp(brain.GetType(), "m_DefaultBlend");
        _blendStyle = _defaultBlend is null ? null : StellarInterop.FindPropertyUp(_defaultBlend.PropertyType, "m_Style");
        return _blendStyle is not null;
    }

    private static bool SameObject(object? a, object? b) =>
        a is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase x && b is Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase y && x.Pointer == y.Pointer;
}
