using System;
using System.Collections.Generic;
using Stellar.Application.Abstractions;
using Stellar.Infrastructure.Events;
using Xunit;

namespace Stellar.Application.Tests.Events;

/// <summary>
/// Covers <see cref="MessagePipeContainerBridge"/>'s subscription decision logic through
/// duck-typed managed fakes injected via <see cref="IGameTypeRegistry"/> — the same
/// shape-first reflection the bridge uses against the real IL2CPP interop surface
/// (route ladder, Subscribe-extension overload matching, explicit empty params-array,
/// duck-typed token disposal). The il2cpp-only coercions (op_Implicit delegate
/// conversion, Il2CppSystem.Type resolve) are exercised only in-game.
/// </summary>
public sealed class MessagePipeContainerBridgeTests
{
    private const string EventName = "Fake.Events.FakeDirtyEvent";

    private readonly StubLog _log = new();
    private readonly FakeRegistry _registry = new();
    private readonly MessagePipeContainerBridge _bridge;
    private readonly FakeBroker<FakeDirtyEvent> _broker = new();

    public MessagePipeContainerBridgeTests()
    {
        _bridge = new MessagePipeContainerBridge(_log, _registry);
        _registry.Map(EventName, typeof(FakeDirtyEvent));
        _registry.Map("MessagePipe.ISubscriber`1", typeof(IFakeSubscriber<>));
        _registry.Map("MessagePipe.SubscriberExtensions", typeof(FakeSubscriberExtensions));
        FakeGlobalMessagePipe.Reset();
        FakeVContainerSettings.Instance = null;
    }

    [Fact]
    public void TrySubscribe_ViaAttachedResolver_DeliversPublishedEvents()
    {
        var resolver = new FakeResolver();
        resolver.Register(typeof(IFakeSubscriber<FakeDirtyEvent>), _broker);
        _bridge.AttachResolver(resolver);

        object? seen = null;
        var token = _bridge.TrySubscribe(EventName, e => seen = e);

        Assert.NotNull(token);
        var evt = new FakeDirtyEvent();
        _broker.Publish(evt);
        Assert.Same(evt, seen);
        Assert.Contains(_log.InfoLines, l => l.Contains("GameRoot container resolver"));
    }

    [Fact]
    public void TrySubscribe_TokenDispose_UnsubscribesViaDuckTypedDispose()
    {
        var resolver = new FakeResolver();
        resolver.Register(typeof(IFakeSubscriber<FakeDirtyEvent>), _broker);
        _bridge.AttachResolver(resolver);

        int calls = 0;
        var token = _bridge.TrySubscribe(EventName, _ => calls++);

        token!.Dispose(); // fake game token is NOT System.IDisposable — duck-typed Dispose()
        _broker.Publish(new FakeDirtyEvent());
        Assert.Equal(0, calls);
        Assert.Equal(0, _broker.HandlerCount);
    }

    [Fact]
    public void TrySubscribe_PassesEmptyFiltersArray_NeverNull()
    {
        var resolver = new FakeResolver();
        resolver.Register(typeof(IFakeSubscriber<FakeDirtyEvent>), _broker);
        _bridge.AttachResolver(resolver);

        Assert.NotNull(_bridge.TrySubscribe(EventName, _ => { }));
        Assert.NotNull(FakeSubscriberExtensions.LastFilters);
        Assert.Empty((Array)FakeSubscriberExtensions.LastFilters!);
    }

    [Fact]
    public void TrySubscribe_ViaVContainerSettingsRootScope_WhenNoResolverAttached()
    {
        _registry.Map("VContainer.Unity.VContainerSettings", typeof(FakeVContainerSettings));
        var container = new FakeResolver();
        container.Register(typeof(IFakeSubscriber<FakeDirtyEvent>), _broker);
        FakeVContainerSettings.Instance = new FakeVContainerSettings
        {
            rootLifetimeScopeInstance = new FakeLifetimeScope { Container = container },
        };

        var token = _bridge.TrySubscribe(EventName, _ => { });

        Assert.NotNull(token);
        Assert.Contains(_log.InfoLines, l => l.Contains("VContainerSettings.RootLifetimeScope.Container"));
    }

    [Fact]
    public void TrySubscribe_ViaGlobalMessagePipe_WhenInitialized()
    {
        _registry.Map("MessagePipe.GlobalMessagePipe", typeof(FakeGlobalMessagePipe));
        FakeGlobalMessagePipe.Broker = _broker;
        FakeGlobalMessagePipe.IsInitialized = true;

        var token = _bridge.TrySubscribe(EventName, _ => { });

        Assert.NotNull(token);
        Assert.Contains(_log.InfoLines, l => l.Contains("GlobalMessagePipe.GetSubscriber<T>"));
    }

    [Fact]
    public void TrySubscribe_GlobalMessagePipeNotInitialized_ReturnsNull()
    {
        _registry.Map("MessagePipe.GlobalMessagePipe", typeof(FakeGlobalMessagePipe));
        FakeGlobalMessagePipe.Broker = _broker;
        FakeGlobalMessagePipe.IsInitialized = false;

        Assert.Null(_bridge.TrySubscribe(EventName, _ => { }));
    }

    [Fact]
    public void TrySubscribe_NoRouteAvailable_ReturnsNull()
    {
        // Event + MessagePipe shapes are loaded, but there is no resolver, no
        // VContainerSettings instance and no initialized GlobalMessagePipe.
        Assert.Null(_bridge.TrySubscribe(EventName, _ => { }));
    }

    [Fact]
    public void TrySubscribe_ResolverThrowsForUnregisteredType_FallsThroughToNull()
    {
        _bridge.AttachResolver(new FakeResolver()); // nothing registered → Resolve throws
        Assert.Null(_bridge.TrySubscribe(EventName, _ => { }));
    }

    [Fact]
    public void TrySubscribe_UnknownEventType_ReturnsNull()
    {
        Assert.Null(_bridge.TrySubscribe("No.Such.Event", _ => { }));
    }
}

// ── duck-typed fakes (matched by SHAPE, exactly like the interop surface) ──────

internal sealed class FakeDirtyEvent { }

internal interface IFakeSubscriber<T> { }

internal sealed class FakeBroker<T> : IFakeSubscriber<T>
{
    private readonly List<Action<T>> _handlers = new();
    public int HandlerCount => _handlers.Count;
    public void Add(Action<T> handler) => _handlers.Add(handler);
    public void Remove(Action<T> handler) => _handlers.Remove(handler);
    public void Publish(T message)
    {
        foreach (var h in _handlers.ToArray()) h(message);
    }
}

internal class FakeHandlerFilter<T> { }

// The game-side subscription token deliberately does NOT implement System.IDisposable —
// mirrors the interop token (Il2CppSystem.IDisposable only) to prove the bridge's
// duck-typed Dispose path.
internal sealed class FakeSubscriptionToken<T>
{
    private readonly FakeBroker<T> _broker;
    private readonly Action<T> _handler;
    public FakeSubscriptionToken(FakeBroker<T> broker, Action<T> handler)
    {
        _broker = broker;
        _handler = handler;
    }
    public void Dispose() => _broker.Remove(_handler);
}

internal static class FakeSubscriberExtensions
{
    public static object? LastFilters;

    public static object Subscribe<TMessage>(
        IFakeSubscriber<TMessage> subscriber, Action<TMessage> handler, FakeHandlerFilter<TMessage>[] filters)
    {
        LastFilters = filters;
        var broker = (FakeBroker<TMessage>)subscriber;
        broker.Add(handler);
        return new FakeSubscriptionToken<TMessage>(broker, handler);
    }
}

internal sealed class FakeResolver
{
    private readonly Dictionary<Type, object> _registrations = new();
    public void Register(Type type, object instance) => _registrations[type] = instance;
    public object Resolve(Type type)
        => _registrations.TryGetValue(type, out var o) ? o : throw new InvalidOperationException($"unregistered: {type}");
}

internal sealed class FakeLifetimeScope
{
    public FakeResolver? Container { get; set; }
}

internal sealed class FakeVContainerSettings
{
    public static FakeVContainerSettings? Instance { get; set; }
    public FakeLifetimeScope? rootLifetimeScopeInstance { get; set; }
}

internal static class FakeGlobalMessagePipe
{
    public static bool IsInitialized { get; set; }
    public static object? Broker { get; set; }
    public static IFakeSubscriber<TMessage>? GetSubscriber<TMessage>() => (IFakeSubscriber<TMessage>?)Broker;
    public static void Reset()
    {
        IsInitialized = false;
        Broker = null;
    }
}

internal sealed class FakeRegistry : IGameTypeRegistry
{
    private readonly Dictionary<string, Type> _map = new();
    public void Map(string fullName, Type type) => _map[fullName] = type;
    public Type? FindType(string fullName) => _map.TryGetValue(fullName, out var t) ? t : null;
}
