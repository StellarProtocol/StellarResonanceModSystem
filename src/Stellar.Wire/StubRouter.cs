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

    /// <summary>
    /// Subscribes <paramref name="handler"/> to <paramref name="methodId"/>. Several probes may
    /// subscribe to the SAME id — they are invoked in REGISTRATION order on each delivery, which is
    /// load-bearing: <c>PandaCombatStubProbe</c> latches the dungeon run id from WorldNtf method 3
    /// before <c>PandaWorldAttrProbe</c> reads the same packet's scene attrs, and that probe's
    /// run-id gate needs the new id already latched. Registering the same delegate instance twice
    /// subscribes it twice, so call this exactly once per probe (Host wiring does).
    ///
    /// <para>Handlers must not throw: they share one multicast invocation, so an exception in an
    /// earlier handler skips the later ones (and unwinds into the dispatcher's guard). That was
    /// already the contract when each id had a single handler.</para>
    /// </summary>
    public void Register(uint methodId, Action<uint, byte[]> handler)
    {
        _handlers[methodId] = _handlers.TryGetValue(methodId, out var existing)
            ? (Action<uint, byte[]>)Delegate.Combine(existing, handler)
            : handler;
    }

    /// <summary>Returns <see langword="true"/> when a handler is registered for
    /// <paramref name="methodId"/>.</summary>
    public bool Subscribes(uint methodId) => _handlers.ContainsKey(methodId);

    /// <summary>Invokes every handler registered for <paramref name="methodId"/>, in registration
    /// order, or does nothing if none is registered.</summary>
    public void Route(uint methodId, byte[] payload)
    {
        if (_handlers.TryGetValue(methodId, out var h))
            h(methodId, payload);
    }
}
