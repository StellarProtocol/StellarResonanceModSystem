using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Stellar.Abstractions.Domain;
using Stellar.Wire;
using Stellar.Application.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// History-specific concerns for <see cref="PandaChatProbe"/>:
/// parsing ChitChat <c>GetChipChatRecords_Ret</c> payloads into
/// <see cref="ChatMessage"/> records and applying the channel-attribution
/// heuristic that resolves the channel for batches arriving on the all-Returns
/// path (which carries no service_uuid / method_id on the wire).
/// </summary>
internal sealed partial class PandaChatProbe
{
    // ----------------- history-path state -----------------

    // First-time-seen marker for ChitChat history-fetch returns
    // (GetChipChatRecordsReply, field 5 = repeated ChitChatMsg multi_msg_list).
    // Written-then-read on the I/O thread (same thread as OnAnyReturnEnvelope),
    // so a plain bool is sufficient — no interlock needed.
    private bool _firstChitChatReturnObservedLogged;

    // Diagnostic counter for the per-batch log line. Increments per confirmed
    // chat-history Return regardless of channel resolution outcome.
    private int _historyBatchCount;

    // Last-observed channel per sender (populated by live Notify path on the
    // receive partial). Used as a refinement when the outbound-hook FIFO is
    // empty (or yielded Unknown).
    private readonly ConcurrentDictionary<long, ChatChannel> _senderLastChannel = new();

    /// <summary>
    /// Parse a ChitChat <c>Return</c> payload as a <c>GetChipChatRecordsReply</c>
    /// (the only ChitChat method whose reply carries a chat-record payload).
    /// Other ChitChat Returns produce zero messages and we exit silently.
    ///
    /// <paramref name="payload"/> is the post-zstd-decompression payload bytes
    /// supplied by <see cref="PandaWireTap"/> (no header, no compression
    /// framing). <paramref name="callId"/> + <paramref name="correlated"/>
    /// feed the first-correlated-Return diagnostic.
    ///
    /// Outer envelope: per <c>serv_chit_chat.proto</c>, the payload is a
    /// <c>GetChipChatRecords_Ret { ret = 1 : GetChipChatRecordsReply }</c>
    /// wrapper. We unwrap field 1 (length-delimited) to get the inner reply
    /// bytes, then read <c>multi_msg_list</c> (field 5, repeated ChitChatMsg).
    /// </summary>
    private void ProcessChitChatReturnPayload(ReadOnlySpan<byte> payload, uint callId, bool correlated)
    {
        // First-correlated-Return diagnostic — fires only when call_id matched
        // the pending set (Send hook actually worked), so spam-free.
        if (correlated && !_firstChitChatReturnObservedLogged)
        {
            LogFirstCorrelatedChitChatReturn(payload, callId);
        }

        // Unwrap GetChipChatRecords_Ret { ret = 1 } envelope. If the first tag
        // doesn't match (1, length-delimited) the helper returns the payload
        // unchanged so the parser can attempt a direct-reply parse — defensive
        // against servers that strip the envelope.
        var replyBytes = WireProtocol.UnwrapReturnEnvelope(payload);

        int historyCount = WireProtocol.TryParseGetChipChatRecordsReply(replyBytes, out var messages);
        if (historyCount == 0)
        {
            // Either not a GetChipChatRecords reply, or an empty batch (is_end=true).
            // Either way, nothing to dispatch — exit silently.
            return;
        }

        // Non-chat Returns whose protobuf happens to have a field 5 with
        // length-delimited content would surface here as a "history batch" of
        // garbage. Real ChitChatMsg entries always have a non-empty sender
        // name + non-zero sender id; if NONE match, treat as a non-chat false
        // positive and exit silently.
        int validCount = CountValidHistoryMessages(messages);
        if (validCount == 0) return;

        // CONFIRMED chat history batch. Resolve the channel — two sources:
        //   1. Outbound-hook FIFO (populated by the ZRpcImpl.ProxyCall
        //      prefix when it observes an outbound GetChipChatRecords).
        //   2. Per-message sender hint: if the sender appeared in a live
        //      Notify on a known channel, use that as the per-message override.
        // No hardcoded send-order fallback — game send order is not fixed.
        System.Threading.Interlocked.Increment(ref _historyBatchCount);
        ChatChannel batchChannel = ChatChannel.Unknown;
        if (_pendingHistoryChannels.TryDequeue(out var wireChannel))
        {
            batchChannel = WireProtocol.MapWireChannel(wireChannel);
        }

        DispatchHistoryRecords(messages, batchChannel, callId, historyCount, validCount);
    }

    /// <summary>
    /// Emit the first-time correlated-Return diagnostic line. Hot-path-cheap:
    /// fires at most once per process and only when the outbound Send hook
    /// genuinely correlated this Return to a tracked call_id.
    /// </summary>
    private void LogFirstCorrelatedChitChatReturn(ReadOnlySpan<byte> payload, uint callId)
    {
        _firstChitChatReturnObservedLogged = true;
        var dumpLen = payload.Length < 32 ? payload.Length : 32;
        var sb = new System.Text.StringBuilder(dumpLen * 3);
        for (int i = 0; i < dumpLen; i++) { sb.Append(payload[i].ToString("X2")); sb.Append(' '); }
        _log.Info($"[ChatProbe] first correlated ChitChat Return: callId={callId} payloadLen={payload.Length} firstBytes={sb}");
    }

    /// <summary>
    /// Count entries that look like real <c>ChitChatMsg</c> rows — a non-empty
    /// sender name and a non-zero sender id. Used to distinguish a true history
    /// batch from a non-chat Return whose protobuf happens to expose a field 5
    /// with length-delimited content (false positive of the all-Returns path).
    /// </summary>
    private static int CountValidHistoryMessages(List<(ChatMessage Msg, long MsgId)> messages)
    {
        int validCount = 0;
        for (int i = 0; i < messages.Count; i++)
        {
            var m = messages[i].Msg;
            if (!string.IsNullOrEmpty(m.SenderName) && m.SenderId != 0) validCount++;
        }
        return validCount;
    }

    /// <summary>
    /// Dedup the just-parsed history batch against the live Notify path and
    /// dispatch each survivor with the best-resolved channel (batch channel
    /// first, per-sender hint second, <see cref="ChatChannel.Unknown"/> last).
    /// Key is (senderId, msgId, timestampTicks): msg_id is per-PEER-conversation
    /// for whispers on the SEA build (not globally unique as the proto comment
    /// claims), and senderId alone isn't enough because the same local sender
    /// reuses msg_id sequences across different peer conversations. The wire
    /// send_time disambiguates those.
    /// </summary>
    private void DispatchHistoryRecords(
        List<(ChatMessage Msg, long MsgId)> messages,
        ChatChannel batchChannel,
        uint callId,
        int historyCount,
        int validCount)
    {
        int dispatched = 0;
        ChatMessage? firstDispatched = null;
        foreach (var (origMsg, msgId) in messages)
        {
            if (string.IsNullOrEmpty(origMsg.SenderName) || origMsg.SenderId == 0) continue;
            if (!MarkSeen(origMsg.SenderId, msgId, origMsg.Timestamp.Ticks)) continue;

            // Channel priority: batch-channel if we resolved one; otherwise
            // per-sender hint from live Notify cache; otherwise leave Unknown.
            ChatChannel effective = batchChannel;
            if (effective == ChatChannel.Unknown
                && _senderLastChannel.TryGetValue(origMsg.SenderId, out var hint))
            {
                effective = hint;
            }
            var msg = effective != ChatChannel.Unknown
                ? origMsg with { Channel = effective }
                : origMsg;

            OnMessageReceived?.Invoke(msg);
            firstDispatched ??= msg;
            dispatched++;
        }

        // Log every batch (not just the first) so we can correlate each
        // resolved channel with what the user sees in the UI. Login fetches
        // typically produce 4 batches — small log footprint.
        if (dispatched > 0)
        {
            var f = firstDispatched!;
            _log.Info($"[Chat] history batch received: {dispatched}/{historyCount} dispatched after dedup (callId={callId}; batchIdx={_historyBatchCount} batchChannel={batchChannel}; first: channel={f.Channel} sender='{f.SenderName}' text='{f.Text}')");
        }
        else if (validCount > 0)
        {
            // ValidCount > 0 but dispatched == 0 means ALL messages were
            // deduped (the entire batch was already delivered via live
            // Notify). Log so we can see batches we suppressed.
            _log.Info($"[Chat] history batch fully deduped: validCount={validCount}/{historyCount} (callId={callId}; batchIdx={_historyBatchCount} batchChannel={batchChannel})");
        }
    }
}
