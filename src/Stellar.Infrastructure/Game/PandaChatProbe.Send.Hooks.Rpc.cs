using System;
using System.Reflection;
using HarmonyLib;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaChatProbe
{
    // One-shot: dump the raw outbound ChitChat uuid once so the real value is on
    // record. The outbound SendBytes uuid carries NON-ZERO high 32 bits (it is a
    // per-stub id, not the bare proxy id), so only the low-32 == ChitChatServiceUuid
    // compare identifies a ChitChat call — the retired fullUuid==ChitChatProxyId
    // gate never matched and the capture never ran.
    private bool _outboundUuidLogged;

    /// <summary>
    /// Resolve + patch the transport-agnostic outbound RPC framing entry
    /// <c>ZCode.ZRpc.ZRpcImpl.SendBytes(ulong uuid, uint stubId, uint callId,
    /// uint methodId, byte[] pbData, uint pbSize, bool framePack) -&gt; ZRpcError</c>
    /// with the <see cref="OnRpcSendBytes"/> PREFIX. This is where the login
    /// history-channel correlation is CAPTURED (2.25.2 fix).
    ///
    /// Why here and not <c>ZTcpClient.Send</c> (the 2.25.1 site): the game's own
    /// outbound RPC never passes through the 1-arg
    /// <c>ZTcpClient.Send(ReadOnlySpan&lt;byte&gt;)</c> — that overload is a
    /// plugin/helper entry, so the 2.25.1 prefix on it fired 0 times for game
    /// traffic and nothing was ever stored (every login history batch resolved
    /// Unknown). Lua RPCs — ChitChat chat sends AND the GetChipChatRecords history
    /// fetch — go <c>ZRpcImpl.ProxyCall(…byte[]…)</c> → <c>ProxyCallInternal</c> →
    /// <c>SendBytes</c> → transport, and the World channel can be TCP OR UDP/KCP
    /// (<c>ZUdpClient.Send</c>). <c>SendBytes</c> sits ABOVE that TCP/UDP split, is
    /// public + cold (<c>PosingSendTap</c> already patches it in release_3.7 and
    /// proved it fires), and carries BOTH the call_id (param) and the raw
    /// pre-framing protobuf body (param) in one invocation — so we read the
    /// requested channel straight off the body argument (the proven pre-2.25.1
    /// source) AND key it by call_id with no cross-site/thread correlation needed.
    /// No <c>in</c>/<c>ref</c> struct param, so no trampoline crash.
    /// </summary>
    private void ResolveRpcSendHook(Type? rpcImplType)
    {
        if (rpcImplType is null)
        {
            _log.Warning("[ChatProbe] ZRpcImpl not resolved; outbound history-channel capture disabled");
            return;
        }

        var sendBytes = MatchSendBytesMethod(rpcImplType);
        if (sendBytes is null)
        {
            _log.Warning($"[ChatProbe] SendBytes(ulong,uint,uint,uint,byte[],uint,bool) not found on {rpcImplType.FullName}; history-channel capture disabled");
            return;
        }

        var prefix = typeof(PandaChatProbe).GetMethod(
            nameof(OnRpcSendBytes), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null)
        {
            _log.Warning("[ChatProbe] OnRpcSendBytes prefix method missing (build error)");
            return;
        }

        try
        {
            _harmony.Patch(sendBytes, prefix: new HarmonyMethod(prefix));
            var sig = string.Join(",", Array.ConvertAll(sendBytes.GetParameters(), p => p.ParameterType.Name));
            _log.Info($"[ChatProbe] patched outbound RPC (PREFIX): {rpcImplType.FullName}.SendBytes({sig})");
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] failed to patch {rpcImplType.FullName}.SendBytes: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Pick the <c>SendBytes(ulong uuid, uint stubId, uint callId, uint methodId,
    /// byte[] pbData, uint pbSize, bool framePack)</c> overload: name match, 7
    /// parameters, first param a 64-bit unsigned (the service uuid). The dump +
    /// <c>PosingSendTap</c> recon confirm exactly one such overload on
    /// <c>ZRpcImpl</c> in release_3.7.
    /// </summary>
    private static MethodInfo? MatchSendBytesMethod(Type rpcImplType)
    {
        foreach (var m in rpcImplType.GetMethods(AnyInstance))
        {
            if (m.Name != "SendBytes") continue;
            if (m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            if (ps.Length != 7) continue;
            var p0 = ps[0].ParameterType.Name;
            if (p0 != "UInt64" && p0 != "ulong") continue;
            return m;
        }
        return null;
    }

    /// <summary>
    /// HarmonyX PREFIX on <c>ZRpcImpl.SendBytes</c>. Fires on the thread that
    /// issued the outbound RPC (the Unity main thread for Lua-issued chat/history).
    /// For ChitChat Calls: record the call_id for Return correlation; for a
    /// GetChipChatRecords Call additionally capture the requested channel_type
    /// keyed by that call_id; and — the fresh-login send fix — resolve the live
    /// chat <c>ZTcpClient</c> off <c>__instance</c> (the <c>ZRpcImpl</c> doing the
    /// send) and feed the shared send cache. Hot-path safe: a single service-uuid
    /// compare rejects all non-chat RPC before any further work; never throws
    /// across the IL2CPP boundary.
    ///
    /// <c>__instance</c> is injected by HarmonyX as the receiver (the live
    /// <c>ZRpcImpl</c>); for an instance method it is non-null. <c>__args</c>
    /// layout (params only; no <c>this</c>) — verified by
    /// <c>PosingSendTap</c> (UuidArg=0, MethodArg=3) and the dump:
    /// [0]=uuid(ulong) [1]=stubId [2]=callId [3]=methodId [4]=pbData(byte[]) [5]=pbSize [6]=framePack.
    /// </summary>
    private static void OnRpcSendBytes(object? __instance, object?[] __args)
    {
        var probe = Instance;
        if (probe is null) return;

        try
        {
            if (__args is null || __args.Length < 7) return;

            LogFirstRpcSendBytesFire(probe);

            var uuidObj = __args[0];
            var callIdObj = __args[2];
            var methodIdObj = __args[3];
            if (uuidObj is null || callIdObj is null || methodIdObj is null) return;

            // One service-uuid compare (low 32 bits — chat ids fit in uint, high
            // half is 0) rejects all non-chat RPC before any allocation/parse.
            ulong fullUuid = Convert.ToUInt64(uuidObj);
            if (unchecked((uint)fullUuid) != ChitChatServiceUuid) return;

            probe.ProcessOutboundChitChatSend(
                __instance, fullUuid,
                Convert.ToUInt32(callIdObj), Convert.ToUInt32(methodIdObj), __args);
        }
        catch (Exception ex)
        {
            Instance?._log.Warning($"[ChatProbe] SendBytes prefix threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Handle a confirmed outbound ChitChat Call (the low-32 service-uuid gate
    /// already passed): record the <paramref name="callId"/> for Return
    /// correlation; on the chat proxy uuid capture the live <c>ZTcpClient</c> off
    /// <paramref name="rpcImpl"/> (the fresh-login send fix); and for
    /// <c>GetChipChatRecords</c> capture the per-request history channel_type.
    /// Split out of <see cref="OnRpcSendBytes"/> to keep that prefix within the
    /// method-LoC budget. Runs on the sending thread; never throws (the caller's
    /// try/catch is the boundary guard).
    /// </summary>
    private void ProcessOutboundChitChatSend(
        object? rpcImpl, ulong fullUuid, uint callId, uint methodId, object?[] args)
    {
        RecordChitChatOutboundCall(callId, methodId);

        if (!_outboundUuidLogged)
        {
            _outboundUuidLogged = true;
            _log.Info($"[ChatProbe] outbound ChitChat uuid=0x{fullUuid:X16} (svcLow matches)");
        }

        // PRIMARY fresh-login fix: this rpcImpl (ZRpcImpl) is mid-send on the chat
        // connection, so it IS the live chat RPC — resolve its ZTcpClient off the
        // sender and feed the SAME cache slot the passive inbound capture writes
        // (Send.Connection.cs). The hook fires at login during the
        // GetChipChatRecords history fetch, BEFORE any inbound chat packet, so it
        // populates the live client and closes the fresh-login send window that
        // passive-only capture left open. The low-32 == ChitChatServiceUuid gate
        // (already passed in OnRpcSendBytes) is the proven identifier for a
        // ChitChat outbound call; the outbound uuid carries non-zero high bits, so
        // there is NO extra fullUuid==ChitChatProxyId check — capture runs for
        // every ChitChat outbound call.
        CaptureLiveChatClientFromOutbound(rpcImpl, callId);

        // Only GetChipChatRecords carries a per-request history channel_type.
        if (methodId == GetChipChatRecordsMethodId)
        {
            CaptureHistoryChannelFromSendArgs(callId, args[4], args[5]);
        }
    }

    /// <summary>
    /// Record a ChitChat outbound <paramref name="callId"/> for Return
    /// correlation (bounded insert — feeds the one-shot correlated-Return
    /// diagnostic) and keep our own outbound counter ahead of the game's observed
    /// ids. Emits the first-observed one-shot.
    /// </summary>
    private void RecordChitChatOutboundCall(uint callId, uint methodId)
    {
        if (_pendingChitChatCallIds.Count < MaxPendingChatCalls)
        {
            _pendingChitChatCallIds.TryAdd(callId, 0);
        }
        ObserveCallId(callId);

        if (!_firstSendObservedLogged)
        {
            _firstSendObservedLogged = true;
            _log.Info($"[ChatProbe] first ChitChat outbound Call observed (SendBytes): callId={callId} methodId={methodId}");
        }
    }

    /// <summary>
    /// Coerce the <c>SendBytes</c> <c>pbData</c>/<c>pbSize</c> args to a managed
    /// byte[] and capture the history channel for <paramref name="callId"/>.
    /// pbData arrives as the IL2CPP projection of byte[] (Il2CppStructArray) under
    /// HarmonyX boxing — coerced the same way the retired ProxyCall hook did.
    /// </summary>
    private void CaptureHistoryChannelFromSendArgs(uint callId, object? pbDataObj, object? pbSizeObj)
    {
        if (pbDataObj is null) return;
        var body = pbDataObj as byte[] ?? Il2CppSpanCoercion.CoerceToByteArray(pbDataObj);
        if (body is null) return;
        uint bodySize = pbSizeObj is { } sizeObj ? Convert.ToUInt32(sizeObj) : 0u;
        TryCaptureHistoryChannel(callId, body, bodySize);
    }

    /// <summary>
    /// Once-per-process confirmation that HarmonyX is invoking the SendBytes
    /// PREFIX — fires on the very first outbound RPC (chat or not), so it proves
    /// the capture hook is live even before any chat/history request. Gated by
    /// <see cref="_firstRpcSendBytesFiredLogged"/>.
    /// </summary>
    private static void LogFirstRpcSendBytesFire(PandaChatProbe probe)
    {
        if (System.Threading.Interlocked.Exchange(ref _firstRpcSendBytesFiredLogged, 1) != 0) return;
        probe._log.Info("[ChatProbe] SendBytes PREFIX first invocation (outbound RPC framing hook live)");
    }

    /// <summary>
    /// Record the requested channel for an outbound <c>GetChipChatRecords</c> Call,
    /// keyed by its <paramref name="callId"/>, so
    /// <see cref="ProcessChitChatReturnPayload"/> can attribute the matching
    /// history Return to the right channel. Parses <c>channel_type</c> straight out
    /// of the raw request body (<paramref name="body"/>, sliced to
    /// <paramref name="bodySize"/>) — the SAME pre-framing, uncompressed protobuf
    /// the game hands to <c>SendBytes</c> (compression + the wire header are added
    /// downstream), so there is no header-offset math and no zstd concern. This is
    /// the proven pre-2.25.1 channel source; what changed is that we now also have
    /// the call_id at this site, so keying is correct under empty batches,
    /// out-of-order Returns, and dropped sends (it replaces both the old positional
    /// FIFO and the 2.25.1 framed-byte parse that never fired). Bounds the map + the
    /// companion order queue at <see cref="MaxPendingChatCalls"/> by draining the
    /// oldest ids on overflow (a stale id a Return already removed drains as a
    /// harmless no-op <c>TryRemove</c>).
    /// </summary>
    private void TryCaptureHistoryChannel(uint callId, byte[] body, uint bodySize)
    {
        // Respect pbSize in case pbData is a larger pooled buffer; the envelope's
        // length-delimited field already bounds the inner read, so this is a guard.
        int len = bodySize > 0 && bodySize <= (uint)body.Length ? (int)bodySize : body.Length;

        // Also reads target_id (field 5): a private (channel 5) history fetch names its peer ONLY in the
        // request — the reply has no peer — so we key it by call_id beside the channel.
        if (!WireProtocol.TryParseGetChipChatRecordsRequest(
                new ReadOnlySpan<byte>(body, 0, len), out int channelType, out long peerId)) return;
        if (channelType <= 0) return;

        _historyChannelByCallId[callId] = channelType;
        if (peerId != 0) _historyPeerByCallId[callId] = peerId;
        _historyChannelCallIdOrder.Enqueue(callId);

        // Keep the order queue (and thus the maps) bounded. Evicts oldest-first:
        // a stale id (already removed by its Return) is a no-op TryRemove; a live
        // id past the cap is a never-returned call we drop so it can't grow us.
        while (_historyChannelCallIdOrder.Count > MaxPendingChatCalls
               && _historyChannelCallIdOrder.TryDequeue(out var oldest))
        {
            _historyChannelByCallId.TryRemove(oldest, out _);
            _historyPeerByCallId.TryRemove(oldest, out _);
        }

        if (!_firstHistoryChannelCapturedLogged)
        {
            _firstHistoryChannelCapturedLogged = true;
            _log.Info($"[ChatProbe] first GetChipChatRecords channel captured: callId={callId} channelType={channelType} (wire={WireProtocol.MapWireChannel(channelType)})");
        }
    }
}
