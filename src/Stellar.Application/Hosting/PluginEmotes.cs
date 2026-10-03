using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
namespace Stellar.Application.Hosting;

/// <summary>Per-plugin view of <see cref="IEmotes"/>; unload detaches the plugin's handlers.</summary>
internal sealed class PluginEmotes : IEmotes
{
    private readonly IEmotes _inner;
    private readonly HandlerList<Action> _changed = new();

    public PluginEmotes(IEmotes inner) => _inner = inner;

    public IReadOnlyList<EmoteInfo> Unlocked => _inner.Unlocked;
    public Task<EmoteResult> PlayAsync(int id) => _inner.PlayAsync(id);

    public event Action? UnlockedChanged
    {
        add => _changed.Add(value, h => _inner.UnlockedChanged += h);
        remove => _changed.Remove(value, h => _inner.UnlockedChanged -= h);
    }

    public void ReleaseAll() => _changed.Clear(h => _inner.UnlockedChanged -= h);
}
