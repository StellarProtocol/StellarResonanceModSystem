using System;
using System.Reflection;
using Stellar.Abstractions.Services;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Active, on-demand resolution of the <b>live</b> chat <c>ZTcpClient</c> for
/// <see cref="TrySend"/>. The passively-captured client (see
/// <c>Send.Connection.cs</c>) is only populated from inbound chat packets, so
/// after a reconnect sends fail-closed in the dead window until the next inbound
/// packet re-caches it. This partial closes that window by walking the game's RPC
/// controller to the socket on every send — there is no cache to go stale.
///
/// Chat is NOT a dedicated socket: the whole game multiplexes RPC over two
/// transports owned by <c>ZCode.ZRpc.ZRpcCtrl</c>. The ChitChat Lua proxy
/// (id 1321197368) picks its channel (Gateway vs World) at runtime, so we resolve
/// it via <c>ZRpcCtrl.GetChannelTypeByProxyId</c> rather than hard-coding it.
/// Live chain (all by reflection; verified against release_3.7 dump.cs):
/// <code>
/// ZRpcCtrl.Instance.GetRpc((ChannelType)ch)   // ZSingleton&lt;ZRpcCtrl&gt;.Instance, FlattenHierarchy
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
/// </summary>
internal sealed partial class PandaChatProbe
{
    // ChitChat Lua RPC proxy id (name "ChitChat"). GetChannelTypeByProxyId maps
    // this to a ChannelType (Gateway/World) once the runtime uuid2ChannelType_
    // map is built — so it is resolved live, never hard-coded.
    private const ulong ChitChatProxyId = 1321197368uL;

    // ---- one-shot metadata (types / static members) ----
    // Resolved once on the first send; _activeMetaResolved gates re-resolution and
    // _activeMetaOk records whether the chain is structurally present. A structural
    // miss warns once and then never retries (the passive path still works).
    private bool _activeMetaResolved;
    private bool _activeMetaOk;
    private bool _activeUnavailableWarned;
    private bool _activeResolveSucceededLogged;

    private Type? _zRpcCtrlType;
    private Type? _zTcpConnectionType;
    private Type? _channelTypeEnum;
    private MethodInfo? _miGetRpc;                 // ZRpcCtrl.GetRpc(ChannelType)
    private MethodInfo? _miGetChannelTypeByProxyId; // ZRpcCtrl.GetChannelTypeByProxyId(ulong) static

    // ---- lazily cached accessors bound off the live concrete types ----
    // GetTransfer/GetTransport are declared on the concrete impls (ZRpcImpl /
    // ZRpcTransfer), reached through IRpc/ITransfer at runtime; conn_/client_ are
    // IL2CPP fields surfaced as properties. All cached after first success.
    private MethodInfo? _miGetTransfer;
    private MethodInfo? _miGetTransport;
    private PropertyInfo? _piConn;
    private PropertyInfo? _piClient;

    // ---- cached resolved channel ----
    // GetChannelTypeByProxyId needs the runtime proxy map, so we only cache the
    // result once it yields a valid ChannelType (Gateway=0 / World=1); an early
    // send (pre-connect) that returns anything else leaves it unresolved to retry.
    private int _chatChannel = -1;

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
            if (rpcCtrl is null) return null;

            if (!TryResolveChatChannel(rpcCtrl, out int channel)) return null;

            var rpc = _miGetRpc!.Invoke(rpcCtrl, new[] { Enum.ToObject(_channelTypeEnum!, channel) });
            if (rpc is null) return null;

            object? transfer = InvokeNoArgCached(rpc, "GetTransfer", ref _miGetTransfer);
            if (transfer is null) return null;

            object? transport = InvokeNoArgCached(transfer, "GetTransport", ref _miGetTransport);
            if (transport is null) return null;

            object? conn = ReadMemberCached(transport, "conn_", ref _piConn);
            if (conn is null) return null;

            // A UDP channel's conn_ is a ZUdpConnection (no client_) — only the
            // TCP connection carries the ZTcpClient we can Send through.
            if (!_zTcpConnectionType!.IsInstanceOfType(conn)) return null;

            object? client = ReadMemberCached(conn, "client_", ref _piClient);
            if (client is null) return null;

            // Belt-and-suspenders: never hand a freed native handle to Send.
            if (ReadNativeHandle(client) == IntPtr.Zero) return null;

            if (!_activeResolveSucceededLogged)
            {
                _activeResolveSucceededLogged = true;
                _log.Info($"[ChatProbe] resolving live chat client on demand via ZRpcCtrl (channel={SafeChannelName(channel)})");
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
    /// <c>ZRpcCtrl</c> / <c>ZTcpConnection</c> / <c>ChannelType</c> types plus the
    /// <c>GetRpc(ChannelType)</c> and static <c>GetChannelTypeByProxyId(ulong)</c>
    /// methods. On a structural miss it warns once and caches the failure so we
    /// never retry (the passive path remains the fallback).
    /// </summary>
    private bool EnsureActiveMetadata()
    {
        if (_activeMetaResolved) return _activeMetaOk;
        _activeMetaResolved = true;

        try
        {
            _zRpcCtrlType = StellarInterop.FindType("ZCode.ZRpc.ZRpcCtrl");
            _zTcpConnectionType = StellarInterop.FindType("ZCode.ZNet.ZTcpConnection");
            _channelTypeEnum = StellarInterop.FindType("ZCode.ZRpc.ChannelType");

            if (_zRpcCtrlType is not null)
            {
                // GetRpc(ChannelType) is unique by (name, arity) on ZRpcCtrl —
                // GetRpcByProxyId has a different name, and the 0-arg GetRpc()
                // overloads live on the transfer/transport interfaces.
                _miGetRpc = StellarInterop.FindMethod(_zRpcCtrlType, "GetRpc", 1);

                // Bind the (ulong) overload explicitly: the IntPtr thunk of the
                // same name lives on the Lua-wrapper type ZCode_ZRpc_ZRpcCtrlWrap,
                // not here, but be precise regardless.
                _miGetChannelTypeByProxyId = _zRpcCtrlType.GetMethod(
                    "GetChannelTypeByProxyId",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static,
                    binder: null, types: new[] { typeof(ulong) }, modifiers: null);
            }

            _activeMetaOk = _zRpcCtrlType is not null
                            && _zTcpConnectionType is not null
                            && _channelTypeEnum is not null
                            && _miGetRpc is not null
                            && _miGetChannelTypeByProxyId is not null;

            if (!_activeMetaOk)
            {
                WarnActiveUnavailableOnce(
                    $"metadata incomplete (ctrl={_zRpcCtrlType is not null}, conn={_zTcpConnectionType is not null}, " +
                    $"enum={_channelTypeEnum is not null}, getRpc={_miGetRpc is not null}, getChannel={_miGetChannelTypeByProxyId is not null})");
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
    /// Resolve (and cache) the ChitChat channel via
    /// <c>ZRpcCtrl.GetChannelTypeByProxyId(ChitChatProxyId)</c>. Only caches a
    /// valid <c>ChannelType</c> (Gateway=0 / World=1); anything else (e.g. the
    /// proxy map not built yet pre-connect) leaves it unresolved so a later send
    /// retries. Returns false on failure.
    /// </summary>
    private bool TryResolveChatChannel(object rpcCtrl, out int channel)
    {
        channel = _chatChannel;
        if (channel == 0 || channel == 1) return true;

        var raw = _miGetChannelTypeByProxyId!.Invoke(rpcCtrl, new object[] { ChitChatProxyId });
        if (raw is null) return false;
        int resolved = Convert.ToInt32(raw);
        if (resolved != 0 && resolved != 1) return false; // map not ready / unknown

        _chatChannel = resolved;
        channel = resolved;
        return true;
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

    /// <summary>Map a resolved channel int to its <c>ChannelType</c> name for the log line.</summary>
    private string SafeChannelName(int channel)
    {
        try
        {
            if (_channelTypeEnum is not null)
            {
                var name = Enum.GetName(_channelTypeEnum, Enum.ToObject(_channelTypeEnum, channel));
                if (!string.IsNullOrEmpty(name)) return name!;
            }
        }
        catch { /* fall through to the numeric form */ }
        return channel == 0 ? "Gateway" : channel == 1 ? "World" : channel.ToString();
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
}
