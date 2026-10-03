using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
namespace Stellar.Application.Hosting;

/// <summary>Per-plugin view of <see cref="SceneFreezeService"/>; unload drops the plugin's tokens and handlers.</summary>
internal sealed class PluginSceneFreeze : ISceneFreeze
{
    private readonly SceneFreezeService _inner;
    private readonly object _owner;
    private readonly HandlerList<Action<bool>> _changed = new();

    public PluginSceneFreeze(SceneFreezeService inner, object owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public IDisposable Freeze() => _inner.Freeze(_owner);
    public bool IsFrozen => _inner.IsFrozen;
    public bool HoldsPositions => _inner.HoldsPositions;

    public event Action<bool>? Changed
    {
        add => _changed.Add(value, h => _inner.Changed += h);
        remove => _changed.Remove(value, h => _inner.Changed -= h);
    }

    public void ReleaseAll()
    {
        _changed.Clear(h => _inner.Changed -= h);
        _inner.ReleaseOwner(_owner);
    }
}
