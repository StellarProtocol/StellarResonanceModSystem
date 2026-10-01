using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>Reference-counted input shield (spec § 6). The first handle raises the game's ignore mask, the last
/// release drops it. While any handle is held, <see cref="KeyboardBlocked"/> asks the Host's keyboard gate to block
/// every game key (spec D8). Reads go straight to Unity input, so neither blocks them. Main thread.</summary>
internal sealed class InputShieldService : IInputShield
{
    private readonly IInputShieldBackend _backend;
    private readonly IShieldInputReader _reader;
    private readonly Action<string> _warn;
    private readonly List<Handle> _handles = new();
    private bool _warned;

    public InputShieldService(IInputShieldBackend backend, IShieldInputReader reader, Action<string> warn)
    {
        _backend = backend;
        _reader = reader;
        _warn = warn;
    }

    public bool IsShielded { get; private set; }

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
        if (_handles.Count > 0) Raise();
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
        if (_backend.SetShield(true)) { IsShielded = true; return; }
        if (_warned) return;
        _warned = true;
        _warn("could not block the game's input; your character may move while the free camera is on");
    }

    private void Release(Handle h)
    {
        if (!_handles.Remove(h) || _handles.Count > 0) return;
        KeyboardBlockChanged?.Invoke();
        if (!IsShielded) return;
        IsShielded = false;
        _backend.SetShield(false);
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

        public void Dispose()
        {
            var svc = _svc;
            _svc = null;
            svc?.Release(this);
        }
    }
}
