using System;
using System.Collections.Generic;

namespace Stellar.Wire;

/// <summary>
/// Pure methodId-keyed handler registry for a stub dispatcher (BCL-only,
/// unit-testable). The IL2CPP header read lives in the Infrastructure dispatcher.
/// </summary>
public sealed class StubRouter
{
    private readonly Dictionary<uint, Action<uint, byte[]>> _handlers = new();

    /// <summary>Registers <paramref name="handler"/> for the given <paramref name="methodId"/>,
    /// replacing any prior registration for that id.</summary>
    public void Register(uint methodId, Action<uint, byte[]> handler) =>
        _handlers[methodId] = handler;

    /// <summary>Returns <see langword="true"/> when a handler is registered for
    /// <paramref name="methodId"/>.</summary>
    public bool Subscribes(uint methodId) => _handlers.ContainsKey(methodId);

    /// <summary>Invokes the handler registered for <paramref name="methodId"/>,
    /// or does nothing if no handler is registered.</summary>
    public void Route(uint methodId, byte[] payload)
    {
        if (_handlers.TryGetValue(methodId, out var h))
            h(methodId, payload);
    }
}
