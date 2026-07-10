using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Events;

/// <summary>
/// Preferred event bridge: obtains the game's own <c>MessagePipe.ISubscriber&lt;T&gt;</c>
/// broker and registers a managed handler on it — plain managed calls into the game's
/// pub/sub, no HarmonyX patching. Resolution ladder (first hit wins, see the
/// <c>Resolution</c> partial):
/// <list type="number">
///   <item>the VContainer <c>IObjectResolver</c> Host attached off <c>Game.GameRoot</c>;</item>
///   <item><c>VContainer.Unity.VContainerSettings.Instance.rootLifetimeScopeInstance.Container</c>
///     (the raw field-backed scope — never <c>GetOrCreateRootLifetimeScopeInstance</c>,
///     which would side-effect an empty root scope into existence);</item>
///   <item><c>MessagePipe.GlobalMessagePipe.GetSubscriber&lt;T&gt;()</c> when the game has
///     called <c>SetProvider</c> (checked via <c>IsInitialized</c>).</item>
/// </list>
/// Returns <c>null</c> from <see cref="TrySubscribe"/> when no route yields a subscriber —
/// falls through to <see cref="HarmonyEventBridge"/> in that case.
///
/// <para>
/// IL2CPP interop shape (verified against the release_2.11 interop assemblies —
/// the original implementation of this bridge missed all four):
/// the Action-based Subscribe extension lives on <c>MessagePipe.SubscriberExtensions</c>
/// (NOT <c>SubscribeExtensions</c>); its handler parameter is
/// <c>Il2CppSystem.Action&lt;T&gt;</c> (converted from the managed delegate via the
/// projected <c>op_Implicit</c>, i.e. Il2CppInterop's <c>DelegateSupport.ConvertDelegate</c>);
/// <c>IObjectResolver.Resolve</c> takes an <c>Il2CppSystem.Type</c>; and the returned
/// token is an interop <c>Il2CppSystem.IDisposable</c> wrapper that does not implement
/// managed <see cref="IDisposable"/>. The same code paths also accept plain managed
/// shapes (System.Action / System.Type / IDisposable), which is what the unit tests
/// exercise via duck-typed fakes.
/// </para>
/// </summary>
internal sealed partial class MessagePipeContainerBridge : IGameEventBridge
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
    /// Subsequent <see cref="TrySubscribe"/> calls will attempt the container route first.
    /// </summary>
    public void AttachResolver(object? resolver)
    {
        _resolver = resolver;
        if (_resolver is null)
        {
            _log.Warning("[MessagePipe] no resolver attached; container route disabled (root-scope + GlobalMessagePipe routes remain)");
            return;
        }
        _log.Info($"[MessagePipe] resolver attached: {_resolver.GetType().FullName}");
    }

    public IDisposable? TrySubscribe(string fullTypeName, Action<object?> handler)
    {
        var eventType = _types.FindType(fullTypeName);
        if (eventType is null)
        {
            return null;
        }

        var subscriber = ResolveSubscriber(eventType, out string route);
        if (subscriber is null)
        {
            return null;
        }

        var token = InvokeSubscribe(eventType, subscriber, handler);
        if (token is not null)
        {
            _log.Info($"[MessagePipe] ISubscriber<{eventType.Name}> resolved via {route}; handler subscribed");
        }
        return token;
    }

    /// <summary>
    /// Close the Action-based <c>Subscribe</c> extension over <paramref name="eventType"/>,
    /// coerce the managed forwarder to the method's declared handler type, and invoke it.
    /// Returns a managed token that keeps the delegate chain alive, or <c>null</c> when the
    /// extension surface is missing.
    /// </summary>
    private IDisposable? InvokeSubscribe(Type eventType, object subscriber, Action<object?> handler)
    {
        var subscribeDef = FindSubscribeExtensionMethod();
        if (subscribeDef is null)
        {
            return null;
        }

        var closed = subscribeDef.MakeGenericMethod(eventType);
        var ps = closed.GetParameters();

        // Managed Action<T> forwarder; converted to Il2CppSystem.Action<T> via the
        // projected op_Implicit when the declared parameter demands it.
        var forwarder = CreateForwarder(eventType, handler);
        var coerced = CoerceDelegate(forwarder, ps[1].ParameterType);
        if (coerced is null)
        {
            _log.Warning($"[MessagePipe] cannot convert Action<{eventType.Name}> to {ps[1].ParameterType.Name}");
            return null;
        }

        // Reflection Invoke does not auto-fill params arrays — pass an explicit
        // empty filters array of the closed element type.
        var filters = Array.CreateInstance(ps[2].ParameterType.GetElementType()!, 0);
        var token = closed.Invoke(null, new object?[] { subscriber, coerced, filters });
        if (token is null)
        {
            return null;
        }

        // Hold the managed forwarder AND the coerced (possibly il2cpp) delegate for the
        // token's lifetime so the GC can never collect the subscription out from under
        // the game's broker.
        return new SubscriptionToken(token, forwarder, coerced);
    }
}
