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
        // Resolve THIS Return's history channel by call_id BEFORE any early
        // return (see ResolveHistoryChannel) — so an EMPTY-history batch consumes
        // its OWN map entry and can never poison a later channel's batch.
        ChatChannel batchChannel = ResolveHistoryChannel(callId, out long batchPeerId);

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

        // CONFIRMED chat history batch. The channel was already resolved at the
        // top of this method from the call_id -> channel_type map (populated by
        // the outbound ZTcpClient.Send prefix). If that missed, batchChannel is
        // Unknown and DispatchHistoryRecords falls through to the per-sender
        // hint (a sender seen on a live Notify on a known channel).
        System.Threading.Interlocked.Increment(ref _historyBatchCount);

        DispatchHistoryRecords(messages, new HistoryBatch(batchChannel, batchPeerId, callId), historyCount, validCount);
    }

    /// <summary>Per-Return attribution resolved from the request's call_id: channel + (private only) peer charId.</summary>
    private readonly record struct HistoryBatch(ChatChannel Channel, long PeerId, uint CallId);

    /// <summary>
    /// Resolve (and consume) the history channel recorded for a Return's
    /// <paramref name="callId"/> by the outbound <c>ZTcpClient.Send</c> prefix.
    /// The <c>TryRemove</c> is UNCONDITIONAL — the caller invokes this before its
    /// empty/invalid early-returns — so an empty-history batch removes its OWN
    /// entry (never leaving a stale one for the next channel to mis-claim, the
    /// old positional-FIFO bug) and the map never leaks. A Return whose call_id
    /// we never recorded (non-chat Return, dropped/compressed/overflowed send)
    /// resolves <see cref="ChatChannel.Unknown"/>, and the caller's
    /// <see cref="DispatchHistoryRecords"/> falls through to the per-sender hint.
    /// <see cref="WireProtocol.MapWireChannel"/> runs only on a hit. <paramref name="peerId"/> is the private
    /// fetch's <c>target_id</c> (0 for non-private batches), consumed from its companion map the same way.
    /// </summary>
    private ChatChannel ResolveHistoryChannel(uint callId, out long peerId)
    {
        if (!_historyPeerByCallId.TryRemove(callId, out peerId)) peerId = 0;
        return _historyChannelByCallId.TryRemove(callId, out var wireChannel)
            ? WireProtocol.MapWireChannel(wireChannel)
            : ChatChannel.Unknown;
    }

    /// <summary>
    /// Emit the first-time correlated-Return diagnostic line. Hot-path-cheap:
    /// fires at most once per process and only when the outbound Send hook
    /// genuinely correlated this Return to a tracked call_id.
    /// </summary>
    private void LogFirstCorrelatedChitChatReturn(ReadOnlySpan<byte> payload, uint callId)
    {
        _firstChitChatReturnObservedLogged = true;
        // The leading bytes of a history reply are its first record (sender char info incl. the NAME, then text), so
        // normal play logs the framing facts alone; the hex prefix is a diagnostics-mode extra (DiagFirstReturnBytes).
        _log.Info($"[ChatProbe] first correlated ChitChat Return: callId={callId} payloadLen={payload.Length}");
        DiagFirstReturnBytes(payload, callId);
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
        HistoryBatch batch,
        int historyCount,
        int validCount)
    {
        ChatChannel batchChannel = batch.Channel;
        uint callId = batch.CallId;
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
            // A private fetch names its peer in the REQUEST (the reply rows don't) — authoritative, so it
            // wins over ChatService's sender/target inference for the whole batch.
            if (batch.PeerId != 0 && effective == ChatChannel.Whisper) msg = msg with { PeerId = batch.PeerId };

            OnMessageReceived?.Invoke(msg);
            firstDispatched ??= msg;
            dispatched++;
        }

        // Log every batch (not just the first) so we can correlate each
        // resolved channel with what the user sees in the UI. Login fetches
        // typically produce 4 batches — small log footprint. Counts, channel and
        // lengths ONLY: never the message text, sender name or peer charId (a
        // private batch is someone's whisper conversation — the log is shared).
        if (dispatched > 0)
        {
            var f = firstDispatched!;
            _log.Info($"[Chat] history batch received: {dispatched}/{historyCount} dispatched after dedup (callId={callId}; batchIdx={_historyBatchCount} batchChannel={batchChannel} hasPeer={batch.PeerId != 0}; first: channel={f.Channel} textLen={f.Text?.Length ?? 0})");
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
