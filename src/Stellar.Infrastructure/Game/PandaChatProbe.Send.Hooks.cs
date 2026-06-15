using System;
using System.Reflection;
using HarmonyLib;
using Stellar.Wire;
using Stellar.Application.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// HarmonyX hook installation and PREFIX handlers for <see cref="PandaChatProbe"/>'s
/// send path:
/// <list type="bullet">
/// <item><see cref="TryHookProxyCallForChannelCapture"/> — patches
///   <c>ZRpcImpl.ProxyCall</c> so we can record the channel_type field on
///   outbound history requests.</item>
/// <item><see cref="ResolveTcpClientSendFromConnection"/> +
///   <see cref="ResolveTcpClientSend"/> — locate
///   <c>ZTcpClient.Send(ReadOnlySpan&lt;byte&gt;)</c> reflectively and install
///   a PREFIX so outbound ChitChat call_ids can be tracked for Return
///   correlation.</item>
/// <item><see cref="OnProxyCall"/> + <see cref="OnTcpClientSend"/> — the
///   PREFIX bodies themselves.</item>
/// </list>
/// </summary>
internal sealed partial class PandaChatProbe
{
    /// <summary>
    /// Install a HarmonyX PREFIX on <c>ZCode.ZRpc.ZRpcImpl.ProxyCall(...)</c>
    /// to capture <c>channel_type</c> from outbound <c>ChitChat.GetChipChatRecords</c>
    /// requests. Walks all <c>ProxyCall</c> overloads on the type — there are
    /// usually two (IBufferMessage body and byte[] body); we patch the byte-array
    /// one since the Lua chat path uses it (the IBufferMessage overload caused
    /// the prior crash).
    /// </summary>
    private void TryHookProxyCallForChannelCapture(Type? rpcImplType)
    {
        if (rpcImplType is null) return;

        var prefix = typeof(PandaChatProbe).GetMethod(
            nameof(OnProxyCall), BindingFlags.Static | BindingFlags.NonPublic);
        if (prefix is null) return;

        var target = MatchProxyCallOverload(rpcImplType);
        if (target is null)
        {
            _log.Warning("[ChatProbe] no byte-array ProxyCall overload found — channel resolution disabled (see overload list above)");
            return;
        }

        try
        {
            _harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            var ps = target.GetParameters();
            _log.Info($"[ChatProbe] patched ProxyCall PREFIX: {rpcImplType.FullName}.ProxyCall(args={ps.Length}, body={ps[2].ParameterType.Name})");
        }
        catch (Exception ex)
        {
            _log.Warning($"[ChatProbe] failed to patch ProxyCall: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Enumerate every <c>ProxyCall</c> overload, log its signature for recon,
    /// and return the first match for the byte-array body shape
    /// (<c>(*, uint, byte[]|*ArrayBase*|*StructArray*, ...)</c>) — that's the
    /// overload the Lua chat path takes for GetChipChatRecords. The
    /// IBufferMessage overload is intentionally skipped (crashed on the prior
    /// attempt).
    /// </summary>
    private MethodInfo? MatchProxyCallOverload(Type rpcImplType)
    {
        MethodInfo? target = null;
        foreach (var m in rpcImplType.GetMethods(AnyInstance))
        {
            if (m.Name != "ProxyCall") continue;
            if (m.IsGenericMethodDefinition) continue;
            var ps = m.GetParameters();
            var sig = ps.Length == 0
                ? "()"
                : string.Join(",", Array.ConvertAll(ps, p => p.ParameterType.Name));
            _log.Info($"[ChatProbe] ProxyCall overload: ({sig})");

            if (target is null && ps.Length >= 3)
            {
                var p1Name = ps[1].ParameterType.Name;
                var p2Name = ps[2].ParameterType.Name;
                bool isUint = p1Name == "UInt32" || p1Name == "uint";
                bool isArray = p2Name == "Byte[]" || p2Name.IndexOf("ArrayBase", StringComparison.Ordinal) >= 0 || p2Name.IndexOf("StructArray", StringComparison.Ordinal) >= 0;
                if (isUint && isArray)
                {
                    target = m;
                }
            }
        }
        return target;
    }

    /// <summary>
    /// HarmonyX PREFIX on <c>ZRpcImpl.ProxyCall</c>. Inspects the proxy +
    /// methodId; for <c>ChitChat.GetChipChatRecords</c> (service uuid +
    /// method 2) extracts <c>channel_type</c> from the request body and
    /// queues it for the next chat-Return arrival to consume.
    /// </summary>
    private static void OnProxyCall(object?[] __args)
    {
        var probe = Instance;
        if (probe is null) return;
        if (__args is null || __args.Length < 3) return;
        var proxyObj = __args[0];
        var methodIdObj = __args[1];
        var bodyObj = __args[2];
        if (proxyObj is null || methodIdObj is null || bodyObj is null) return;

        try
        {
            uint methodId = Convert.ToUInt32(methodIdObj);
            if (methodId != GetChipChatRecordsMethodId) return;

            // Verify the proxy targets ChitChat service. IProxy exposes a Uuid()
            // method or a Uuid property; try both via reflection. We only need
            // the low 32 bits since chat service ids fit in uint.
            uint proxyUuidLow = TryReadProxyUuidLow(proxyObj);
            if (proxyUuidLow != ChitChatServiceUuid) return;

            TryRecordHistoryChannel(probe, bodyObj);
        }
        catch (Exception ex)
        {
            Instance?._log.Warning($"[ChatProbe] ProxyCall prefix threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>Body-extract + parse channel_type for GetChipChatRecords; enqueue for next chat-Return. Returns true on enqueue.</summary>
    private static bool TryRecordHistoryChannel(PandaChatProbe probe, object bodyObj)
    {
        // Body shape: either byte[] (Lua) or IBufferMessage (C#). For byte[]
        // we parse directly; for IBufferMessage we'd need ToByteArray() via
        // reflection. Start with byte[] (the Lua path used by the in-game
        // chat module per recon).
        // The body arrives as Il2CppStructArray<byte> (the IL2CPP projection
        // of byte[]). CoerceToByteArray walks Length + indexer to copy the
        // bytes into a managed byte[] — same path used by OnTcpDataReceived.
        byte[]? body = bodyObj as byte[] ?? Il2CppSpanCoercion.CoerceToByteArray(bodyObj);
        if (body is null) return false;

        int channelType = WireProtocol.ParseChannelTypeFromGetChipChatRecordsRequest(body);
        if (channelType <= 0) return false;

        probe._pendingHistoryChannels.Enqueue(channelType);

        // One-shot diagnostic: surface the first observed outbound history
        // channel so the send-side hook is confirmed at boot, then go
        // silent. Subsequent calls feed the FIFO without logging.
        if (!probe._firstProxyCallObservedLogged)
        {
            probe._firstProxyCallObservedLogged = true;
            probe._log.Info($"[ChatProbe] first GetChipChatRecords ProxyCall observed: channelType={channelType} (wire={WireProtocol.MapWireChannel(channelType)})");
        }
        else
        {
            probe.DiagProxyCallObserved(channelType);
        }
        return true;
    }

    /// <summary>Read <c>IProxy.Uuid()</c> or property as low-32-bit uint. Returns 0 on failure.</summary>
    private static uint TryReadProxyUuidLow(object proxy)
    {
        try
        {
            var t = proxy.GetType();
            // Try method Uuid() first.
            var uuidMethod = t.GetMethod("Uuid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, types: Type.EmptyTypes, modifiers: null);
            object? raw = null;
            if (uuidMethod is not null) raw = uuidMethod.Invoke(proxy, null);
            else
            {
                var uuidProp = t.GetProperty("Uuid", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (uuidProp is not null) raw = uuidProp.GetValue(proxy);
            }
            if (raw is null) return 0;
            return unchecked((uint)Convert.ToUInt64(raw));
        }
        catch { return 0; }
    }

    /// <summary>Coerce an IBufferMessage instance to raw protobuf bytes via reflection.</summary>
    private static byte[]? TryExtractBufferMessageBytes(object msg)
    {
        try
        {
            var t = msg.GetType();
            var toByteArr = t.GetMethod("ToByteArray", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, binder: null, types: Type.EmptyTypes, modifiers: null);
            if (toByteArr is null) return null;
            var raw = toByteArr.Invoke(msg, null);
            return raw as byte[];
        }
        catch { return null; }
    }

}
