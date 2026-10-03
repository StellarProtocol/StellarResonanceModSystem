using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
namespace Stellar.Application.Hosting;

/// <summary>A plugin's view of <see cref="LightsService"/>: its lamps, its people level and the people it lights are owned
/// by its key (another plugin cannot change or remove them; the lamp cap counts per plugin), its <see cref="ResetAll"/>
/// ends only them, and the unload (<see cref="ReleaseAll"/>) ends them and drops its <see cref="Released"/> handlers.</summary>
internal sealed class PluginLights : ILights
{
    private readonly LightsService _inner;
    private readonly object _owner;
    private readonly HandlerList<Action> _released = new();

    public PluginLights(LightsService inner, object owner)
    {
        _inner = inner;
        _owner = owner;
    }

    public bool IsAvailable => _inner.IsAvailable;
    public LampId AddLamp(LampSettings settings) => _inner.AddLamp(_owner, settings);
    public bool UpdateLamp(LampId lamp, LampSettings settings) => _inner.UpdateLamp(_owner, lamp, settings);
    public void RemoveLamp(LampId lamp) => _inner.RemoveLamp(_owner, lamp);
    public float PeopleLevel { get => _inner.GetLevel(_owner); set => _inner.SetLevel(_owner, value); }
    public bool SetPersonLight(EntityId person, PersonLight light) => _inner.SetPersonLight(_owner, person, light);
    public void ResetAll() => _inner.ReleaseOwner(_owner);

    public event Action? Released
    {
        add => _released.Add(value, h => _inner.Released += h);
        remove => _released.Remove(value, h => _inner.Released -= h);
    }

    public void ReleaseAll()
    {
        _released.Clear(h => _inner.Released -= h);
        _inner.ReleaseOwner(_owner);
    }
}
