using System;
using System.Reflection;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using Stellar.Application.Abstractions;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Lifetime tracking for the cached chat-server <c>ZTcpClient</c> that
/// <see cref="TrySend"/> dispatches through.
///
/// Why this exists: the game tears its connection down and builds a NEW
/// <c>ZTcpClient</c> on reconnect (line switch, some scene transfers, net drop,
/// logout → char select) via <c>ZNetExtensions.CloseAndDelayDispose(ZTcpClient)</c>.
/// <c>ZNativeObject.CheckValid()</c> is <c>[Conditional("DEBUG")]</c> — stripped
/// in release — so <c>Send</c> on the stale wrapper hands a freed native handle
/// to the native socket layer → access violation → the whole game dies (managed
/// try/catch can't catch it). Three guards, all fail-closed for sending:
/// <list type="bullet">
/// <item><see cref="CacheChatTcpClient"/> re-caches whenever a chat packet
///   arrives on a DIFFERENT connection (native-pointer identity).</item>
/// <item>PREFIXes on <c>ZTcpClient.Close</c> and
///   <c>ZNetExtensions.CloseAndDelayDispose(ZTcpClient)</c> drop the cache the
///   moment the game closes the cached instance.</item>
/// <item><see cref="TryTakeLiveChatClient"/> refuses to send through a client
///   whose native <c>handle_</c> reads zero.</item>
/// </list>
/// </summary>
internal sealed partial class PandaChatProbe
{
    // Native IL2CPP object pointer of _chatTcpClient. Identity is compared by
    // pointer, never by managed reference — Il2CppInterop can hand us distinct
    // wrapper objects for the same native instance (wire-tap env vs Harmony
    // __instance). Volatile so the per-packet fast path can skip the lock.
    private volatile IntPtr _chatTcpClientPtr;

    // The most recently CLOSED chat client, kept referenced on purpose: a late
    // inbound packet already queued on the dying connection must not re-cache
    // it. Holding the wrapper keeps the IL2CPP object alive (GC handle), so its
    // address can't be recycled by a new ZTcpClient and the pointer compare
    // stays sound. Only ever one instance — replaced on the next close.
    private object? _closedChatTcpClient;
    private IntPtr _closedChatTcpClientPtr;

    // Accessor for ZNativeObject.handle_ (Il2CppInterop projects the field as a
    // property) or, failing that, the internal Handle getter. Resolved lazily on
    // the first liveness check; _handleAccessorResolved gates re-resolution.
    private PropertyInfo? _tcpClientHandleAccessor;
    private bool _handleAccessorResolved;

    private static IntPtr NativePtr(object? o) => (o as Il2CppObjectBase)?.Pointer ?? IntPtr.Zero;

    /// <summary>
    /// Cache (or re-cache) the chat <c>ZTcpClient</c> carried by an inbound
    /// chat envelope. <see cref="_chatTcpClient"/> is the outbound send target
    /// for <see cref="IChat.Send"/>. Same native instance → no-op (lock-free fast
    /// path). A different instance replaces the cache — the game reconnected.
    /// The just-closed instance is never re-cached (see <see cref="_closedChatTcpClient"/>).
    /// </summary>
    /// <param name="label">Optional suffix appended to the boot log (e.g.
    /// <c>" (via method=3 path)"</c> distinguishes which notify path captured
    /// the client). Null = no suffix.</param>
    private void CacheChatTcpClient(WireEnvelope env, string? label)
    {
        var conn = env.Connection;
        if (conn is null) return;
        var ptr = NativePtr(conn);
        if (ptr == IntPtr.Zero || ptr == _chatTcpClientPtr) return;

        lock (_chatTcpClientLock)
        {
            if (ptr == _chatTcpClientPtr || ptr == _closedChatTcpClientPtr) return;
            bool first = _chatTcpClient is null && _closedChatTcpClient is null;
            _chatTcpClient = conn;
            _chatTcpClientPtr = ptr;
            if (first)
                _log.Info($"[ChatProbe] cached chat ZTcpClient instance ({conn.GetType().FullName}) for outbound send{label ?? string.Empty}");
            else
                _log.Info("[ChatProbe] chat connection changed — re-cached");
        }
    }

    /// <summary>
    /// Snapshot the cached chat client under the lock (so a concurrent close
    /// can't null it between check and Invoke) and verify its native handle is
    /// still non-zero. A zero handle means the wrapper was disposed — drop the
    /// cache and fail. Returns false with <paramref name="failureReason"/>.
    /// </summary>
    private bool TryTakeLiveChatClient(out object client, out string? failureReason)
    {
        client = null!;
        failureReason = null;
        lock (_chatTcpClientLock)
        {
            var cached = _chatTcpClient;
            if (cached is null)
            {
                failureReason = "chat connection closed (reconnecting) — will resume after next chat packet";
                return false;
            }
            if (ReadNativeHandle(cached) == IntPtr.Zero)
            {
                InvalidateCachedChatClientLocked(cached);
                _log.Info("[ChatProbe] chat connection closed — send disabled until next chat packet");
                failureReason = "chat connection closed (reconnecting) — will resume after next chat packet";
                return false;
            }
            client = cached;
            return true;
        }
    }

    /// <summary>
    /// Read <c>ZNativeObject.handle_</c> off <paramref name="client"/>. Returns
    /// the handle, or <c>-1</c> (treated as alive) when no accessor resolved —
    /// the close hooks are then the only guard; that case is logged once.
    /// </summary>
    private IntPtr ReadNativeHandle(object client)
    {
        if (!_handleAccessorResolved)
        {
            _handleAccessorResolved = true;
            _tcpClientHandleAccessor = FindPropertyUp(client.GetType(), "handle_")
                                       ?? FindPropertyUp(client.GetType(), "Handle");
            if (_tcpClientHandleAccessor is null)
                _log.Warning("[ChatProbe] ZNativeObject handle accessor not found — send liveness check disabled (close hooks still guard)");
        }
        if (_tcpClientHandleAccessor is null) return new IntPtr(-1);
        try
        {
            return _tcpClientHandleAccessor.GetValue(client) switch
            {
                IntPtr p => p,
                long l => new IntPtr(l),
                _ => IntPtr.Zero,
            };
        }
        catch
        {
            // Can't prove it's alive → fail closed.
            return IntPtr.Zero;
        }
    }

    /// <summary>Walk the base chain for an instance property (any visibility) — <c>handle_</c> is declared on <c>ZNativeObject</c>, not <c>ZTcpClient</c>.</summary>
    private static PropertyInfo? FindPropertyUp(Type? t, string name)
    {
        for (; t is not null; t = t.BaseType)
        {
            var p = t.GetProperty(name, AnyInstance | BindingFlags.DeclaredOnly);
            if (p is not null && p.GetMethod is not null) return p;
        }
        return null;
    }

    /// <summary>Drop <paramref name="cached"/> and remember it as closed. Caller holds <see cref="_chatTcpClientLock"/>.</summary>
    private void InvalidateCachedChatClientLocked(object cached)
    {
        _closedChatTcpClient = cached;
        _closedChatTcpClientPtr = _chatTcpClientPtr;
        _chatTcpClient = null;
        _chatTcpClientPtr = IntPtr.Zero;
    }

    /// <summary>
    /// Install the close-side PREFIXes once <c>ZTcpClient</c> is resolved:
    /// <c>ZTcpClient.Close(Action&lt;ZTcpClient&gt;)</c> and the static
    /// extension <c>ZNetExtensions.CloseAndDelayDispose(ZTcpClient)</c> (same
    /// namespace/assembly as ZTcpClient). Each failure is a warning only —
    /// patching fails open; the handle check in <see cref="TryTakeLiveChatClient"/>
    /// still guards sends. <c>ZNativeObject.Dispose</c> is deliberately NOT
    /// patched: it's the base of every native net object (UDP clients, timers),
    /// so the trampoline would fire far more often than the two close paths.
    /// </summary>
    private void InstallConnectionCloseHooks(Type tcpClientType)
    {
        MethodInfo? close = null;
        foreach (var m in tcpClientType.GetMethods(AnyInstance | BindingFlags.DeclaredOnly))
        {
            if (m.Name == "Close" && m.GetParameters().Length == 1) { close = m; break; }
        }
        TryPatchClosePrefix(close, nameof(OnTcpClientClosePrefix), $"{tcpClientType.FullName}.Close");

        MethodInfo? delayDispose = null;
        var ext = tcpClientType.Assembly.GetType($"{tcpClientType.Namespace}.ZNetExtensions");
        if (ext is not null)
        {
            foreach (var m in ext.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (m.Name != "CloseAndDelayDispose") continue;
                var ps = m.GetParameters();
                if (ps.Length == 1 && ps[0].ParameterType == tcpClientType) { delayDispose = m; break; }
            }
        }
        TryPatchClosePrefix(delayDispose, nameof(OnCloseAndDelayDisposePrefix), $"{tcpClientType.Namespace}.ZNetExtensions.CloseAndDelayDispose(ZTcpClient)");
    }

    private void TryPatchClosePrefix(MethodInfo? target, string prefixName, string label)
    {
        if (target is null)
        {
            _log.Warning($"[ChatProbe] {label} not found — chat connection close not observed on that path");
            return;
        }
        try
        {
            var prefix = typeof(PandaChatProbe).GetMethod(prefixName, BindingFlags.Static | BindingFlags.NonPublic)!;
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            _log.Info($"[ChatProbe] patched connection close (PREFIX): {label}");
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] failed to patch {label}: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>PREFIX on <c>ZTcpClient.Close(Action&lt;ZTcpClient&gt;)</c>. Never throws; one pointer compare for non-chat clients.</summary>
    private static void OnTcpClientClosePrefix(object __instance) => OnChatClientClosing(__instance);

    /// <summary>PREFIX on static <c>ZNetExtensions.CloseAndDelayDispose(ZTcpClient)</c>. Never throws.</summary>
    private static void OnCloseAndDelayDisposePrefix(object?[] __args)
        => OnChatClientClosing(__args is { Length: > 0 } ? __args[0] : null);

    private static void OnChatClientClosing(object? client)
    {
        try
        {
            var probe = Instance;
            if (probe is null || client is null) return;
            var ptr = NativePtr(client);
            if (ptr == IntPtr.Zero || ptr != probe._chatTcpClientPtr) return;
            lock (probe._chatTcpClientLock)
            {
                var cached = probe._chatTcpClient;
                if (cached is null || ptr != probe._chatTcpClientPtr) return;
                probe.InvalidateCachedChatClientLocked(cached);
            }
            probe._log.Info("[ChatProbe] chat connection closed — send disabled until next chat packet");
        }
        catch
        {
            // Never propagate across the IL2CPP boundary.
        }
    }
}
