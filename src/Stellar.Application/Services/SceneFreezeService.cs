using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>Reference-counted scene freeze (spec § 4). The framework calls <see cref="ReleaseAll"/> on a zone change,
/// a cutscene and a disconnect. <c>STELLAR_FREEZE_NO_POSITIONS=1</c> arrives as <c>positionsDisabled</c>. Main thread.</summary>
internal sealed class SceneFreezeService : ISceneFreeze
{
    private readonly ISceneFreezeBackend _backend;
    private readonly bool _positionsDisabled;
    private readonly List<Token> _tokens = new();

    public SceneFreezeService(ISceneFreezeBackend backend, bool positionsDisabled)
    {
        _backend = backend;
        _positionsDisabled = positionsDisabled;
        _backend.HoldDisabled += () => { if (IsFrozen) Changed?.Invoke(true); };
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
        Changed?.Invoke(true);
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
        Changed?.Invoke(false);
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
