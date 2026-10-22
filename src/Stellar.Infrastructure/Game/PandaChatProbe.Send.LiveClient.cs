using System;
using System.Collections.Generic;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Active, on-demand resolution of the <b>live</b> chat <c>ZTcpClient</c> for
/// <see cref="TrySend"/>. The passively-captured client (see
/// <c>Send.Connection.cs</c>) is only populated from inbound chat packets, so
/// after a reconnect — and, critically, at fresh login before any inbound chat
/// packet or native send — sends fail-closed until the next inbound packet
/// re-caches it. This partial closes that window by walking the game's RPC
/// controller to the socket on every send — there is no cache to go stale.
///
/// Chat is NOT a dedicated socket: the whole game multiplexes RPC over two
/// transports owned by <c>ZCode.ZRpc.ZRpcCtrl</c>. ChitChat is a PURE Lua RPC
/// proxy (id 1321197368, no C# factory), so we resolve its live socket via the
/// game's own by-proxy resolver <c>ZRpcCtrl.GetRpcByProxyId(ulong)</c>. That
/// resolves by proxy-manager membership (populated at login by <c>AddLuaProxy</c>),
/// NOT by the lazy <c>uuid2ChannelType_</c> cache that <c>GetChannelTypeByProxyId</c>
/// reads — the latter is only seeded for the send proxy as a side effect of the
/// FIRST native <c>SendChitChatMsg</c>, so it is empty at fresh login (history
/// arrives on a different stub uuid) and the old two-step lookup failed exactly
/// when a native send had not happened yet. <c>GetRpcByProxyId</c> works at fresh
/// login precisely when the native send does.
///
/// Live chain (all by reflection; verified against release_3.7 dump.cs):
/// <code>
/// ZRpcCtrl.Instance.GetRpcByProxyId(ChitChatProxyId)  // ZSingleton&lt;ZRpcCtrl&gt;.Instance, FlattenHierarchy
///   .GetTransfer()                            // IRpc -> ZRpcImpl.GetTransfer()
///   .GetTransport()                           // ITransfer -> ZRpcTransfer.GetTransport()
///   .conn_                                    // ZTransport.conn_ (IL2CPP field -> property), @0x10
///   is ZTcpConnection ? .client_ : null       // UDP channels have no client_; @0x60
/// </code>
/// Every step is wrapped so a reflection failure (any null step, a UDP channel, a
/// zero native handle, or a throw) returns null and lets <see cref="TrySend"/>
/// fall back to the passive cache. We NEVER per-frame poll <c>ZRpcCtrl</c> (it
/// runs the net echo/Update loop — reflecting into it on a timer stalled the
/// ZServerTime singleton in a past bug); resolution only happens inside a
/// user-paced send.
///
/// ⚠️ We bind and invoke <c>GetRpcByProxyId</c> on the CONCRETE <c>ZRpcCtrl</c>
/// type, never through the <c>IRpcCtrl</c> interface — the
/// <c>ZRpcCtrl</c>→<c>IRpcCtrl</c> Il2CppInterop cast fails (the same trap that
/// broke the avatar SocialProxy). The returned <c>IRpc</c> is a concrete
/// <c>ZRpcImpl</c>; we keep resolving <c>GetTransfer()</c>/<c>GetTransport()</c>
/// off its runtime type via <see cref="InvokeNoArgCached"/>.
/// </summary>
internal sealed partial class PandaChatProbe
{
    // ChitChat Lua RPC proxy id (name "ChitChat"). GetRpcByProxyId resolves this
    // to the live IRpc by proxy-manager membership (seeded at login), so it
    // works at fresh login — no dependence on the lazy send-side channel cache.
    private const ulong ChitChatProxyId = 1321197368uL;

    // ---- one-shot metadata (types / members) ----
    // Resolved once on the first send; _activeMetaResolved gates re-resolution and
    // _activeMetaOk records whether the chain is structurally present. A structural
    // miss warns once and then never retries (the passive path still works).
    private bool _activeMetaResolved;
    private bool _activeMetaOk;
    private bool _activeUnavailableWarned;
    private bool _activeResolveSucceededLogged;

    private Type? _zRpcCtrlType;
    private Type? _zTcpConnectionType;
    private bool _zTcpConnTypeResolved;            // gates EnsureZTcpConnectionType re-resolution
    private MethodInfo? _miGetRpcByProxyId;        // ZRpcCtrl.GetRpcByProxyId(ulong) instance

    // Fire-once diagnostic set: each distinct silent null step of the active walk
    // (TryResolveLiveChatClient) is logged at most once so a retried send can't
    // spam the net loop. See <see cref="LiveResolveNull"/>.
    private readonly HashSet<string> _liveResolveNullStepsLogged = new();

    // ---- lazily cached accessors bound off the live concrete types ----
    // GetTransfer/GetTransport are declared on the concrete impls (ZRpcImpl /
    // ZRpcTransfer), reached through IRpc/ITransfer at runtime; conn_/client_ are
    // IL2CPP fields surfaced as properties. All cached after first success.
    private MethodInfo? _miGetTransfer;
    private MethodInfo? _miGetTransport;
    private PropertyInfo? _piConn;
    private PropertyInfo? _piClient;

    /// <summary>
    /// Resolve the live chat <c>ZTcpClient</c> from <c>ZRpcCtrl</c> on demand.
    /// Returns a handle-verified live client, or <c>null</c> on any failure
    /// (structural miss, singleton not up yet, UDP channel, zero handle, or a
    /// reflection throw) so <see cref="TrySend"/> can fall back to the passive
    /// cache. Never throws.
    /// </summary>
    private object? TryResolveLiveChatClient()
    {
        if (!EnsureActiveMetadata()) return null;

        try
        {
            // ZSingleton<ZRpcCtrl>.Instance (base-chain / FlattenHierarchy walk).
            // Not yet created pre-login -> null -> passive fallback.
            var rpcCtrl = StellarInterop.GetSingleton(_zRpcCtrlType);
            if (rpcCtrl is null) return LiveResolveNull("rpcCtrl");

            // Game's own by-proxy resolver: resolves by proxy-manager membership
            // (populated at login), so it works at fresh login — unlike the old
            // GetChannelTypeByProxyId path that depended on the lazy send cache.
            var rpc = _miGetRpcByProxyId!.Invoke(rpcCtrl, new object[] { ChitChatProxyId });
            if (rpc is null) return LiveResolveNull("GetRpcByProxyId");

            object? transfer = InvokeNoArgCached(rpc, "GetTransfer", ref _miGetTransfer);
            if (transfer is null) return LiveResolveNull("transfer");

            object? transport = InvokeNoArgCached(transfer, "GetTransport", ref _miGetTransport);
            if (transport is null) return LiveResolveNull("transport");

            object? conn = ReadMemberCached(transport, "conn_", ref _piConn);
            if (conn is null) return LiveResolveNull("conn");

            // A UDP channel's conn_ is a ZUdpConnection (no client_) — only the
            // TCP connection carries the ZTcpClient we can Send through.
            if (!_zTcpConnectionType!.IsInstanceOfType(conn))
                return LiveResolveNull($"conn-not-ZTcpConnection type={conn.GetType().FullName}");

            object? client = ReadMemberCached(conn, "client_", ref _piClient);
            if (client is null) return LiveResolveNull("client");

            // Belt-and-suspenders: never hand a freed native handle to Send.
            if (ReadNativeHandle(client) == IntPtr.Zero) return LiveResolveNull("zero-handle");

            if (!_activeResolveSucceededLogged)
            {
                _activeResolveSucceededLogged = true;
                _log.Info("[ChatProbe] resolved live chat client via GetRpcByProxyId");
            }
            return client;
        }
        catch (Exception ex)
        {
            WarnActiveUnavailableOnce($"{ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// One-shot resolution of the static metadata the live chain needs: the
    /// <c>ZRpcCtrl</c> / <c>ZTcpConnection</c> types plus the instance
    /// <c>GetRpcByProxyId(ulong)</c> method. On a structural miss it warns once and
    /// caches the failure so we never retry (the passive path remains the fallback).
    /// </summary>
    private bool EnsureActiveMetadata()
    {
        if (_activeMetaResolved) return _activeMetaOk;
        _activeMetaResolved = true;

        try
        {
            _zRpcCtrlType = StellarInterop.FindType("ZCode.ZRpc.ZRpcCtrl");
            // Resolve ZTcpConnection via the shared lazy resolver so the outbound
            // SendBytes capture (which needs the same type-check but not the
            // GetRpcByProxyId resolver) can reach it independently of this walk.
            _zTcpConnectionType = EnsureZTcpConnectionType();

            if (_zRpcCtrlType is not null)
            {
                // Bind the (ulong) overload explicitly on the concrete ZRpcCtrl —
                // never via IRpcCtrl (the Il2CppInterop interface cast fails).
                _miGetRpcByProxyId = _zRpcCtrlType.GetMethod(
                    "GetRpcByProxyId",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, types: new[] { typeof(ulong) }, modifiers: null);
            }

            _activeMetaOk = _zRpcCtrlType is not null
                            && _zTcpConnectionType is not null
                            && _miGetRpcByProxyId is not null;

            // One-shot metadata-resolution outcome (fires once — this body runs
            // once under _activeMetaResolved). Tells the next session which bound
            // members resolved so a silent active-walk null can be attributed.
            _log.Info(
                $"[ChatProbe] live-resolve metadata (ctrl={_zRpcCtrlType is not null}, conn={_zTcpConnectionType is not null}, " +
                $"getRpcByProxyId={_miGetRpcByProxyId is not null}) -> ok={_activeMetaOk}");

            if (!_activeMetaOk)
            {
                WarnActiveUnavailableOnce(
                    $"metadata incomplete (ctrl={_zRpcCtrlType is not null}, conn={_zTcpConnectionType is not null}, " +
                    $"getRpcByProxyId={_miGetRpcByProxyId is not null})");
            }

            return _activeMetaOk;
        }
        catch (Exception ex)
        {
            _activeMetaOk = false;
            WarnActiveUnavailableOnce($"{ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Resolve (first call) + invoke a no-arg method by name off the live object's
    /// concrete type, caching the <see cref="MethodInfo"/>. The IL2CPP interface
    /// returns (<c>IRpc</c>/<c>ITransfer</c>) hand us the concrete impl, so we bind
    /// the method on the concrete type. Returns null if unresolved.
    /// </summary>
    private static object? InvokeNoArgCached(object target, string methodName, ref MethodInfo? cache)
    {
        var mi = cache;
        if (mi is null)
        {
            mi = StellarInterop.FindMethod(target.GetType(), methodName, 0);
            if (mi is null) return null;
            cache = mi;
        }
        return mi.Invoke(target, null);
    }

    /// <summary>
    /// Read an IL2CPP field (surfaced as a property by Il2CppInterop) by name off
    /// the live object's concrete type, caching the accessor. <c>conn_</c> and
    /// <c>client_</c> have no C# getter of their own but project as readable
    /// properties. Returns null if unresolved.
    /// </summary>
    private static object? ReadMemberCached(object target, string memberName, ref PropertyInfo? cache)
    {
        var pi = cache;
        if (pi is null)
        {
            pi = StellarInterop.FindPropertyUp(target.GetType(), memberName);
            if (pi is null) return null;
            cache = pi;
        }
        return pi.GetValue(target);
    }

    /// <summary>
    /// Log the active-resolution-unavailable warning exactly once, then stay
    /// silent — the passive capture still handles sends, so this must not spam.
    /// </summary>
    private void WarnActiveUnavailableOnce(string why)
    {
        if (_activeUnavailableWarned) return;
        _activeUnavailableWarned = true;
        _log.Warning($"[ChatProbe] active chat client resolution unavailable — falling back to passive capture: {why}");
    }

    /// <summary>
    /// Lazily resolve the <c>ZCode.ZNet.ZTcpConnection</c> type, shared by the
    /// active <c>ZRpcCtrl</c> walk and the outbound <c>SendBytes</c> capture — both
    /// must type-check <c>conn_</c> before reading <c>client_</c> (a UDP channel's
    /// conn_ has no client_). Resolved INDEPENDENTLY of the active-walk metadata
    /// (<see cref="EnsureActiveMetadata"/>) so the outbound capture works even
    /// when <c>GetRpcByProxyId</c> can't be bound. Resolves at most once.
    /// </summary>
    private Type? EnsureZTcpConnectionType()
    {
        if (!_zTcpConnTypeResolved)
        {
            _zTcpConnTypeResolved = true;
            try
            {
                _zTcpConnectionType ??= StellarInterop.FindType("ZCode.ZNet.ZTcpConnection");
            }
            catch
            {
                // Leave null — both resolution paths then fall through to the
                // passive cache, which is the designed backstop.
            }
        }
        return _zTcpConnectionType;
    }

    /// <summary>
    /// Fire-once (per distinct <paramref name="step"/>) Info diagnostic marking
    /// WHERE <see cref="TryResolveLiveChatClient"/> bailed to null, so a session
    /// whose active walk silently returns null tells us exactly which step failed.
    /// Deduped by a set (not a counter) so a retried send can never spam the net
    /// loop. Returns null so a bail reads as <c>return LiveResolveNull("step")</c>.
    /// </summary>
    private object? LiveResolveNull(string step)
    {
        bool fire;
        lock (_liveResolveNullStepsLogged)
        {
            fire = _liveResolveNullStepsLogged.Add(step);
        }
        if (fire) _log.Info($"[ChatProbe] live-resolve null at: {step}");
        return null;
    }

    /// <summary>
    /// PRIMARY fresh-login capture entry (called from the <c>ZRpcImpl.SendBytes</c>
    /// PREFIX in <c>Send.Hooks.Rpc.cs</c>, strictly gated on the ChitChat proxy
    /// uuid): resolve the live <c>ZTcpClient</c> from the sending
    /// <paramref name="rpcImpl"/> and feed it into the SHARED passive-cache slot.
    /// The hook fires at login during the history fetch — before any inbound chat
    /// packet — so this is what actually closes the fresh-login send window.
    /// Never throws (the resolve + cache are both guarded).
    /// </summary>
    private void CaptureLiveChatClientFromOutbound(object? rpcImpl, uint callId)
    {
        if (rpcImpl is null) return;
        var client = TryResolveClientFromOutboundRpc(rpcImpl);
        if (client is null) return;
        CacheChatTcpClientFromOutbound(client, callId);
    }

    /// <summary>
    /// Resolve the live chat <c>ZTcpClient</c> from a <c>ZRpcImpl</c> that is
    /// CURRENTLY performing an outbound ChitChat send (the <c>SendBytes</c>
    /// <c>__instance</c>). Same transport chain as the tail of
    /// <see cref="TryResolveLiveChatClient"/> — <c>GetTransfer()</c> →
    /// <c>GetTransport()</c> → <c>conn_</c> → (type-checked <c>ZTcpConnection</c>)
    /// <c>client_</c> → <c>handle_ != 0</c> — but sourced from an RPC impl that is
    /// DEFINITELY live (it is mid-send), so there is no singleton lookup and no
    /// dependence on the <c>GetRpcByProxyId</c> resolver. Reuses the shared chain
    /// accessor caches (<see cref="_miGetTransfer"/> etc.). Returns null on any
    /// null step, a UDP channel, a zero handle, or a throw; never throws.
    /// </summary>
    private object? TryResolveClientFromOutboundRpc(object rpcImpl)
    {
        try
        {
            object? transfer = InvokeNoArgCached(rpcImpl, "GetTransfer", ref _miGetTransfer);
            if (transfer is null) return null;

            object? transport = InvokeNoArgCached(transfer, "GetTransport", ref _miGetTransport);
            if (transport is null) return null;

            object? conn = ReadMemberCached(transport, "conn_", ref _piConn);
            if (conn is null) return null;

            var tcpConnType = EnsureZTcpConnectionType();
            if (tcpConnType is null || !tcpConnType.IsInstanceOfType(conn)) return null;

            object? client = ReadMemberCached(conn, "client_", ref _piClient);
            if (client is null) return null;

            if (ReadNativeHandle(client) == IntPtr.Zero) return null;

            return client;
        }
        catch
        {
            // Never propagate across the IL2CPP boundary / the net send path.
            return null;
        }
    }
}
