using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>
/// In-process <see cref="IPluginExchange"/> — a type-keyed registry of provider objects. Constructed once by
/// Host and shared to every plugin via <c>IPluginServices.Exchange</c>. No Unity dependency. Provide/Consume
/// run on the game thread (plugin construction + UI callbacks), so a plain dictionary suffices.
/// </summary>
public sealed class PluginExchange : IPluginExchange
{
    private readonly Dictionary<Type, object> _providers = new();

    public void Provide<T>(T implementation) where T : class
        => _providers[typeof(T)] = implementation;

    public T? Consume<T>() where T : class
        => _providers.TryGetValue(typeof(T), out var impl) ? (T)impl : null;
}
