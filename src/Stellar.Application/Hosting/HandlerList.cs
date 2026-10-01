using System;
using System.Collections.Generic;
namespace Stellar.Application.Hosting;

/// <summary>Remembers the event handlers a plugin added through a facade so unload can detach every one of them.</summary>
internal sealed class HandlerList<T> where T : Delegate
{
    private readonly List<T> _items = new();

    public void Add(T? handler, Action<T> subscribe)
    {
        if (handler is null) return;
        _items.Add(handler);
        subscribe(handler);
    }

    public void Remove(T? handler, Action<T> unsubscribe)
    {
        if (handler is not null && _items.Remove(handler)) unsubscribe(handler);
    }

    public void Clear(Action<T> unsubscribe)
    {
        foreach (var h in _items) unsubscribe(h);
        _items.Clear();
    }
}
