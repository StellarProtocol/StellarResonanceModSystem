using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
namespace Stellar.Application.Hosting;

/// <summary>Per-plugin view of <see cref="SceneVisibilityService"/>; tokens are released when the plugin unloads.</summary>
internal sealed class PluginSceneVisibility : ISceneVisibility
{
    private readonly SceneVisibilityService _inner;
    private readonly object _owner;

    public PluginSceneVisibility(SceneVisibilityService inner, object owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public IDisposable Hide(VisibilityLayers layers) => _inner.Hide(layers, _owner);
    public VisibilityLayers Hidden => _inner.Hidden;
    public event Action<VisibilityLayers>? Changed
    {
        add => _inner.Changed += value;
        remove => _inner.Changed -= value;
    }
    public void ReleaseAll() => _inner.ReleaseOwner(_owner);
}
