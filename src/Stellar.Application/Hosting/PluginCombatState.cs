using System;
using Stellar.Abstractions.Services;
namespace Stellar.Application.Hosting;

/// <summary>Per-plugin view of <see cref="ICombatState"/>; unload detaches the plugin's handlers.</summary>
internal sealed class PluginCombatState : ICombatState
{
    private readonly ICombatState _inner;
    private readonly HandlerList<Action<bool>> _changed = new();

    public PluginCombatState(ICombatState inner) => _inner = inner;

    public bool LocalPlayerInCombat => _inner.LocalPlayerInCombat;

    public event Action<bool>? Changed
    {
        add => _changed.Add(value, h => _inner.Changed += h);
        remove => _changed.Remove(value, h => _inner.Changed -= h);
    }

    public void ReleaseAll() => _changed.Clear(h => _inner.Changed -= h);
}
