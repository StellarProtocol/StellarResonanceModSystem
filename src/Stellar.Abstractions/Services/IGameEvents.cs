using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Subscribe to typed game events by their fully-qualified Panda type name.
/// Backed by MessagePipe's <c>ISubscriber&lt;T&gt;</c> when the host can reach the
/// game's container; otherwise backed by hook fallbacks. Plugins see the same API either way.
/// </summary>
public interface IGameEvents
{
    /// <summary>
    /// Subscribe to a game event by name. The returned token unsubscribes when disposed.
    /// The handler receives the raw event payload (or <c>null</c> if the event has no message body).
    /// </summary>
    IDisposable Subscribe(string fullTypeName, Action<object?> handler);
}
