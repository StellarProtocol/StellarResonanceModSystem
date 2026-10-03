using System;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
namespace Stellar.Application.Hosting;

/// <summary>A plugin's view of <see cref="PosingService"/>: its people are owned by its key, so another plugin cannot
/// take them, its <see cref="ResetAll"/> resets only them, and the unload (<see cref="ReleaseAll"/>) resets them and
/// drops its <see cref="Changed"/> handlers (spec § 5 unload backstop).</summary>
internal sealed class PluginPosing : IPosing
{
    private readonly PosingService _inner;
    private readonly object _owner;
    private readonly HandlerList<Action> _changed = new();

    public PluginPosing(PosingService inner, object owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public bool IsAvailable => _inner.IsAvailable;
    public IReadOnlyList<PersonInfo> NearbyPeople(float radius) => _inner.NearbyPeople(radius);
    public IReadOnlyList<ExpressionInfo> Expressions => _inner.Expressions;
    public IPoseTarget? Select(EntityId person) => _inner.Select(person, _owner);
    public bool TryGetVisiblePosition(EntityId person, out Position3D position) => _inner.TryGetVisiblePosition(person, out position);
    public bool TryGetCurrentAction(EntityId person, out int actionId, out float moment) =>
        _inner.TryGetCurrentAction(person, out actionId, out moment);
    public void ResetAll() => _inner.ReleaseOwner(_owner);

    public event Action? Changed
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
