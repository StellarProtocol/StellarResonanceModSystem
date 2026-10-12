using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>Reference-counted input shield (spec § 6). The first handle raises the game's ignore mask, the last
/// release drops it. While any handle is held, <see cref="KeyboardBlocked"/> asks the Host's keyboard gate to block
/// every game key (spec D8). Reads go straight to Unity input, so neither blocks them. The one owner of the game's
/// ignore-mask source (qa M-4, 2026-10-03): the scene freeze's pause block (<see cref="SetPauseBlock"/> — movement and
/// combat masked while the world is paused) is a second LAYER of the same mask, and every change re-applies the union of
/// both layers, so a free-camera release never clears the pause block and an unfreeze never clears the camera's mask.
/// Main thread.</summary>
internal sealed class InputShieldService : IInputShield
{
    private readonly IInputShieldBackend _backend;
    private readonly IShieldInputReader _reader;
    private readonly ITextFieldFocus _focus;
    private readonly Action<string> _warn;
    private readonly List<Handle> _handles = new();
    private bool _warned, _warnedPause;
    private bool _pauseBlock;

    public InputShieldService(IInputShieldBackend backend, IShieldInputReader reader, ITextFieldFocus focus, Action<string> warn)
    {
        _backend = backend;
        _reader = reader;
        _focus = focus;
        _warn = warn;
    }

    public bool IsShielded { get; private set; }

    public bool IsPointerOverGameUi => _reader.PointerOverGameUi;

    /// <summary>True while the pause block layer is requested (the world is paused).</summary>
    internal bool PauseBlocked => _pauseBlock;

    /// <summary>True while any handle is held — the Host ORs this into <c>KeyboardInputGate</c> (independent of the mask).</summary>
    internal bool KeyboardBlocked => _handles.Count > 0;

    /// <summary>Raised when <see cref="KeyboardBlocked"/> flips, so the Host applies the gate at once instead of on its
    /// next throttled tick (an Esc in that gap would otherwise open the game menu).</summary>
    internal event Action? KeyboardBlockChanged;

    public IInputShieldHandle Shield() => Shield(owner: null);

    internal IInputShieldHandle Shield(object? owner)
    {
        var h = new Handle(this, owner);
        _handles.Add(h);
        if (_handles.Count == 1) KeyboardBlockChanged?.Invoke();
        if (!IsShielded) Raise();
        return h;
    }

    /// <summary>Re-issues the mask after a zone load (the game may rebuild its ignore table). No-op when nothing is held.</summary>
    internal void Reassert()
    {
        if (_handles.Count == 0 && !_pauseBlock) return;
        _backend.Forget();
        if (_handles.Count > 0) Raise();
        else ApplyPause();
    }

    /// <summary>The scene freeze's pause block: the local player's movement and combat masked while the world is paused
    /// (owner report 2026-10-02: WASD while frozen played the run in place). Independent of the handles — a free-camera
    /// release keeps it, and dropping it keeps the camera's mask.</summary>
    internal void SetPauseBlock(bool on)
    {
        if (_pauseBlock == on) return;
        _pauseBlock = on;
        ApplyPause();
    }

    internal void ReleaseOwner(object owner)
    {
        foreach (var h in _handles.Where(h => Equals(h.Owner, owner)).ToList()) h.Dispose();
    }

    internal void ReleaseAll()
    {
        foreach (var h in _handles.ToList()) h.Dispose();
    }

    private void Raise()
    {
        if (_backend.Apply(camera: true, pause: _pauseBlock)) { IsShielded = true; return; }
        if (_warned) return;
        _warned = true;
        _warn("could not block the game's input; your character may move while the free camera is on");
    }

    private void ApplyPause()
    {
        if (_backend.Apply(camera: IsShielded, pause: _pauseBlock) || !_pauseBlock || _warnedPause) return;
        _warnedPause = true;
        _warn("could not block movement while the world is paused; your character may move in place");
    }

    private void Release(Handle h)
    {
        if (!_handles.Remove(h) || _handles.Count > 0) return;
        KeyboardBlockChanged?.Invoke();
        if (!IsShielded) return;
        IsShielded = false;
        _backend.Apply(camera: false, pause: _pauseBlock);
    }

    private sealed class Handle : IInputShieldHandle
    {
        private InputShieldService? _svc;

        public Handle(InputShieldService svc, object? owner) { _svc = svc; Owner = owner; }

        public object? Owner { get; }
        public bool IsActive => _svc is not null;
        public bool IsHeld(StellarKeyCode key) => _svc?._reader.IsHeld(key) ?? false;
        public ModifierKeys Modifiers => _svc?._reader.Modifiers ?? ModifierKeys.None;
        public bool IsMouseHeld(int button) => _svc?._reader.IsMouseHeld(button) ?? false;
        public (float X, float Y) MouseDelta => _svc?._reader.MouseDelta ?? (0f, 0f);
        public float Wheel => _svc?._reader.Wheel ?? 0f;
        public (float X, float Y) Pointer => _svc?._reader.Pointer ?? (0f, 0f);
        public bool TextFieldFocused => _svc?._focus.AnyFieldFocused ?? false;

        public void Dispose()
        {
            var svc = _svc;
            _svc = null;
            svc?.Release(this);
        }
    }
}
