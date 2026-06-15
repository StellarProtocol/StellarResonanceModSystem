using System;
using System.Collections.Generic;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Events;

/// <summary>
/// Fallback event bridge: events are delivered via HarmonyX postfix patches on known game
/// lifecycle methods (see <c>BootstrapPlugin</c> wire-up). The bridge publishes events
/// internally via <see cref="Publish"/> when those patches fire.
/// </summary>
internal sealed class HarmonyEventBridge : IGameEventBridge
{
    private readonly Dictionary<string, List<Action<object?>>> _handlers = new(StringComparer.Ordinal);

    /// <summary>Called by the host's HarmonyX postfixes for each lifecycle hit.</summary>
    public void Publish(string fullTypeName, object? payload)
    {
        if (!_handlers.TryGetValue(fullTypeName, out var list))
        {
            return;
        }

        // Iterate a copy so a handler can unsubscribe mid-dispatch.
        foreach (var handler in list.ToArray())
        {
            try
            {
                handler(payload);
            }
            catch
            {
                // Handler-level exceptions are not the bridge's concern; suppress to keep
                // the dispatch loop going. Service-level error reporting can be added later.
            }
        }
    }

    public IDisposable? TrySubscribe(string fullTypeName, Action<object?> handler)
    {
        if (!_handlers.TryGetValue(fullTypeName, out var list))
        {
            _handlers[fullTypeName] = list = new List<Action<object?>>();
        }
        list.Add(handler);
        return new Token(this, fullTypeName, handler);
    }

    private sealed class Token : IDisposable
    {
        private readonly HarmonyEventBridge _owner;
        private readonly string _typeName;
        private readonly Action<object?> _handler;
        private bool _disposed;

        public Token(HarmonyEventBridge owner, string typeName, Action<object?> handler)
        {
            _owner = owner;
            _typeName = typeName;
            _handler = handler;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            if (_owner._handlers.TryGetValue(_typeName, out var list))
            {
                list.Remove(_handler);
            }
        }
    }
}
