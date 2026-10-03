using System;
using System.Collections.Generic;
using System.Linq;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

internal sealed class SceneVisibilityService : ISceneVisibility
{
    private readonly IVisibilityBackend _backend;
    private readonly List<Token> _tokens = new();
    private VisibilityLayers _requested;

    public SceneVisibilityService(IVisibilityBackend backend) => _backend = backend;

    public VisibilityLayers Hidden { get; private set; }
    public VisibilityLayers Available => _backend.Available;
    public event Action<VisibilityLayers>? Changed;

    public IDisposable Hide(VisibilityLayers layers) => Hide(layers, owner: null);

    internal IDisposable Hide(VisibilityLayers layers, object? owner)
    {
        var t = new Token(this, layers, owner);
        _tokens.Add(t);
        Recompute();
        return t;
    }

    internal void ReleaseOwner(object owner)
    {
        foreach (var t in _tokens.Where(t => Equals(t.Owner, owner)).ToList()) t.Dispose();
    }

    private void Release(Token t)
    {
        if (_tokens.Remove(t)) Recompute();
    }

    private void Recompute()
    {
        var union = VisibilityLayers.None;
        var keepParty = true;
        foreach (var t in _tokens)
        {
            union |= t.Layers & ~VisibilityLayers.KeepParty;
            if ((t.Layers & VisibilityLayers.OtherPlayers) != 0 && (t.Layers & VisibilityLayers.KeepParty) == 0)
                keepParty = false;
        }
        if ((union & VisibilityLayers.OtherPlayers) != 0 && keepParty) union |= VisibilityLayers.KeepParty;
        if (union == _requested) return;
        _requested = union;
        Publish(_backend.Apply(union));
    }

    /// <summary>Re-issues the held set (the game's photo mode ended, or a hide target was rebuilt). No-op when
    /// nothing is held AND the backend reports no pending restore (M2: a release Apply couldn't complete earlier —
    /// e.g. an effect manager briefly unavailable — would otherwise never be retried, since nothing looks "held").</summary>
    internal void Reassert()
    {
        if (_requested == VisibilityLayers.None && !_backend.HasPendingRestore) return;
        Publish(_backend.Reassert(_requested));
    }

    private void Publish(VisibilityLayers achieved)
    {
        // KeepParty only means anything alongside an actually-achieved OtherPlayers hide.
        if ((achieved & VisibilityLayers.OtherPlayers) == 0) achieved &= ~VisibilityLayers.KeepParty;
        if (achieved == Hidden) return;
        Hidden = achieved;
        Changed?.Invoke(achieved);
    }

    private sealed class Token : IDisposable
    {
        private SceneVisibilityService? _owner;
        public Token(SceneVisibilityService svc, VisibilityLayers layers, object? owner)
        {
            _owner = svc;
            Layers = layers;
            Owner = owner;
        }
        public VisibilityLayers Layers { get; }
        public object? Owner { get; }
        public void Dispose()
        {
            var svc = _owner;
            _owner = null;
            svc?.Release(this);
        }
    }
}
