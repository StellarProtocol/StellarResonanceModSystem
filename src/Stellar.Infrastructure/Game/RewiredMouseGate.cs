using System;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Hides the mouse from the game by disabling Rewired's mouse controller (<c>ReInput.controllers.Mouse.enabled=false</c>),
/// the mouse twin of <see cref="KeyboardInputGate"/>'s primary path. The game reads every action (ComAttack, LockTarget,
/// the mouse axes and wheel) through Rewired, while the framework and uGUI read <c>UnityEngine.Input</c>, so Stellar keeps
/// seeing clicks while the game does not. Used by the sticky cursor to swallow the world click that releases it.
/// <para>Transition-guarded (<see cref="SetBlocked"/> touches Rewired only on a change); resolution is lazy and retried on
/// the next change if it failed. Any failure logs and leaves the mouse alone. Never leaves the mouse disabled:
/// <see cref="Dispose"/> and the process-exit / unhandled-exception hooks re-enable it.</para>
/// </summary>
internal sealed class RewiredMouseGate : IDisposable
{
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

    private readonly IGameTypeRegistry _types;
    private readonly IPluginLog _log;
    private PropertyInfo? _controllersProp;   // static ReInput.controllers
    private PropertyInfo? _mouseProp;         // ControllerHelper.Mouse
    private PropertyInfo? _enabledProp;       // Controller.enabled
    private bool _blocked;
    private bool _hooked;

    public RewiredMouseGate(IGameTypeRegistry types, IPluginLog log)
    {
        _types = types;
        _log = log;
    }

    /// <summary>Disable (on) / re-enable (off) the game's mouse. Idempotent.</summary>
    public void SetBlocked(bool on)
    {
        if (on == _blocked) return;
        if (!Resolve()) return;
        if (!TrySetEnabled(!on)) return;
        _blocked = on;
        if (on) Hook();
    }

    public void Dispose()
    {
        Unhook();
        if (_blocked) SetBlocked(false);
    }

    private bool TrySetEnabled(bool enabled)
    {
        try
        {
            // Re-read the helper + controller each time (as KeyboardInputGate does): Rewired may rebuild them.
            var helper = _controllersProp!.GetValue(null);
            var mouse = helper is null ? null : _mouseProp!.GetValue(helper);
            if (mouse is null) { _log.Warning("[MouseGate] Rewired mouse controller is null"); return false; }
            _enabledProp!.SetValue(mouse, enabled);
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning($"[MouseGate] Rewired mouse set threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private bool Resolve()
    {
        if (_enabledProp is not null) return true;
        try
        {
            var reInput = _types.FindType("Rewired.ReInput");
            _controllersProp = reInput?.GetProperty("controllers", AnyStatic);
            var helper = _controllersProp?.GetValue(null);
            _mouseProp = helper?.GetType().GetProperty("Mouse", AnyInstance);
            var mouse = helper is null ? null : _mouseProp?.GetValue(helper);
            var enabled = mouse?.GetType().GetProperty("enabled", AnyInstance);
            if (enabled is null || !enabled.CanWrite)
            {
                _log.Warning($"[MouseGate] Rewired mouse not resolved (ReInput={reInput is not null} helper={helper is not null} mouse={mouse is not null}) — the world click is NOT swallowed");
                return false;
            }
            _enabledProp = enabled;
            _log.Info("[MouseGate] Rewired mouse controller resolved");
            return true;
        }
        catch (Exception ex)
        {
            _log.Warning($"[MouseGate] resolve threw: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    // ---- crash/exit hardening, mirroring KeyboardInputGate: never leave the game's mouse disabled ----

    private void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        AppDomain.CurrentDomain.ProcessExit += OnTeardown;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
    }

    private void Unhook()
    {
        if (!_hooked) return;
        _hooked = false;
        AppDomain.CurrentDomain.ProcessExit -= OnTeardown;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
    }

    private void OnTeardown(object? sender, EventArgs e) => ForceRestore();
    private void OnUnhandledException(object? sender, UnhandledExceptionEventArgs e) => ForceRestore();

    // Best-effort re-enable from a non-main thread: only ever re-enables, so a race costs at most a redundant call.
    private void ForceRestore()
    {
        try { if (_blocked) SetBlocked(false); }
        catch { /* teardown path, nothing to recover to */ }
    }
}
