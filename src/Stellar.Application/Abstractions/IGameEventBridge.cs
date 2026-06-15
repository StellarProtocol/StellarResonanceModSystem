using System;

namespace Stellar.Application.Abstractions;

/// <summary>
/// Outbound interface — a single backend for delivering game events to subscribers.
/// Implementations: MessagePipe-via-VContainer, HarmonyX-postfix, etc.
/// </summary>
/// <remarks>
/// <see cref="GameEventsService"/> composes multiple bridges and prefers the first that
/// successfully attaches a subscription. Bridges may return <c>null</c> from
/// <see cref="TrySubscribe"/> to signal "can't deliver this event type" — the service then
/// tries the next bridge.
/// </remarks>
internal interface IGameEventBridge
{
    /// <summary>
    /// Attempt to subscribe to a game event by its fully-qualified type name.
    /// Returns a disposable unsubscribe token on success, or <c>null</c> if this bridge
    /// cannot deliver the event.
    /// </summary>
    IDisposable? TrySubscribe(string fullTypeName, Action<object?> handler);
}
