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
/// <item><c>Send.Hooks.Tcp.cs</c> — resolves <c>ZTcpClient.Send(ReadOnlySpan&lt;byte&gt;)</c> as the plugin send target for <see cref="TrySend"/> + installs the connection-close hooks (no capture — the game does not use this overload).</item>
/// <item><c>Send.Hooks.Rpc.cs</c> — <c>ZRpcImpl.SendBytes</c> PREFIX; captures outbound ChitChat call_ids (and the per-call history channel_type) for Return correlation at the transport-agnostic RPC framing layer.</item>
/// <item><c>Send.Connection.cs</c> — cached chat ZTcpClient lifetime: re-cache on reconnect, close PREFIXes, handle liveness check.</item>
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
    // Populated by the ZRpcImpl.SendBytes prefix (Send.Hooks.Rpc.cs) for every
    // outbound ChitChat Call (serviceUuid==ChitChatServiceUuid). Consumed
    // (TryRemove) by the recv-path drain loop when a Return arrives with a
    // matching call_id.
    // Bounded to MaxPendingChatCalls to prevent unbounded growth if Returns are
    // dropped or never arrive (e.g., server-side timeout).
    private readonly ConcurrentDictionary<uint, byte> _pendingChitChatCallIds = new();
    private const int MaxPendingChatCalls = 64;

    // Map of outbound GetChipChatRecords call_id -> requested channel_type, so a
    // history Return can be attributed to the channel that ASKED for it, keyed by
    // the RPC call_id (NOT a positional FIFO). Keying by call_id is correct even
    // when a channel's login history is EMPTY (that Return removes its own entry
    // and poisons no other), when Returns complete out of order, and when a send
    // is dropped/unrecorded (its Return just misses -> resolves Unknown -> the
    // per-sender hint). Populated by the ZRpcImpl.SendBytes prefix
    // (TryCaptureHistoryChannel on Send.Hooks.Rpc.cs — that transport-agnostic
    // framing method has both the call_id and the raw request body as plain
    // params); consumed (TryRemove) by ProcessChitChatReturnPayload on the
    // matching Return.
    //
    // Bounded by MaxPendingChatCalls via a companion insertion-order queue:
    // whenever the order queue exceeds the cap we drain its head, evicting the
    // oldest still-live entry (a never-returned call) and draining stale ids
    // that a Return already removed. Both structures therefore stay <= the cap.
    private readonly ConcurrentDictionary<uint, int> _historyChannelByCallId = new();
    private readonly ConcurrentQueue<uint> _historyChannelCallIdOrder = new();
    private bool _firstHistoryChannelCapturedLogged;

    // Cached chat-server ZTcpClient instance, captured from env.Connection on
    // chat packets (in OnChatNotifyEnvelope / OnChatMethod3Envelope). Lives as
    // an opaque object because the ZTcpClient type is only available via
    // reflection at runtime. Re-cached when the game reconnects and nulled when
    // the game closes it — see Send.Connection.cs. Synchronized via _chatTcpClientLock.
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
    // ZRpcImpl.SendBytes PREFIX). Confirms the send-side capture hook is firing.
    private bool _firstSendObservedLogged;

    // One-shot diagnostic — first invocation of the ZRpcImpl.SendBytes PREFIX
    // regardless of service/payload. Confirms HarmonyX is wired correctly onto
    // the transport-agnostic outbound RPC framing method (the capture site as of
    // 2.25.2; the 2.25.1 ZTcpClient.Send site never fired for game traffic).
    private static int _firstRpcSendBytesFiredLogged;

    /// <summary>
    /// Successful result from <see cref="BuildOutboundFrame"/>: the encoded packet,
    /// the call_id stamped into it, and the channel_type used for the log line.
    /// </summary>
    private readonly record struct OutboundFrameResult(byte[] Packet, uint CallId, int ChannelType);

    /// <summary>
    /// Build a Call packet for ChitChat.SendChitChatMsg and dispatch it through
    /// the live chat-server <c>ZTcpClient.Send(ReadOnlySpan&lt;byte&gt;)</c>.
    /// The client is resolved on demand from <c>ZRpcCtrl</c> (primary — see
    /// <c>Send.LiveClient.cs</c>), which closes the post-reconnect dead window;
    /// if that can't resolve (UDP channel, null step, reflection failure) we fall
    /// back to the passively-captured client (see <c>Send.Connection.cs</c>).
    /// Returns false with a descriptive <paramref name="failureReason"/> only when
    /// BOTH sources are unavailable.
    /// </summary>
    public bool TrySend(ChatTarget target, string text, out string? failureReason)
    {
        if (!BuildOutboundFrame(target, text, out failureReason, out var frame))
        {
            return false;
        }

        // PRIMARY: resolve the live chat ZTcpClient on demand. Returns a
        // handle-verified client, or null on any failure → passive fallback.
        object? client = TryResolveLiveChatClient();

        // FALLBACK: the passively-captured client, re-cached on inbound chat
        // packets and dropped by the close hooks. Snapshotted under the lock +
        // native-handle liveness check (sending through a freed handle → native
        // access violation that kills the game, uncatchable here). If this also
        // yields nothing, both sources failed → report the passive reason.
        if (client is null)
        {
            if (!TryTakeLiveChatClient(out var passive, out failureReason))
            {
                return false;
            }
            client = passive;
        }

        return SendThroughClient(client, frame, text, out failureReason);
    }

    /// <summary>
    /// Dispatch an already-built frame through a resolved (live or passive) chat
    /// <c>ZTcpClient</c>: coerce the managed <c>byte[]</c> to an IL2CPP
    /// <c>ReadOnlySpan&lt;byte&gt;</c> and reflection-invoke <c>Send</c>. Shared by
    /// the active and passive paths. Returns false with
    /// <paramref name="failureReason"/> on any invoke failure.
    /// </summary>
    private bool SendThroughClient(object client, OutboundFrameResult frame, string text, out string? failureReason)
    {
        failureReason = null;
        try
        {
            // Convert managed byte[] -> Il2CppSystem.ReadOnlySpan<byte> for the
            // reflection Invoke. Resolved lazily on first call.
            object spanArg = WrapBytesAsIl2CppSpan(frame.Packet);

            _tcpClientSend!.Invoke(client, new object[] { spanArg });

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
    /// empty text, Send method unresolved, or the target has no send-side mapping.
    /// Client availability is NOT a precondition here — it is decided at dispatch
    /// (active resolution first, passive cache second). On success returns the
    /// fully built frame so the caller can log/dispatch.
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
    /// <paramref name="failureReason"/>) for empty text, unresolved
    /// <c>ZTcpClient.Send</c>, or unsupported target.
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
        // NB: no "_chatTcpClient is null" gate here — client availability is
        // decided later by resolution (active ZRpcCtrl path first, passive cache
        // second). Gating on the passive cache would re-block the exact
        // post-reconnect dead window this fix exists to close.
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
