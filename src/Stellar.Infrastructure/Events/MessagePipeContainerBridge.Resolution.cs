using System;
using System.Linq;
using System.Reflection;

namespace Stellar.Infrastructure.Events;

/// <summary>
/// Resolution machinery for <see cref="MessagePipeContainerBridge"/>: the three-route
/// subscriber ladder, the <c>Subscribe</c> extension-method lookup, the managed→il2cpp
/// delegate/type coercions, and the lifetime-holding subscription token. Everything is
/// duck-typed reflection so the same code serves the real interop surface and the
/// managed fakes the unit tests inject through <c>IGameTypeRegistry</c>.
/// </summary>
internal sealed partial class MessagePipeContainerBridge
{
    private const BindingFlags AnyStatic = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags AnyInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    // ── Route ladder ──────────────────────────────────────────────────────────

    /// <summary>
    /// Obtain <c>ISubscriber&lt;eventType&gt;</c> via the first available route:
    /// attached GameRoot resolver → VContainerSettings root scope → GlobalMessagePipe.
    /// Each route failure is swallowed (logged only by the caller's overall outcome) —
    /// resolution runs repeatedly from the framework tick until a route lands.
    /// </summary>
    private object? ResolveSubscriber(Type eventType, out string route)
    {
        var subscriberInterface = _types.FindType("MessagePipe.ISubscriber`1");
        if (subscriberInterface is null)
        {
            route = "none (MessagePipe.ISubscriber`1 not loaded)";
            return null;
        }
        var closedSubscriber = subscriberInterface.MakeGenericType(eventType);

        var viaResolver = TryRoute(() => ResolveFromResolver(_resolver, closedSubscriber));
        if (viaResolver is not null) { route = "GameRoot container resolver"; return viaResolver; }

        var viaRootScope = TryRoute(() => ResolveFromResolver(FindRootScopeResolver(), closedSubscriber));
        if (viaRootScope is not null) { route = "VContainerSettings.RootLifetimeScope.Container"; return viaRootScope; }

        var viaGlobal = TryRoute(() => ResolveFromGlobalMessagePipe(eventType));
        if (viaGlobal is not null) { route = "GlobalMessagePipe.GetSubscriber<T>"; return viaGlobal; }

        route = "none";
        return null;
    }

    /// <summary>
    /// Resolve an arbitrary game service instance through the same container ladder
    /// (GameRoot resolver → VContainerSettings root scope). Used by consumers that
    /// need the service OBJECT rather than a MessagePipe subscriber (e.g. the
    /// DungeonSyncService OnSync delegate wrap). Null when no route lands yet.
    /// </summary>
    internal object? TryResolveService(string fullTypeName, out string route)
    {
        var serviceType = _types.FindType(fullTypeName);
        if (serviceType is null) { route = $"none ({fullTypeName} not loaded)"; return null; }

        var viaResolver = TryRoute(() => ResolveFromResolver(_resolver, serviceType));
        if (viaResolver is not null) { route = "GameRoot container resolver"; return viaResolver; }

        var viaRootScope = TryRoute(() => ResolveFromResolver(FindRootScopeResolver(), serviceType));
        if (viaRootScope is not null) { route = "VContainerSettings.RootLifetimeScope.Container"; return viaRootScope; }

        // Child-scope scan: services like DungeonSyncService are registered in a
        // CHILD LifetimeScope (world/game scope), invisible from the root container
        // (proven live 2026-07-05: root resolve never succeeded). LifetimeScopes are
        // Unity components — enumerate them all and try each scope's container.
        // Main-thread only (caller runs on the framework tick).
        foreach (var container in FindAllLifetimeScopeContainers())
        {
            var viaChild = TryRoute(() => ResolveFromResolver(container, serviceType));
            if (viaChild is not null) { route = "child LifetimeScope scan"; return viaChild; }
        }

        route = "none";
        return null;
    }

    /// <summary>
    /// Every <c>VContainer.Unity.LifetimeScope</c> currently alive (including inactive),
    /// via <c>UnityEngine.Object.FindObjectsOfType(Il2CppSystem.Type)</c> — each yields
    /// its <c>Container</c>. Defensive: any interop hiccup yields nothing.
    /// </summary>
    private System.Collections.Generic.IEnumerable<object> FindAllLifetimeScopeContainers()
    {
        object? scopes = null;
        try
        {
            var scopeType = _types.FindType("VContainer.Unity.LifetimeScope");
            var unityObject = _types.FindType("UnityEngine.Object");
            if (scopeType is null || unityObject is null) yield break;

            var findAll = unityObject.GetMethod("FindObjectsOfType", AnyStatic, null,
                new[] { ToIl2CppType(scopeType)?.GetType() ?? typeof(Type) }, null);
            if (findAll is null) yield break;

            var typeArg = findAll.GetParameters()[0].ParameterType == typeof(Type)
                ? (object?)scopeType
                : ToIl2CppType(scopeType);
            if (typeArg is null) yield break;
            scopes = findAll.Invoke(null, new[] { typeArg });
        }
        catch { yield break; }
        if (scopes is not System.Collections.IEnumerable list) yield break;

        foreach (var scope in list)
        {
            object? container = null;
            try { container = scope?.GetType().GetProperty("Container", AnyInstance)?.GetValue(scope); }
            catch { /* dead scope */ }
            if (container is not null) yield return container;
        }
    }

    // A route probe must never throw out of the ladder: an unregistered type makes
    // VContainer's Resolve throw, and interop getters can throw on dead objects.
    private static object? TryRoute(Func<object?> probe)
    {
        try { return probe(); }
        catch { return null; }
    }

    /// <summary>
    /// Call <c>Resolve</c> on a VContainer-shaped resolver. The interop surface takes an
    /// <c>Il2CppSystem.Type</c>; managed fakes take a <see cref="System.Type"/> — pick the
    /// overload by parameter type and coerce the argument accordingly.
    /// </summary>
    private static object? ResolveFromResolver(object? resolver, Type closedSubscriber)
    {
        if (resolver is null) return null;

        foreach (var m in resolver.GetType().GetMethods(AnyInstance))
        {
            if (m.Name != "Resolve") continue;
            var ps = m.GetParameters();
            if (ps.Length != 1) continue;

            if (ps[0].ParameterType == typeof(Type))
                return m.Invoke(resolver, new object[] { closedSubscriber });

            if (ps[0].ParameterType.Name == "Type")
            {
                var il2CppType = ToIl2CppType(closedSubscriber);
                if (il2CppType is null) return null;
                return m.Invoke(resolver, new[] { il2CppType });
            }
        }
        return null;
    }

    /// <summary>
    /// <c>VContainerSettings.Instance</c> → the raw <c>rootLifetimeScopeInstance</c> (field-backed;
    /// deliberately NOT <c>RootLifetimeScope</c>/<c>GetOrCreateRootLifetimeScopeInstance</c>, which
    /// can create an empty root scope as a side effect) → its <c>Container</c>.
    /// </summary>
    private object? FindRootScopeResolver()
    {
        var settingsType = _types.FindType("VContainer.Unity.VContainerSettings");
        var settings = settingsType?.GetProperty("Instance", AnyStatic)?.GetValue(null);
        if (settings is null) return null;

        var scope = settings.GetType().GetProperty("rootLifetimeScopeInstance", AnyInstance)?.GetValue(settings);
        return scope?.GetType().GetProperty("Container", AnyInstance)?.GetValue(scope);
    }

    /// <summary>
    /// <c>GlobalMessagePipe.GetSubscriber&lt;T&gt;()</c> — only valid when the game called
    /// <c>SetProvider</c> (guarded via <c>IsInitialized</c> so the projected throw-helper
    /// never fires).
    /// </summary>
    private object? ResolveFromGlobalMessagePipe(Type eventType)
    {
        var globalType = _types.FindType("MessagePipe.GlobalMessagePipe");
        if (globalType is null) return null;

        if (globalType.GetProperty("IsInitialized", AnyStatic)?.GetValue(null) is not true) return null;

        var def = globalType.GetMethods(AnyStatic).FirstOrDefault(m =>
            m.Name == "GetSubscriber" &&
            m.IsGenericMethodDefinition &&
            m.GetGenericArguments().Length == 1 &&
            m.GetParameters().Length == 0);
        return def?.MakeGenericMethod(eventType).Invoke(null, null);
    }

    // ── Subscribe extension + coercions ──────────────────────────────────────

    /// <summary>
    /// Locate the Action-based <c>Subscribe&lt;T&gt;(subscriber, handler, filters[])</c>
    /// extension. The interop class is <c>MessagePipe.SubscriberExtensions</c>; the
    /// legacy managed name <c>SubscribeExtensions</c> is kept as a fallback. Matched
    /// shape-first (generic def, 3 params, <c>Action`1</c> handler, managed filters
    /// array) so both the il2cpp projection and managed fakes bind.
    /// </summary>
    private MethodInfo? FindSubscribeExtensionMethod()
    {
        var extensions = _types.FindType("MessagePipe.SubscriberExtensions")
            ?? _types.FindType("MessagePipe.SubscribeExtensions");
        if (extensions is null)
        {
            _log.Warning("[MessagePipe] MessagePipe.SubscriberExtensions is not loaded");
            return null;
        }

        var subscribe = extensions.GetMethods(AnyStatic).FirstOrDefault(m =>
            m.Name == "Subscribe" &&
            m.IsGenericMethodDefinition &&
            m.GetGenericArguments().Length == 1 &&
            m.GetParameters().Length == 3 &&
            m.GetParameters()[1].ParameterType.Name == "Action`1" &&
            m.GetParameters()[2].ParameterType.IsArray);

        if (subscribe is null)
        {
            _log.Warning("[MessagePipe] Subscribe(ISubscriber<T>, Action<T>, filters[]) extension not found");
        }
        return subscribe;
    }

    /// <summary>
    /// Coerce the managed <c>Action&lt;T&gt;</c> forwarder to the Subscribe method's declared
    /// handler parameter. Managed shape: pass through. Interop shape: invoke the projected
    /// <c>Il2CppSystem.Action&lt;T&gt;.op_Implicit(System.Action&lt;T&gt;)</c>, which routes
    /// through Il2CppInterop's <c>DelegateSupport.ConvertDelegate</c> — the sanctioned way to
    /// hand a managed delegate to il2cpp code.
    /// </summary>
    private static object? CoerceDelegate(Delegate forwarder, Type declaredHandlerType)
    {
        if (declaredHandlerType.IsInstanceOfType(forwarder)) return forwarder;

        var opImplicit = declaredHandlerType.GetMethod(
            "op_Implicit", AnyStatic, binder: null, types: new[] { forwarder.GetType() }, modifiers: null);
        return opImplicit?.Invoke(null, new object[] { forwarder });
    }

    // System.Type → Il2CppSystem.Type via Il2CppInterop.Runtime.Il2CppType.From(Type).
    // Reflection-only so this assembly never JITs against Il2CppInterop in unit tests.
    private static object? ToIl2CppType(Type type)
    {
        var il2CppTypeHelper = Type.GetType("Il2CppInterop.Runtime.Il2CppType, Il2CppInterop.Runtime", throwOnError: false);
        var from = il2CppTypeHelper?.GetMethod("From", AnyStatic, binder: null, types: new[] { typeof(Type) }, modifiers: null);
        return from?.Invoke(null, new object[] { type });
    }

    private static Delegate CreateForwarder(Type eventType, Action<object?> downstream)
    {
        var method = typeof(MessagePipeContainerBridge)
            .GetMethod(nameof(Forward), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(eventType);
        return (Delegate)method.Invoke(null, new object[] { downstream })!;
    }

    private static Delegate Forward<T>(Action<object?> downstream) => new Action<T>(message => downstream(message));

    /// <summary>
    /// Managed disposal token. Pins the managed forwarder and the coerced il2cpp delegate
    /// for the subscription's lifetime, and disposes the game-side token either through
    /// managed <see cref="IDisposable"/> or a duck-typed <c>Dispose()</c> (the interop
    /// token implements <c>Il2CppSystem.IDisposable</c>, not the managed interface).
    /// </summary>
    private sealed class SubscriptionToken : IDisposable
    {
        private object? _gameToken;
#pragma warning disable IDE0052 // held intentionally: GC roots for the live subscription
        private Delegate? _managedForwarder;
        private object? _coercedHandler;
#pragma warning restore IDE0052

        public SubscriptionToken(object gameToken, Delegate managedForwarder, object coercedHandler)
        {
            _gameToken = gameToken;
            _managedForwarder = managedForwarder;
            _coercedHandler = coercedHandler;
        }

        public void Dispose()
        {
            var token = _gameToken;
            if (token is null) return;
            _gameToken = null;
            try
            {
                if (token is IDisposable managed) managed.Dispose();
                else token.GetType().GetMethod("Dispose", AnyInstance, binder: null, types: Type.EmptyTypes, modifiers: null)
                          ?.Invoke(token, null);
            }
            catch { /* disposal must never throw */ }
            _managedForwarder = null;
            _coercedHandler = null;
        }
    }
}
