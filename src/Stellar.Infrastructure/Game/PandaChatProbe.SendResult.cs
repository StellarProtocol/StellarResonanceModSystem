using System;
using System.Collections.Concurrent;
using Stellar.Abstractions.Domain;
using Stellar.Wire;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Server-driven send cooldown for <see cref="PandaChatProbe"/>: correlate each <c>SendChitChatMsg</c> Return
/// (<c>SendChitChatMsgReply{show_msg, cd_end_time, err_code}</c>) to the channel its request named, by call_id.
/// Returns carry no method id, so — exactly like the history channel — the channel is captured at SEND time:
/// the framework's own sends at dispatch (we mint the call_id), the game's own chat-box sends from the
/// <c>ZRpcImpl.SendBytes</c> prefix (request body = <c>v_request{channel_type=2,…}</c>). The framework builds its
/// sends itself, so the game's <c>chatCD_</c> never learns about them — this reply is the only cooldown source.
/// </summary>
internal sealed partial class PandaChatProbe
{
    private readonly record struct PendingSend(int ChannelType, bool ViaFramework);

    // call_id -> the channel a still-unanswered SendChitChatMsg went to. Bounded like the history map: an order
    // queue evicts the oldest never-answered call past MaxPendingChatCalls (stale ids drain as no-op removes).
    private readonly ConcurrentDictionary<uint, PendingSend> _sendByCallId = new();
    private readonly ConcurrentQueue<uint> _sendCallIdOrder = new();
    private bool _firstSendReplyLogged;

    public Action<ChatSendResult>? OnSendResult { get; set; }

    /// <summary>Remember which channel <paramref name="callId"/>'s send targeted (any thread).</summary>
    private void RecordSendCall(uint callId, int channelType, bool viaFramework)
    {
        if (channelType <= 0) return;
        _sendByCallId[callId] = new PendingSend(channelType, viaFramework);
        _sendCallIdOrder.Enqueue(callId);
        while (_sendCallIdOrder.Count > MaxPendingChatCalls && _sendCallIdOrder.TryDequeue(out var oldest))
        {
            _sendByCallId.TryRemove(oldest, out _);
        }
    }

    /// <summary>Game chat-box send seen on the SendBytes prefix: read channel_type off the raw request body.</summary>
    private void CaptureSendChannelFromSendArgs(uint callId, object? pbDataObj, object? pbSizeObj)
    {
        if (pbDataObj is null) return;
        var body = pbDataObj as byte[] ?? Il2CppSpanCoercion.CoerceToByteArray(pbDataObj);
        if (body is null) return;
        uint size = pbSizeObj is { } s ? Convert.ToUInt32(s) : 0u;
        int len = size > 0 && size <= (uint)body.Length ? (int)size : body.Length;
        RecordSendCall(callId, WireProtocol.ParseChannelTypeFromSendChitChatMsgRequest(new ReadOnlySpan<byte>(body, 0, len)), viaFramework: false);
    }

    /// <summary>
    /// If <paramref name="callId"/> answers a tracked send, parse the reply and raise <see cref="OnSendResult"/>;
    /// returns true when it consumed the Return (so the history parser is skipped). I/O thread.
    /// </summary>
    private bool TryProcessSendReply(uint callId, ReadOnlySpan<byte> payload)
    {
        if (!_sendByCallId.TryRemove(callId, out var pending)) return false;
        if (!WireProtocol.TryParseSendChitChatMsgReply(WireProtocol.UnwrapReturnEnvelope(payload), out var r)) return true;

        var result = new ChatSendResult(
            Channel:        WireProtocol.MapWireChannel(pending.ChannelType),
            Ok:             r.ErrCode == 0,
            ErrorCode:      r.ErrCode,
            CooldownEndUtc: r.CdEndTime > 0 ? DateTimeOffset.FromUnixTimeSeconds(r.CdEndTime).UtcDateTime : null)
        {
            ViaFramework  = pending.ViaFramework,
            ServerTimeUtc = r.ShowMsgTimestamp > 0 ? DateTimeOffset.FromUnixTimeSeconds(r.ShowMsgTimestamp).UtcDateTime : null,
        };

        if (!_firstSendReplyLogged)
        {
            _firstSendReplyLogged = true;
            _log.Info($"[ChatProbe] first send reply: callId={callId} channel={result.Channel} err={r.ErrCode} cdEnd={r.CdEndTime} showTs={r.ShowMsgTimestamp} viaFramework={pending.ViaFramework}");
        }
        else if (r.ErrCode != 0)
        {
            _log.Info($"[ChatProbe] send refused: channel={result.Channel} err={r.ErrCode} cdEnd={r.CdEndTime} viaFramework={pending.ViaFramework}");
        }
        OnSendResult?.Invoke(result);
        return true;
    }
}
