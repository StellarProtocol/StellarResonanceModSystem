using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
namespace Stellar.Application.Hosting;

/// <summary>
/// Per-plugin view of <see cref="SceneVisibilityService"/>. On plugin unload <see cref="ReleaseAll"/> releases the
/// plugin's hide tokens AND unsubscribes every <see cref="Changed"/> handler it added through this façade, so an
/// unloaded plugin is never called back.
/// </summary>
internal sealed class PluginSceneVisibility : ISceneVisibility
{
    private readonly SceneVisibilityService _inner;
    private readonly object _owner;
    private readonly List<Action<VisibilityLayers>> _handlers = new();

    public PluginSceneVisibility(SceneVisibilityService inner, object owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public IDisposable Hide(VisibilityLayers layers) => _inner.Hide(layers, _owner);
    public VisibilityLayers Hidden => _inner.Hidden;
    public VisibilityLayers Available => _inner.Available;
    public event Action<VisibilityLayers>? Changed
    {
        add
        {
            if (value is null) return;
            _handlers.Add(value);
            _inner.Changed += value;
        }
        remove
        {
            if (value is null || !_handlers.Remove(value)) return;
            _inner.Changed -= value;
        }
    }

    public void ReleaseAll()
    {
        foreach (var h in _handlers) _inner.Changed -= h;
        _handlers.Clear();
        _inner.ReleaseOwner(_owner);
    }
}
