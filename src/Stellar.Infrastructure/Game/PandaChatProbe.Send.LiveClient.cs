using System;
using System.Collections.Generic;
using System.Reflection;
using Il2CppInterop.Runtime.InteropTypes;
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
///   -> IRpc        .Cast&lt;ZRpcImpl&gt;()      .GetTransfer()   // ZRpcImpl.GetTransfer()   -> ITransfer
///   -> ITransfer   .Cast&lt;ZRpcTransfer&gt;()  .GetTransport()  // ZRpcTransfer.GetTransport()-> ITransport
///   -> ITransport  .Cast&lt;ZTransport&gt;()    .conn_           // ZTransport.conn_ @0x10    -> IConnection
///   -> IConnection .TryCast&lt;ZTcpConnection&gt;() .client_      // null = UDP/not-TCP;  @0x60 -> ZTcpClient
/// </code>
/// ⭐ Il2CppInterop hands every hop back wrapped as the INTERFACE CLR type
/// (<c>IRpc</c>/<c>ITransfer</c>/<c>IConnection</c>), NOT the concrete impl. A
/// cached <c>MethodInfo</c>/<c>PropertyInfo</c> resolved from (or invoked against)
/// that interface wrapper throws <c>TargetException: Object does not match target
/// type</c>, because the member is declared on the CONCRETE type. So at every hop
/// we <c>TryCast</c> the wrapper to its concrete il2cpp type FIRST and resolve the
/// next member off the known concrete <see cref="Type"/> (never
/// <c>object.GetType()</c>) — this is why live resolution never succeeded before
/// (2.25.5 fix), and why the shared accessor cache now only ever holds
/// concrete-type members and can't be cross-poisoned between the active walk and
/// the outbound capture. The <c>conn is ZTcpConnection</c> CLR check is likewise
/// replaced by <c>TryCast&lt;ZTcpConnection&gt;()</c> (null ⇒ UDP), since <c>is</c>
/// tests the wrapper's CLR type, not the il2cpp type.
///
/// Every step is wrapped so a reflection failure (any null step, a UDP channel, a
/// zero native handle, or a throw) returns null and lets <see cref="TrySend"/>
/// fall back to the passive cache. We NEVER per-frame poll <c>ZRpcCtrl</c> (it
/// runs the net echo/Update loop — reflecting into it on a timer stalled the
/// ZServerTime singleton in a past bug); resolution only happens inside a
/// user-paced send (active walk) or the outbound <c>SendBytes</c> hook (capture).
///
/// ⚠️ We bind and invoke <c>GetRpcByProxyId</c> on the CONCRETE <c>ZRpcCtrl</c>
/// type, never through the <c>IRpcCtrl</c> interface — the
/// <c>ZRpcCtrl</c>→<c>IRpcCtrl</c> Il2CppInterop cast fails (the same trap that
/// broke the avatar SocialProxy).
/// </summary>
internal sealed partial class PandaChatProbe
{
    // ChitChat Lua RPC proxy id (name "ChitChat"). GetRpcByProxyId resolves this
    // to the live IRpc by proxy-manager membership (seeded at login), so it
    // works at fresh login — no dependence on the lazy send-side channel cache.
    // NB: this is the PROXY id, distinct from the per-stub service_uuid on the
    // wire (e.g. 0x4EBFDF38) — do NOT compare it against the SendBytes uuid param.
    private const ulong ChitChatProxyId = 1321197368uL;

    // ---- one-shot metadata (types / members) ----
    // Resolved once on the first send; _activeMetaResolved gates re-resolution and
    // _activeMetaOk records whether the chain is structurally present. A structural
    // miss warns once and then never retries (the passive path still works).
    private bool _activeMetaResolved;
    private bool _activeMetaOk;
    private bool _activeUnavailableWarned;

    private Type? _zRpcCtrlType;
    private MethodInfo? _miGetRpcByProxyId;        // ZRpcCtrl.GetRpcByProxyId(ulong) instance

    // ---- concrete types the cast-aware transport walk hops through ----
    // Members are bound off THESE concrete types (never an interface wrapper's
    // GetType()) so the shared accessor cache can't be poisoned. Resolved once by
    // EnsureLiveChainTypes (reachable from the capture BEFORE the first send).
    private bool _liveChainTypesResolved;
    private Type? _zRpcImplType;        // ZCode.ZRpc.ZRpcImpl       (GetTransfer)
    private Type? _zRpcTransferType;    // ZCode.ZRpc.ZRpcTransfer   (GetTransport)
    private Type? _zTransportType;      // ZCode.ZRpc.ZTransport     (conn_)
    private Type? _zTcpConnectionType;  // ZCode.ZNet.ZTcpConnection (client_)

    // ---- lazily cached accessors bound off the concrete types above ----
    private MethodInfo? _miGetTransfer;
    private MethodInfo? _miGetTransport;
    private PropertyInfo? _piConn;
    private PropertyInfo? _piClient;

    // ---- reflection TryCast<T> plumbing (Il2CppObjectBase.TryCast<T>()) ----
    // Open generic cached once; closed instantiations cached per concrete type.
    private MethodInfo? _tryCastOpen;
    private readonly Dictionary<Type, MethodInfo> _tryCastClosed = new();

    // One-shot gate for the outbound-capture fail diagnostic (the capture hook can
    // fire several times at login / on a net thread, so this must not spam).
    private bool _captureFailLogged;

    /// <summary>
    /// Resolve the live chat <c>ZTcpClient</c> from <c>ZRpcCtrl</c> on demand.
    /// Returns a handle-verified live client, or <c>null</c> on any failure (and
    /// then exactly one <c>live-resolve ...</c> diagnostic was logged) so
    /// <see cref="TrySend"/> can fall back to the passive cache. Never throws.
    /// </summary>
    private object? TryResolveLiveChatClient()
    {
        // Metadata-incomplete is an outcome too (EnsureActiveMetadata logs WHY once).
        if (!EnsureActiveMetadata()) return LiveResolveNull("metadata");

        object? rpc;
        try
        {
            // ZSingleton<ZRpcCtrl>.Instance (FlattenHierarchy); not up pre-login.
            var rpcCtrl = StellarInterop.GetSingleton(_zRpcCtrlType);
            if (rpcCtrl is null) return LiveResolveNull("rpcCtrl");

            // Game's own by-proxy resolver — works at fresh login (proxy-manager
            // membership), unlike the old lazy GetChannelTypeByProxyId path.
            rpc = _miGetRpcByProxyId!.Invoke(rpcCtrl, new object[] { ChitChatProxyId });
            if (rpc is null) return LiveResolveNull("GetRpcByProxyId");
        }
        catch (Exception ex)
        {
            return LiveResolveNull($"exception ({ex.GetType().Name}: {ex.Message})");
        }

        var client = ResolveClientFromRpcObject(rpc, out var step);
        if (client is null) return LiveResolveNull(step);

        // Success is also an outcome — one line per attempt (this walk runs only
        // inside a user-paced TrySend, so one line per send is bounded).
        _log.Info("[ChatProbe] resolved live chat client via GetRpcByProxyId");
        return client;
    }

    /// <summary>
    /// Shared cast-aware transport walk for BOTH live-client paths. Given an
    /// <c>IRpc</c> wrapper (active walk) or the concrete <c>ZRpcImpl</c>
    /// <c>__instance</c> (outbound capture), re-wrap each Il2CppInterop
    /// INTERFACE-typed hop to its CONCRETE type with <see cref="TryCastIl2Cpp"/>
    /// and resolve every member off the known concrete <see cref="Type"/>. Returns
    /// a handle-verified <c>ZTcpClient</c>, or null with <paramref name="failStep"/>
    /// naming the hop that failed (so a bail still pinpoints the wrong type/step).
    /// Never throws.
    /// </summary>
    private object? ResolveClientFromRpcObject(object? rpcWrapper, out string failStep)
    {
        failStep = string.Empty;
        try
        {
            var impl = TryCastIl2Cpp(rpcWrapper, _zRpcImplType);
            if (impl is null) { failStep = "cast-ZRpcImpl"; return null; }

            var transfer = TryCastIl2Cpp(
                InvokeNoArgOn(impl, _zRpcImplType!, "GetTransfer", ref _miGetTransfer), _zRpcTransferType);
            if (transfer is null) { failStep = "transfer"; return null; }

            var transport = TryCastIl2Cpp(
                InvokeNoArgOn(transfer, _zRpcTransferType!, "GetTransport", ref _miGetTransport), _zTransportType);
            if (transport is null) { failStep = "transport"; return null; }

            // conn_ -> IConnection wrapper; TryCast<ZTcpConnection> is null for a
            // UDP channel (no client_). This REPLACES the unreliable `is` check.
            var conn = TryCastIl2Cpp(
                ReadMemberOn(transport, _zTransportType!, "conn_", ref _piConn), _zTcpConnectionType);
            if (conn is null) { failStep = "conn-not-ZTcpConnection"; return null; }

            var client = ReadMemberOn(conn, _zTcpConnectionType!, "client_", ref _piClient);
            if (client is null) { failStep = "client"; return null; }

            // Belt-and-suspenders: never hand a freed native handle to Send.
            if (ReadNativeHandle(client) == IntPtr.Zero) { failStep = "zero-handle"; return null; }

            return client;
        }
        catch (Exception ex)
        {
            failStep = $"exception ({ex.GetType().Name}: {ex.Message})";
            return null;
        }
    }

    /// <summary>
    /// One-shot resolution of the active-walk-only metadata (the <c>ZRpcCtrl</c>
    /// type + its <c>GetRpcByProxyId(ulong)</c> method) plus the shared transport
    /// chain types (<see cref="EnsureLiveChainTypes"/>). On a structural miss it
    /// warns once and caches the failure (the passive path remains the fallback).
    /// </summary>
    private bool EnsureActiveMetadata()
    {
        if (_activeMetaResolved) return _activeMetaOk;
        _activeMetaResolved = true;

        try
        {
            _zRpcCtrlType = StellarInterop.FindType("ZCode.ZRpc.ZRpcCtrl");

            if (_zRpcCtrlType is not null)
            {
                // Bind the (ulong) overload explicitly on the concrete ZRpcCtrl —
                // never via IRpcCtrl (the Il2CppInterop interface cast fails).
                _miGetRpcByProxyId = _zRpcCtrlType.GetMethod(
                    "GetRpcByProxyId",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance,
                    binder: null, types: new[] { typeof(ulong) }, modifiers: null);
            }

            bool chainOk = EnsureLiveChainTypes();
            _activeMetaOk = _zRpcCtrlType is not null && _miGetRpcByProxyId is not null && chainOk;

            // One-shot metadata-resolution outcome (this body runs once).
            _log.Info($"[ChatProbe] live-resolve metadata (ctrl={_zRpcCtrlType is not null}, getRpcByProxyId={_miGetRpcByProxyId is not null}, chainTypes={chainOk}) -> ok={_activeMetaOk}");

            if (!_activeMetaOk)
                WarnActiveUnavailableOnce($"metadata incomplete (ctrl={_zRpcCtrlType is not null}, getRpcByProxyId={_miGetRpcByProxyId is not null}, chainTypes={chainOk})");

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
    /// Resolve (once) the four CONCRETE types the cast-aware transport walk hops
    /// through. Shared by the active <c>ZRpcCtrl</c> walk and the outbound capture
    /// — the latter fires at login BEFORE the first send, so this must be reachable
    /// without <see cref="EnsureActiveMetadata"/>. Returns true only when all four
    /// resolved; a miss lets both walks fall through to the passive cache.
    /// </summary>
    private bool EnsureLiveChainTypes()
    {
        if (_liveChainTypesResolved) return AllChainTypesOk();
        _liveChainTypesResolved = true;
        try
        {
            _zRpcImplType       ??= StellarInterop.FindType("ZCode.ZRpc.ZRpcImpl");
            _zRpcTransferType   ??= StellarInterop.FindType("ZCode.ZRpc.ZRpcTransfer");
            _zTransportType     ??= StellarInterop.FindType("ZCode.ZRpc.ZTransport");
            _zTcpConnectionType ??= StellarInterop.FindType("ZCode.ZNet.ZTcpConnection");
        }
        catch
        {
            // Leave nulls — both walks then fall through to the passive cache.
        }
        return AllChainTypesOk();
    }

    private bool AllChainTypesOk() =>
        _zRpcImplType is not null && _zRpcTransferType is not null
        && _zTransportType is not null && _zTcpConnectionType is not null;

    /// <summary>
    /// Re-wrap <paramref name="obj"/> (an Il2CppInterop wrapper, typically returned
    /// as an INTERFACE CLR type) to the <paramref name="concrete"/> il2cpp type via
    /// <c>Il2CppObjectBase.TryCast&lt;T&gt;()</c> — null (never a throw) on a type
    /// mismatch, e.g. a UDP connection where we expect <c>ZTcpConnection</c>.
    /// Reflection-invoked because the target type is only known at runtime (same
    /// pattern as GameFreezeBackend's cached ECS cast). Returns null if obj is not
    /// an il2cpp wrapper, the concrete type/cast API can't be bound, or the cast
    /// fails.
    /// </summary>
    private object? TryCastIl2Cpp(object? obj, Type? concrete)
    {
        if (obj is not Il2CppObjectBase || concrete is null) return null;
        MethodInfo? closed;
        lock (_tryCastClosed)
        {
            if (!_tryCastClosed.TryGetValue(concrete, out closed))
            {
                _tryCastOpen ??= typeof(Il2CppObjectBase).GetMethod(nameof(Il2CppObjectBase.TryCast));
                closed = _tryCastOpen?.MakeGenericMethod(concrete);
                if (closed is not null) _tryCastClosed[concrete] = closed;
            }
        }
        return closed?.Invoke(obj, null);
    }

    /// <summary>
    /// Invoke a no-arg method resolved off the KNOWN concrete
    /// <paramref name="declaringType"/> (not <c>target.GetType()</c>), caching the
    /// <see cref="MethodInfo"/>. Resolving off the concrete type guarantees the
    /// cached member's declaring type matches the TryCast'd target — no
    /// cross-poisoning / <c>TargetException</c>. Returns null if unresolved.
    /// </summary>
    private static object? InvokeNoArgOn(object target, Type declaringType, string methodName, ref MethodInfo? cache)
    {
        cache ??= StellarInterop.FindMethod(declaringType, methodName, 0);
        return cache?.Invoke(target, null);
    }

    /// <summary>
    /// Read an IL2CPP field (surfaced as a property by Il2CppInterop) resolved off
    /// the KNOWN concrete <paramref name="declaringType"/>, caching the accessor.
    /// <c>conn_</c>/<c>client_</c> have no C# getter of their own but project as
    /// readable properties. Returns null if unresolved.
    /// </summary>
    private static object? ReadMemberOn(object target, Type declaringType, string memberName, ref PropertyInfo? cache)
    {
        cache ??= StellarInterop.FindPropertyUp(declaringType, memberName);
        return cache?.GetValue(target);
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
    /// Info diagnostic marking WHERE <see cref="TryResolveLiveChatClient"/> bailed
    /// to null (the <paramref name="step"/>), returning null so a bail reads as
    /// <c>return LiveResolveNull("step")</c>. One line PER send attempt (the walk
    /// runs only inside a user-paced <see cref="TrySend"/>, so this is bounded) —
    /// no attempt is ever silently blank.
    /// </summary>
    private object? LiveResolveNull(string step)
    {
        _log.Info($"[ChatProbe] live-resolve null at: {step}");
        return null;
    }

    /// <summary>
    /// PRIMARY fresh-login capture entry (called from the <c>ZRpcImpl.SendBytes</c>
    /// PREFIX in <c>Send.Hooks.Rpc.cs</c> for every ChitChat outbound call): resolve
    /// the live <c>ZTcpClient</c> from the sending <paramref name="rpcImpl"/>
    /// (<c>__instance</c>) via the shared cast-aware walk and feed it into the
    /// SHARED passive-cache slot. The hook fires at login during the history fetch —
    /// before any inbound chat packet — so this is what actually closes the
    /// fresh-login send window. On failure logs the fail step once (fire-once: the
    /// hook can fire several times at login / on a net thread). Never throws.
    /// </summary>
    private void CaptureLiveChatClientFromOutbound(object? rpcImpl, uint callId)
    {
        if (rpcImpl is null) return;
        if (!EnsureLiveChainTypes()) { LogCaptureFailOnce("chain-types"); return; }

        var client = ResolveClientFromRpcObject(rpcImpl, out var step);
        if (client is null) { LogCaptureFailOnce(step); return; }

        CacheChatTcpClientFromOutbound(client, callId);
    }

    private void LogCaptureFailOnce(string step)
    {
        if (_captureFailLogged) return;
        _captureFailLogged = true;
        _log.Info($"[ChatProbe] outbound capture null at: {step}");
    }
}
