using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>Reference-counted scene freeze — a global time pause (spec § 4; amendment 2026-10-02 late). The framework calls
/// <see cref="ReleaseAll"/> on a zone change, a cutscene, a disconnect and when its watchdog finds the pause outside the
/// world, stalled or without its driver. <c>STELLAR_FREEZE_NO_POSITIONS=1</c> arrives as <c>positionsDisabled</c>.
/// <see cref="Changed"/> runs every handler on its own (qa I-2, 2026-10-03): a throwing handler is logged once and never
/// stops the others, never keeps <see cref="Freeze"/> from handing back its token (a leaked token would leave the game
/// paused, movement and combat masked, until the next zone change) and never stops an unfreeze. Main thread.</summary>
internal sealed class SceneFreezeService : ISceneFreeze
{
    private readonly ISceneFreezeBackend _backend;
    private readonly bool _positionsDisabled;
    private readonly Action<string>? _warn;
    private readonly List<Token> _tokens = new();
    private bool _warnedHandler;

    public SceneFreezeService(ISceneFreezeBackend backend, bool positionsDisabled, Action<string>? warn = null)
    {
        _backend = backend;
        _positionsDisabled = positionsDisabled;
        _warn = warn;
        _backend.HoldDisabled += () => { if (IsFrozen) RaiseChanged(true); };
    }

    public bool IsFrozen { get; private set; }
    public bool HoldsPositions => IsFrozen && _backend.HoldsPositions;
    public event Action<bool>? Changed;

    public IDisposable Freeze() => Freeze(owner: null);

    internal IDisposable Freeze(object? owner)
    {
        var t = new Token(this, owner);
        _tokens.Add(t);
        if (IsFrozen) return t;
        _backend.EnsureHooks();
        _backend.FreezeAll(holdPositions: !_positionsDisabled);
        IsFrozen = true;
        RaiseChanged(true);
        return t;
    }

    internal void ReleaseAll()
    {
        foreach (var t in _tokens.ToList()) t.Dispose();
    }

    internal void ReleaseOwner(object owner)
    {
        foreach (var t in _tokens.Where(t => Equals(t.Owner, owner)).ToList()) t.Dispose();
    }

    private void Release(Token t)
    {
        if (!_tokens.Remove(t) || _tokens.Count > 0 || !IsFrozen) return;
        IsFrozen = false;
        _backend.UnfreezeAll();
        RaiseChanged(false);
    }

    // Each handler on its own (rare: a freeze press, an unfreeze, a hold turned off — the invocation list is no hot path).
    private void RaiseChanged(bool frozen)
    {
        if (Changed is not { } changed) return;
        foreach (var d in changed.GetInvocationList())
        {
            try { ((Action<bool>)d)(frozen); }
            catch (Exception ex)
            {
                if (_warnedHandler) continue;
                _warnedHandler = true;
                _warn?.Invoke("a scene-freeze listener threw (ignored): " + ex.Message);
            }
        }
    }

    private sealed class Token : IDisposable
    {
        private SceneFreezeService? _svc;
        public Token(SceneFreezeService svc, object? owner) { _svc = svc; Owner = owner; }
        public object? Owner { get; }
        public void Dispose()
        {
            var svc = _svc;
            _svc = null;
            svc?.Release(this);
        }
    }
}
