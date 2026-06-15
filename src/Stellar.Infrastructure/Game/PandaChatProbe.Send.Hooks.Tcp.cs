using System;
using System.Reflection;
using HarmonyLib;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaChatProbe
{
    /// <summary>
    /// Locate <c>ZTcpConnection.OnData(ReadOnlySpan&lt;byte&gt;, ZTcpClient)</c>
    /// purely to read out its second parameter type (ZTcpClient), then resolve +
    /// patch <c>ZTcpClient.Send(ReadOnlySpan&lt;byte&gt;)</c>. The recv-side hook
    /// itself lives on <see cref="PandaWireTap"/>; we only need OnData here as
    /// a reflection anchor for the ZTcpClient type.
    /// </summary>
    private void ResolveTcpClientSendFromConnection(Type? tcpConnType)
    {
        if (tcpConnType is null)
        {
            _log.Warning("[ChatProbe] ZTcpConnection not found; ZTcpClient.Send not resolved");
            return;
        }

        MethodInfo[] methods;
        try
        {
            methods = tcpConnType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] GetMethods({tcpConnType.FullName}) threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var onData = FindOnDataOverload(methods);
        if (onData is null)
        {
            _log.Warning("[ChatProbe] OnData(ReadOnlySpan<byte>, ZTcpClient) not found; ZTcpClient.Send not resolved");
            return;
        }

        try
        {
            var onDataParams = onData.GetParameters();
            if (onDataParams.Length >= 2)
            {
                var tcpClientType = onDataParams[1].ParameterType;
                ResolveTcpClientSend(tcpClientType);
            }
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] ZTcpClient.Send resolution threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Pick the <c>OnData(ReadOnlySpan&lt;byte&gt;, ZTcpClient)</c> overload — reflection anchor for the ZTcpClient type. Mirrors <see cref="MatchSendOverload"/>.</summary>
    private static MethodInfo? FindOnDataOverload(MethodInfo[] methods)
    {
        foreach (var m in methods)
        {
            if (m.Name != "OnData") continue;
            ParameterInfo[] ps;
            try { ps = m.GetParameters(); }
            catch { continue; }
            if (ps.Length != 2) continue;
            if (ps[0].ParameterType?.Name != "ReadOnlySpan`1") continue;
            return m;
        }
        return null;
    }

    /// <summary>
    /// Locate <c>ZTcpClient.Send(ReadOnlySpan&lt;byte&gt;)</c> on the supplied
    /// type. Writes <see cref="_tcpClientSend"/> on success and installs the
    /// <see cref="OnTcpClientSend"/> PREFIX so we can correlate outbound
    /// ChitChat Calls with Returns. Logs the chosen overload's parameter type
    /// so the log doubles as a recon record.
    /// </summary>
    private void ResolveTcpClientSend(Type tcpClientType)
    {
        MethodInfo[] methods;
        try
        {
            methods = tcpClientType.GetMethods(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] GetMethods({tcpClientType.FullName}) for Send threw: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        var best = MatchSendOverload(methods);
        if (best is null)
        {
            _log.Warning($"[ChatProbe] no single-arg Send method found on {tcpClientType.FullName}");
            return;
        }

        _tcpClientSend = best;
        var paramTypeName = best.GetParameters()[0].ParameterType.FullName ?? best.GetParameters()[0].ParameterType.Name;
        _log.Info($"[ChatProbe] resolved {tcpClientType.FullName}.Send({paramTypeName})");

        InstallTcpClientSendPrefix(tcpClientType, best);
    }

    /// <summary>
    /// Walk <paramref name="methods"/> and pick the best single-arg
    /// <c>Send</c> overload. Preferred: the <c>ReadOnlySpan&lt;byte&gt;</c>
    /// (IL2CPP-projected <c>ReadOnlySpan`1</c>) shape used by BPSR's outbound
    /// dispatch. Falls back to any other single-arg <c>Send</c> so the log at
    /// least surfaces what's available.
    /// </summary>
    private static MethodInfo? MatchSendOverload(MethodInfo[] methods)
    {
        MethodInfo? best = null;
        foreach (var m in methods)
        {
            if (m.Name != "Send") continue;
            var ps = m.GetParameters();
            if (ps.Length != 1) continue;

            // Prefer the ReadOnlySpan<byte> overload. The IL2CPP-projected
            // shape names it "ReadOnlySpan`1" — match by name to be tolerant
            // of either the managed type or the IL2CPP wrapper type.
            var pt = ps[0].ParameterType;
            if (pt?.Name == "ReadOnlySpan`1")
            {
                best = m;
                break;
            }
            // Hold a non-Span single-arg Send as fallback; we'd still need to
            // figure out how to drive it, but at least the log surfaces it.
            best ??= m;
        }
        return best;
    }

    /// <summary>
    /// Install the <see cref="OnTcpClientSend"/> HarmonyX PREFIX on the chosen
    /// <c>Send</c> overload so we observe every outbound packet before
    /// transmission. Used to capture ChitChat call_ids for Return correlation
    /// (the only path that gives us chat-Return identification — Returns don't
    /// carry service_uuid in their wire header).
    /// </summary>
    private void InstallTcpClientSendPrefix(Type tcpClientType, MethodInfo send)
    {
        var sendPrefix = typeof(PandaChatProbe).GetMethod(
            nameof(OnTcpClientSend), BindingFlags.Static | BindingFlags.NonPublic);
        if (sendPrefix is null)
        {
            _log.Warning("[ChatProbe] OnTcpClientSend prefix method missing (build error)");
            return;
        }
        try
        {
            _harmony.Patch(send, prefix: new HarmonyMethod(sendPrefix));
            _log.Info($"[ChatProbe] patched send dispatch (PREFIX): {tcpClientType.FullName}.Send");
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] failed to patch {tcpClientType.FullName}.Send: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// HarmonyX PREFIX on ZTcpClient.Send(ReadOnlySpan&lt;byte&gt;). Fires
    /// on the thread that originated the outbound RPC (game thread for Lua-issued
    /// chat sends; our thread for plugin-initiated sends via TrySend).
    ///
    /// Hot-path constraints — identical to OnTcpDataReceived:
    ///   1. Zero allocation on the non-chat path (single ToArray copy is the cost
    ///      we pay before the filter; non-chat filter exits before any other work).
    ///   2. Never throws across the IL2CPP boundary.
    ///   3. Bounded dictionary growth via MaxPendingChatCalls cap.
    /// </summary>
    private static void OnTcpClientSend(object?[] __args)
    {
        var probe = Instance;
        if (probe is null) return;

        LogFirstSendPrefixFire(probe, __args);

        if (__args is null || __args.Length < 1) return;
        var arg0 = __args[0];
        if (arg0 is null) return;

        try
        {
            var bytes = CoerceIl2CppByteArray(probe, arg0);
            if (bytes is null || bytes.Length < 22) return; // [size:4][flags:2][svc:8][stub:4][call:4][method:4] = 26; need at least 22 for Call header less method_id

            TryRecordOutboundChitChatCall(probe, bytes);
        }
        catch (Exception ex)
        {
            Instance?._log.Warning($"[ChatProbe] Send prefix threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Once-per-process diagnostic confirming HarmonyX is invoking the Send prefix on <c>ZTcpClient.Send</c>. Gated by <see cref="_firstSendPrefixFiredLogged"/>.</summary>
    private static void LogFirstSendPrefixFire(PandaChatProbe probe, object?[]? __args)
    {
        if (System.Threading.Interlocked.Exchange(ref _firstSendPrefixFiredLogged, 1) != 0) return;

        int argCount = __args?.Length ?? -1;
        string arg0Type = "<null>";
        if (__args is not null && __args.Length >= 1 && __args[0] is not null)
        {
            arg0Type = __args[0]!.GetType().FullName ?? __args[0]!.GetType().Name;
        }
        probe._log.Info($"[ChatProbe] Send PREFIX first invocation: argCount={argCount} arg0Type={arg0Type}");
    }

    /// <summary>Parse the outbound packet's Call header; for ChitChat Calls record the <c>call_id</c> in <see cref="_pendingChitChatCallIds"/> (bounded) and advance the outbound counter. Returns <c>true</c> on recorded ChitChat Call.</summary>
    private static bool TryRecordOutboundChitChatCall(PandaChatProbe probe, byte[] bytes)
    {
        // Wire layout per BPSR-B Packet.create_call_packet:
        //   [size:u32 BE][flags:u16 BE][service_uuid:u64 BE][stub_id:u32 BE]
        //   [call_id:u32 BE][method_id:u32 BE][payload]
        ushort flags = (ushort)((bytes[4] << 8) | bytes[5]);
        ushort msgType = (ushort)(flags & 0x7FFF);
        if (msgType != ZprotoMsgTypeIdCall) return false; // only correlate Calls

        // service_uuid low 32 bits at offset 10..13 (high 32 are always 0 for
        // chat service ids per BPSR-B's chitchat_method_id catalog).
        uint svcLow =
            ((uint)bytes[10] << 24) | ((uint)bytes[11] << 16)
            | ((uint)bytes[12] << 8) | bytes[13];
        if (svcLow != ChitChatServiceUuid) return false;

        uint callId =
            ((uint)bytes[18] << 24) | ((uint)bytes[19] << 16)
            | ((uint)bytes[20] << 8) | bytes[21];

        // Bounded insert — if the game (or a misbehaving plugin) is issuing
        // chat Calls faster than Returns arrive, drop excess to prevent
        // unbounded dict growth. Worst case: we miss a few history batches.
        if (probe._pendingChitChatCallIds.Count < MaxPendingChatCalls)
        {
            probe._pendingChitChatCallIds.TryAdd(callId, 0);
        }

        // Keep our outbound-send counter ahead of observed game call_ids so
        // our plugin sends don't collide with the game's. Cheap CAS loop.
        probe.ObserveCallId(callId);

        if (!probe._firstSendObservedLogged)
        {
            probe._firstSendObservedLogged = true;
            probe._log.Info($"[ChatProbe] first ChitChat outbound Call observed: callId={callId} packetBytes={bytes.Length}");
        }
        return true;
    }

    /// <summary>
    /// Unwrap the IL2CPP <c>ReadOnlySpan&lt;byte&gt;</c> argument boxed into
    /// <c>__args[0]</c> by HarmonyX into a managed <c>byte[]</c>. Uses the
    /// shared <see cref="Il2CppSpanCoercion"/> one-shot reflection lookup so
    /// the recv + send paths share the same resolved
    /// <c>ToArray()</c> MethodInfo. Returns <c>null</c> if the extractor
    /// cannot be resolved or invocation throws — caller must skip silently.
    /// </summary>
    private static byte[]? CoerceIl2CppByteArray(PandaChatProbe probe, object arg0)
    {
        // Same extraction strategy as the receive path: ToArray() unwraps
        // the IL2CPP ReadOnlySpan<byte> into a managed byte[] (the indexer
        // is broken under HarmonyX boxing — verified empirically). The
        // shared resolver lives on PandaWireTap so the recv + send paths
        // share the same one-shot reflection lookup.
        if (Il2CppSpanCoercion.SpanToArrayMethod is null
            && System.Threading.Interlocked.CompareExchange(ref Il2CppSpanCoercion.SpanExtractorResolved, 1, 0) == 0)
        {
            Il2CppSpanCoercion.ResolveSpanExtractor(probe._log, arg0.GetType());
        }
        var toArray = Il2CppSpanCoercion.SpanToArrayMethod;
        if (toArray is null) return null;

        object? rawArr;
        try { rawArr = toArray.Invoke(arg0, null); }
        catch { return null; }
        if (rawArr is null) return null;

        return Il2CppSpanCoercion.CoerceToByteArray(rawArr);
    }
}
