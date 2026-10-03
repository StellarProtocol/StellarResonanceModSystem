using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>Reference-counted look-at-camera: the first handle applies, the last release restores the snapshot.</summary>
internal sealed class LookAtService
{
    private readonly ILookAtBackend _backend;
    private readonly Action<string> _warn;
    private readonly List<Token> _tokens = new();
    private bool _applied;

    public LookAtService(ILookAtBackend backend, Action<string> warn)
    {
        _backend = backend;
        _warn = warn;
    }

    public IDisposable Acquire(object? owner)
    {
        var t = new Token(this, owner);
        _tokens.Add(t);
        if (!_applied) _applied = _backend.TryApply();
        return t;
    }

    public void ReleaseOwner(object owner)
    {
        foreach (var t in _tokens.Where(t => Equals(t.Owner, owner)).ToList()) t.Dispose();
    }

    public void ReleaseAll()
    {
        foreach (var t in _tokens.ToList()) t.Dispose();
    }

    private void Release(Token t)
    {
        if (!_tokens.Remove(t) || _tokens.Count > 0 || !_applied) return;
        _applied = false;
        try { _backend.Restore(); }
        catch (Exception ex) { _warn("look-at restore threw: " + ex.Message); }
    }

    private sealed class Token : IDisposable
    {
        private LookAtService? _svc;
        public Token(LookAtService svc, object? owner) { _svc = svc; Owner = owner; }
        public object? Owner { get; }
        public void Dispose()
        {
            var svc = _svc;
            _svc = null;
            svc?.Release(this);
        }
    }
}
