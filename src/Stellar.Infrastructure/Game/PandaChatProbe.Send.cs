using System;
using System.Collections.Concurrent;
using System.Reflection;
using Stellar.Abstractions.Domain;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Send-side orchestration for <see cref="PandaChatProbe"/>: outbound state,
/// the public <see cref="TrySend"/> entry point, and the chat-target mapper.
///
/// Companion partials (see class doc on <see cref="PandaChatProbe"/>):
/// <list type="bullet">
/// <item><c>Send.Builders.cs</c> — packet/envelope construction + byte writers.</item>
/// <item><c>Send.Il2Cpp.cs</c> — managed-byte[] to IL2CPP ReadOnlySpan&lt;byte&gt; coercion.</item>
/// <item><c>Send.Hooks.cs</c> — ProxyCall + ZTcpClient.Send PREFIX installation and handlers.</item>
/// </list>
/// </summary>
internal sealed partial class PandaChatProbe
{
    // ----------------- send-path state -----------------

    // Outbound ChitChat call_id correlation. Returns from the server don't carry
    // service_uuid in their wire header (bytes 10..13 are call_id) — so the only
    // way to identify a Return as chat-related is to remember which call_ids we
    // sent ChitChat Calls with and match Returns against that set.
    //
    // Populated by the ZTcpClient.Send prefix when an outbound packet has
    // msgType=Call and serviceUuid==ChitChatServiceUuid. Consumed (TryRemove) by
    // the recv-path drain loop when a Return arrives with a matching call_id.
    // Bounded to MaxPendingChatCalls to prevent unbounded growth if Returns are
    // dropped or never arrive (e.g., server-side timeout).
    private readonly ConcurrentDictionary<uint, byte> _pendingChitChatCallIds = new();
    private const int MaxPendingChatCalls = 64;

    // FIFO of channel_type values from outbound GetChipChatRecords requests.
    // Populated by the ZRpcImpl.ProxyCall PREFIX (OnProxyCall) when it observes
    // a GetChipChatRecords call; consumed by ProcessChitChatReturnPayload on
    // the matching Return so history batches can be attributed to a channel.
    private readonly ConcurrentQueue<int> _pendingHistoryChannels = new();
    private bool _firstProxyCallObservedLogged;

    // Cached chat-server ZTcpClient instance, captured from env.Connection on
    // the FIRST chat packet observed (in OnChatNotifyEnvelope / OnChatMethod3-
    // Envelope). Lives as an opaque object because the ZTcpClient type is only
    // available via reflection at runtime. Written-once, read on any thread
    // invoking IChat.Send — synchronized via _chatTcpClientLock.
    private object? _chatTcpClient;
    private readonly object _chatTcpClientLock = new();

    // Resolved MethodInfo for ZCode.ZNet.ZTcpClient.Send(ReadOnlySpan<byte>).
    // Set in PatchAll once we locate the type; null if resolution fails.
    private MethodInfo? _tcpClientSend;

    // Cached ConstructorInfo for Il2CppSystem.ReadOnlySpan<byte>.ctor(byte[])
    // or .ctor(Il2CppStructArray<byte>). Needed because reflection cannot
    // implicitly convert a managed byte[] to the IL2CPP ref-struct wrapper —
    // we must construct one explicitly. Resolved lazily on first send.
    private ConstructorInfo? _il2cppSpanCtor;
    // Concrete Type for Il2CppSystem.ReadOnlySpan<byte>. Cached alongside the
    // ctor so we don't re-walk the parameter type on every send.
    private Type? _il2cppSpanType;

    // One-shot diagnostic gate — first successful TrySend logs the wire size.
    private bool _firstSendLogged;

    // Monotonic counter for the Call header's call_id field. BPSR-B's client
    // increments per outbound Call so the server can correlate Returns to the
    // originating request. We start at 1 to avoid colliding with the server's
    // zero-init "no call" sentinel.
    private long _callIdCounter;

    // One-shot diagnostic — first observed outbound ChitChat Call (by the
    // ZTcpClient.Send PREFIX). Confirms the send-side hook is firing.
    private bool _firstSendObservedLogged;

    // One-shot diagnostic — first invocation of the ZTcpClient.Send PREFIX
    // regardless of payload contents. Confirms HarmonyX is wired correctly
    // (IL2CPP direct dispatch can bypass managed prefix patches).
    private static int _firstSendPrefixFiredLogged;

    /// <summary>
    /// Successful result from <see cref="BuildOutboundFrame"/>: the encoded packet,
    /// the call_id stamped into it, and the channel_type used for the log line.
    /// </summary>
    private readonly record struct OutboundFrameResult(byte[] Packet, uint CallId, int ChannelType);

    /// <summary>
    /// Build a Call packet for ChitChat.SendChitChatMsg and dispatch it through
    /// the cached chat-server <c>ZTcpClient.Send(ReadOnlySpan&lt;byte&gt;)</c>.
    /// The chat client is captured passively on the first observed chat receive
    /// (in <see cref="OnChatNotifyEnvelope"/>) — until then this returns false
    /// with a descriptive <paramref name="failureReason"/>.
    /// </summary>
    public bool TrySend(ChatTarget target, string text, out string? failureReason)
    {
        if (!BuildOutboundFrame(target, text, out failureReason, out var frame))
        {
            return false;
        }

        try
        {
            // Convert managed byte[] -> Il2CppSystem.ReadOnlySpan<byte> for the
            // reflection Invoke. Resolved lazily on first call.
            object spanArg = WrapBytesAsIl2CppSpan(frame.Packet);

            _tcpClientSend!.Invoke(_chatTcpClient, new object[] { spanArg });

            if (!_firstSendLogged)
            {
                _firstSendLogged = true;
                _log.Info($"[Chat] first send dispatched: channelType={frame.ChannelType} textLen={text.Length} packetBytes={frame.Packet.Length} callId={frame.CallId}");
            }
            return true;
        }
        catch (Exception ex)
        {
            var inner = ex.InnerException;
            failureReason = inner is not null
                ? $"{ex.GetType().Name}: {ex.Message} (inner {inner.GetType().Name}: {inner.Message})"
                : $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Validate preconditions and build the wire-encoded outbound Call frame for
    /// a ChitChat.SendChitChatMsg request. Returns false (with
    /// <paramref name="failureReason"/> populated) when any precondition fails —
    /// empty text, chat connection not yet observed, Send method unresolved, or
    /// the target has no send-side mapping. On success returns the fully built
    /// frame so the caller can log/dispatch.
    /// </summary>
    private bool BuildOutboundFrame(
        ChatTarget target,
        string text,
        out string? failureReason,
        out OutboundFrameResult frame)
    {
        frame = default;

        if (!TryResolveSendChannel(target, text, out failureReason, out int channelType, out long whisperTargetId))
        {
            return false;
        }

        // Build the protobuf payload (envelope-wrapped, mirroring BPSR-B's
        // SendChitChatMsgCommand.build_payload).
        var payload = BuildSendChitChatMsgEnvelope(channelType, text, whisperTargetId);

        // Build the wire packet (size + flags + service_uuid + stub + call + method + payload).
        // BPSR-B sets stub_id=0 for outbound client calls; call_id is
        // monotonic for Return correlation (we don't track Returns yet,
        // but the server still expects a unique id).
        uint callId = (uint)System.Threading.Interlocked.Increment(ref _callIdCounter);
        var packet = BuildCallPacket(
            serviceUuid: ChitChatServiceUuid,
            stubId: 0u,
            callId: callId,
            methodId: SendChitChatMsgMethodId,
            payload: payload);
        frame = new OutboundFrameResult(packet, callId, channelType);
        return true;
    }

    /// <summary>
    /// Validate the send preconditions and resolve the wire channel_type +
    /// whisper target for a <see cref="ChatTarget"/>. Returns false (with
    /// <paramref name="failureReason"/>) for empty text, unobserved chat
    /// connection, unresolved <c>ZTcpClient.Send</c>, or unsupported target.
    /// </summary>
    private bool TryResolveSendChannel(
        ChatTarget target,
        string text,
        out string? failureReason,
        out int channelType,
        out long whisperTargetId)
    {
        failureReason = null;
        channelType = -1;
        whisperTargetId = 0L;

        if (string.IsNullOrEmpty(text))
        {
            failureReason = "empty text";
            return false;
        }
        if (_chatTcpClient is null)
        {
            failureReason = "chat connection not yet observed (send before first inbound chat packet)";
            return false;
        }
        if (_tcpClientSend is null)
        {
            failureReason = "ZTcpClient.Send(ReadOnlySpan<byte>) not resolved";
            return false;
        }

        (int ct, long wt) = MapSendTarget(target);
        if (ct < 0)
        {
            failureReason = $"unsupported chat target: {target.GetType().Name}";
            return false;
        }
        channelType = ct;
        whisperTargetId = wt;
        return true;
    }

    /// <summary>
    /// Reverse of <see cref="WireProtocol.MapWireChannel"/>: project a
    /// <see cref="ChatTarget"/> into a <c>ChitChatChannelType</c> wire value
    /// plus an optional whisper target id (carried inside the inner
    /// <c>ChatMsgInfo.target_id</c> field). Returns <c>(-1, 0)</c> for targets
    /// that have no send-side mapping yet.
    /// </summary>
    private static (int channelType, long whisperTargetId) MapSendTarget(ChatTarget target) => target switch
    {
        ChatTarget.SayTarget          => (2, 0L),  // ChannelScene
        ChatTarget.WorldTarget        => (1, 0L),  // ChannelWorld
        ChatTarget.PartyTarget        => (3, 0L),  // ChannelTeam
        ChatTarget.GuildTarget        => (4, 0L),  // ChannelUnion
        ChatTarget.WhisperTarget w    => (5, w.TargetId),  // ChannelPrivate
        _                              => (-1, 0L),
    };
}
