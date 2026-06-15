using System;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Receive-payload extraction helpers for <see cref="PandaChatProbe"/>. Pulls
/// the inner message out of a StubCall / ProxyReturn / wrapper-property arg
/// for the postfix dispatcher on <c>.Receive.Dispatch.cs</c>. The chat-shape
/// filter and concrete-type resolution that gate downstream projection live
/// on <c>.Receive.Resolution.cs</c>.
/// </summary>
internal sealed partial class PandaChatProbe
{
    private object? ExtractMessageFromStubCall(object stubCall)
    {
        if (_stubCallMsgProperty is not null)
        {
            try { return _stubCallMsgProperty.GetValue(stubCall); }
            catch { return null; }
        }
        if (_stubCallMsgField is not null)
        {
            try { return _stubCallMsgField.GetValue(stubCall); }
            catch { return null; }
        }
        return null;
    }

    /// <summary>
    /// Tolerant payload extraction for non-StubCall sites:
    ///   - If the argument IS already a protobuf message (Il2CppObjectBase whose
    ///     IL2CPP class lives under Google.Protobuf or Zproto/Chat namespaces),
    ///     return it directly.
    ///   - Otherwise try the standard wrapper property names (Msg/CallMsg/Body/
    ///     Payload/Message). Field fallbacks for the snake-cased backings too.
    /// Reflection is cached per HookSite (one lookup per distinct arg type),
    /// so the steady-state hot path is one cached property/field get.
    /// </summary>
    private static object? ExtractMessageTolerant(object firstArg, HookSite site)
    {
        var t = firstArg.GetType();

        // ProxyReturn unwrap path — at ProcessReturn / AddProxyReturn the arg is a
        // ZCode.ZRpc.ProxyReturn wrapper with no exposed Msg/Body/Payload property.
        // GetRetMsg(IProxyReturn, bool?) is the static helper that pulls out the
        // inner IBufferMessage. Resolved once at PatchAll; if missing we fall
        // through to the legacy wrapper-property scan (which will return null for
        // ProxyReturn, same as before this fix — safe degradation).
        if (IsProxyReturnType(t))
        {
            var unwrapped = TryUnwrapProxyReturn(firstArg);
            if (unwrapped is not null) return unwrapped;
            // null inner or GetRetMsg threw — fall through to wrapper scan.
        }

        return ReadWrappedMessage(firstArg, t, site);
    }

    private static bool IsProxyReturnType(Type t)
    {
        if (_getRetMsgMethod is null) return false;
        var typeName = t.FullName ?? string.Empty;
        return typeName == "ZCode.ZRpc.ProxyReturn"
            || typeName.EndsWith(".ProxyReturn", StringComparison.Ordinal)
            || t.Name == "ProxyReturn";
    }

    // Invoke the resolved GetRetMsg helper to extract the inner IBufferMessage
    // from a ProxyReturn wrapper. Handles the IL2CPP bridge gotcha (managed
    // reflection refuses the concrete-to-interface argument check) by routing
    // the wrapper through Il2CppObjectBase.Cast<IProxyReturn>() first. Static
    // vs instance helpers are distinguished by IsStatic. Returns null on any
    // failure — caller falls through to the legacy wrapper scan.
    private static object? TryUnwrapProxyReturn(object firstArg)
    {
        try
        {
            object? proxyArg = firstArg;
            if (_il2cppCastMethod is not null && firstArg is Il2CppObjectBase)
            {
                try
                {
                    proxyArg = _il2cppCastMethod.Invoke(firstArg, Array.Empty<object?>());
                }
                catch
                {
                    // Cast failed — Invoke below will likely also fail, but the
                    // catch keeps us safe and falls through to the legacy scan.
                }
            }

            var unwrapped = ReadProxyReturnPayload(_getRetMsgMethod!, proxyArg);
            if (System.Threading.Interlocked.Exchange(ref _getRetMsgInvokeLogged, 1) == 0)
            {
                var unwrappedType = unwrapped?.GetType().FullName ?? "null";
                Instance?._log.Info($"[ChatProbe] GetRetMsg first invoke: result type={unwrappedType}");
            }
            return unwrapped;
        }
        catch (Exception unwrapEx)
        {
            if (System.Threading.Interlocked.Exchange(ref _getRetMsgInvokeFailLogged, 1) == 0)
            {
                var inner = unwrapEx.InnerException;
                Instance?._log.Warning($"[ChatProbe] GetRetMsg invoke threw: {unwrapEx.GetType().Name}: {unwrapEx.Message} inner={inner?.GetType().Name}:{inner?.Message}");
            }
            return null;
        }
    }

    /// <summary>
    /// Invoke <paramref name="getRetMsg"/> against <paramref name="proxyArg"/>
    /// with the parameter shape it advertises. Static helpers take the proxy
    /// as the first positional arg + (0..2) trailing flag bools. Instance
    /// helpers take just (0..2) flag bools (target is the proxy). The bool
    /// flags are routing knobs on <c>GetRetMsg</c> — both <c>false</c>
    /// returns the unwrapped inner <c>IBufferMessage</c> directly.
    /// </summary>
    private static object? ReadProxyReturnPayload(MethodInfo getRetMsg, object? proxyArg)
    {
        var paramCount = getRetMsg.GetParameters().Length;
        object? target;
        object?[] args;
        if (getRetMsg.IsStatic)
        {
            target = null;
            args = paramCount switch
            {
                1 => new object?[] { proxyArg },
                2 => new object?[] { proxyArg, false },
                3 => new object?[] { proxyArg, false, false },
                _ => new object?[] { proxyArg }
            };
        }
        else
        {
            target = proxyArg;
            args = paramCount switch
            {
                0 => Array.Empty<object?>(),
                1 => new object?[] { false },
                2 => new object?[] { false, false },
                _ => new object?[paramCount]
            };
        }
        return getRetMsg.Invoke(target, args);
    }

    // Legacy wrapper-property scan. Refreshes the site's cached accessor when
    // the concrete arg type changes, then reads Msg/CallMsg/Body/Payload/
    // Message (or the snake-cased field backings). Falls back to returning the
    // argument itself when it's an IL2CPP-backed object — downstream filters
    // by IL2CPP class name.
    private static object? ReadWrappedMessage(object firstArg, Type t, HookSite site)
    {
        // Refresh the cached accessor when we see a new concrete type at this site.
        if (!ReferenceEquals(site.CachedArgType, t))
        {
            site.MsgProperty = t.GetProperty("Msg", AnyInstance)
                ?? t.GetProperty("CallMsg", AnyInstance)
                ?? t.GetProperty("Body", AnyInstance)
                ?? t.GetProperty("Payload", AnyInstance)
                ?? t.GetProperty("Message", AnyInstance)
                ?? t.GetProperty("callMsg_", AnyInstance)
                ?? t.GetProperty("msg_", AnyInstance)
                ?? t.GetProperty("body_", AnyInstance)
                ?? t.GetProperty("payload_", AnyInstance);
            if (site.MsgProperty is null)
            {
                site.MsgField = t.GetField("callMsg_", AnyInstance)
                    ?? t.GetField("msg_", AnyInstance)
                    ?? t.GetField("body_", AnyInstance)
                    ?? t.GetField("payload_", AnyInstance);
            }
            else
            {
                site.MsgField = null;
            }
            site.CachedArgType = t;
        }

        if (site.MsgProperty is not null)
        {
            try { return site.MsgProperty.GetValue(firstArg); }
            catch { /* fall through */ }
        }
        if (site.MsgField is not null)
        {
            try { return site.MsgField.GetValue(firstArg); }
            catch { /* swallow */ }
        }

        return firstArg is Il2CppObjectBase ? firstArg : null;
    }
}
