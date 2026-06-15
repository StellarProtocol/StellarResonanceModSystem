using System;
using System.Linq;
using System.Reflection;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Events;

/// <summary>
/// Preferred event bridge: resolves <c>MessagePipe.ISubscriber&lt;T&gt;</c> from the game's
/// VContainer <c>IObjectResolver</c> and calls its <c>Subscribe(Action&lt;T&gt;)</c> extension.
/// Returns <c>null</c> from <see cref="TrySubscribe"/> when the container or the event type
/// is unavailable — falls through to <see cref="HarmonyEventBridge"/> in that case.
/// </summary>
internal sealed class MessagePipeContainerBridge : IGameEventBridge
{
    private readonly IPluginLog _log;
    private readonly IGameTypeRegistry _types;
    private object? _resolver;

    public MessagePipeContainerBridge(IPluginLog log, IGameTypeRegistry types)
    {
        _log = log;
        _types = types;
    }

    /// <summary>
    /// Called when the framework has located something that quacks like a VContainer resolver.
    /// Subsequent <see cref="TrySubscribe"/> calls will attempt the container path.
    /// </summary>
    public void AttachResolver(object? resolver)
    {
        _resolver = resolver;
        if (_resolver is null)
        {
            _log.Warning("[MessagePipe] no resolver attached; container path disabled");
            return;
        }
        _log.Info($"[MessagePipe] resolver attached: {_resolver.GetType().FullName}");
    }

    public IDisposable? TrySubscribe(string fullTypeName, Action<object?> handler)
    {
        if (_resolver is null)
        {
            return null;
        }

        var eventType = _types.FindType(fullTypeName);
        if (eventType is null)
        {
            return null;
        }

        var subscriber = ResolveSubscriberFromContainer(eventType);
        if (subscriber is null)
        {
            return null;
        }

        var subscribeAction = FindSubscribeExtensionMethod(eventType);
        if (subscribeAction is null)
        {
            return null;
        }

        var forwarder = CreateForwarder(eventType, handler);
        var closedSubscribe = subscribeAction.MakeGenericMethod(eventType);
        var token = closedSubscribe.Invoke(null, new object?[] { subscriber, forwarder }) as IDisposable;
        return token;
    }

    /// <summary>
    /// Resolve <c>ISubscriber&lt;T&gt;</c> from the game's VContainer resolver.
    /// Returns <c>null</c> and logs a warning if the required types are absent
    /// or the container has no matching <c>Resolve(Type)</c> overload.
    /// </summary>
    private object? ResolveSubscriberFromContainer(Type eventType)
    {
        var subscriberInterface = _types.FindType("MessagePipe.ISubscriber`1");
        if (subscriberInterface is null)
        {
            _log.Warning("[MessagePipe] MessagePipe.ISubscriber`1 is not loaded");
            return null;
        }

        var resolveMethod = FindResolveMethod(_resolver!.GetType());
        if (resolveMethod is null)
        {
            _log.Warning("[MessagePipe] container has no Resolve(Type) method");
            return null;
        }

        var closedSubscriber = subscriberInterface.MakeGenericType(eventType);
        return resolveMethod.Invoke(_resolver, new object[] { closedSubscriber });
    }

    /// <summary>
    /// Locate the <c>Subscribe(this ISubscriber&lt;T&gt;, Action&lt;T&gt;)</c> extension
    /// method on <c>MessagePipe.SubscribeExtensions</c>. Returns <c>null</c> and logs
    /// a warning if the type or the overload cannot be found.
    /// </summary>
    private MethodInfo? FindSubscribeExtensionMethod(Type eventType)
    {
        var subscribeExtensions = _types.FindType("MessagePipe.SubscribeExtensions");
        if (subscribeExtensions is null)
        {
            _log.Warning("[MessagePipe] MessagePipe.SubscribeExtensions is not loaded");
            return null;
        }

        var subscribeAction = subscribeExtensions.GetMethods()
            .FirstOrDefault(m =>
                m.Name == "Subscribe" &&
                m.IsGenericMethodDefinition &&
                m.GetParameters().Length >= 2 &&
                m.GetParameters()[1].ParameterType.IsGenericType &&
                m.GetParameters()[1].ParameterType.GetGenericTypeDefinition() == typeof(Action<>));

        if (subscribeAction is null)
        {
            _log.Warning("[MessagePipe] Subscribe(this ISubscriber<T>, Action<T>) extension not found");
        }
        return subscribeAction;
    }

    private static MethodInfo? FindResolveMethod(Type resolverType)
    {
        return resolverType.GetMethod("Resolve", new[] { typeof(Type) })
            ?? resolverType.GetMethods()
                .FirstOrDefault(m => m.Name == "Resolve" && m.GetParameters().Length == 1 && m.GetParameters()[0].ParameterType == typeof(Type));
    }

    private static Delegate CreateForwarder(Type eventType, Action<object?> downstream)
    {
        var method = typeof(MessagePipeContainerBridge)
            .GetMethod(nameof(Forward), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(eventType);
        return (Delegate)method.Invoke(null, new object[] { downstream })!;
    }

    private static Delegate Forward<T>(Action<object?> downstream) => new Action<T>(message => downstream(message));
}
