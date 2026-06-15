using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Application.Services;

/// <summary>
/// Tries each <see cref="IGameEventBridge"/> in order; the first to return a non-null
/// subscription token wins. Subscriptions arriving before bridges are ready are buffered
/// and re-attempted via <see cref="AttachBridge"/>.
/// </summary>
internal sealed class GameEventsService : IGameEvents
{
    private readonly IPluginLog _log;
    private readonly List<IGameEventBridge> _bridges = new();
    private readonly Dictionary<Action<object?>, BufferedSubscription> _pending = new();

    public GameEventsService(IPluginLog log) => _log = log;

    /// <summary>Registers a bridge and replays buffered subscriptions through it.</summary>
    internal void AttachBridge(IGameEventBridge bridge)
    {
        _bridges.Add(bridge);

        foreach (var sub in _pending.Values)
        {
            if (sub.IsAttached)
            {
                continue;
            }
            sub.TryAttachVia(bridge);
        }
    }

    public IDisposable Subscribe(string fullTypeName, Action<object?> handler)
    {
        var sub = new BufferedSubscription(this, fullTypeName, handler);
        _pending[handler] = sub;

        foreach (var bridge in _bridges)
        {
            sub.TryAttachVia(bridge);
            if (sub.IsAttached)
            {
                break;
            }
        }

        return sub;
    }

    private sealed class BufferedSubscription : IDisposable
    {
        private readonly GameEventsService _owner;
        private readonly string _typeName;
        private readonly Action<object?> _handler;
        private IDisposable? _attached;

        public bool IsAttached => _attached is not null;

        public BufferedSubscription(GameEventsService owner, string typeName, Action<object?> handler)
        {
            _owner = owner;
            _typeName = typeName;
            _handler = handler;
        }

        public void TryAttachVia(IGameEventBridge bridge)
        {
            if (_attached is not null)
            {
                return;
            }
            try
            {
                var token = bridge.TrySubscribe(_typeName, _handler);
                if (token is not null)
                {
                    _attached = token;
                    _owner._log.Debug($"[GameEvents] {_typeName} attached via {bridge.GetType().Name}");
                }
            }
            catch (Exception ex)
            {
                _owner._log.Warning($"[GameEvents] {bridge.GetType().Name}.TrySubscribe({_typeName}) threw: {ex.Message}");
            }
        }

        public void Dispose()
        {
            try
            {
                _attached?.Dispose();
            }
            catch
            {
                // intentionally swallowed — disposal must not throw
            }
            _attached = null;
            _owner._pending.Remove(_handler);
        }
    }
}
