using System;
using Il2CppInterop.Runtime.Injection;
using Stellar.Abstractions.Services;
using UnityEngine;
namespace Stellar.Infrastructure.Unity;

/// <summary>Owns the single <see cref="FreeCameraFrameDriver"/>: created on first use, enabled only while a frame or
/// late-frame handler is set. Main thread.</summary>
internal sealed class FrameDriverHost : IDisposable
{
    private readonly IPluginLog _log;
    private FreeCameraFrameDriver? _driver;
    private bool _registered;
    private Action<float>? _frame;
    private Action? _late;

    public FrameDriverHost(IPluginLog log) => _log = log;

    public void SetFrame(Action<float>? handler) { _frame = handler; Sync(); }

    public void SetLate(Action? handler) { _late = handler; Sync(); }

    public void Dispose()
    {
        _frame = null;
        _late = null;
        if (_driver != null) UnityEngine.Object.Destroy(_driver.gameObject);
        _driver = null;
    }

    private void Sync()
    {
        var wanted = _frame is not null || _late is not null;
        if (!wanted && _driver == null) return;
        var d = Ensure();
        if (d == null) return;
        d.OnFrame = _frame;
        d.OnLateFrame = _late;
        d.enabled = wanted;
    }

    private FreeCameraFrameDriver? Ensure()
    {
        if (_driver != null) return _driver;
        if (!_registered)
        {
            Il2CppClassInitFix.EnsureSeeded(_log);
            try { ClassInjector.RegisterTypeInIl2Cpp<FreeCameraFrameDriver>(); }
            catch (Exception ex) { _log.Debug($"[FreeCam] RegisterTypeInIl2Cpp(FreeCameraFrameDriver): {ex.Message}"); }
            _registered = true;
        }
        var go = new GameObject("StellarFreeCameraDriver") { hideFlags = HideFlags.HideAndDontSave };
        UnityEngine.Object.DontDestroyOnLoad(go);
        _driver = go.AddComponent<FreeCameraFrameDriver>();
        return _driver;
    }
}
