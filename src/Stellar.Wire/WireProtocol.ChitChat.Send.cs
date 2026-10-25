using System;

namespace Stellar.Wire;

/// <summary>
/// <c>ChitChat.SendChitChatMsg</c> (method 1) request/reply parsers — the server-driven send cooldown.
/// </summary>
public static partial class WireProtocol
{
    /// <summary>
    /// Parsed <c>SendChitChatMsgReply</c>. <c>CdEndTime</c> and <c>ShowMsgTimestamp</c> are BOTH Unix SECONDS
    /// (the game computes its per-channel CD as <c>cdEndTime − showMsg.timestamp</c> and counts it down with a
    /// 1-second timer, <c>chat_main_vm.lua:1000-1021</c>; chat timestamps are seconds everywhere). 0 = absent.
    /// <c>ErrCode</c> 0 = accepted; <c>5211</c> = <c>ErrChatSendCdNoEnd</c> ("too frequent").
    /// </summary>
    public readonly record struct SendChitChatMsgReplyResult(long CdEndTime, int ErrCode, long ShowMsgTimestamp, long ShowMsgId);

    /// <summary>
    /// Parse a <c>SendChitChatMsgReply { ChitChatMsg show_msg = 3; int64 cd_end_time = 4; EErrorCode err_code = 5 }</c>
    /// (already unwrapped from the <c>SendChitChatMsg_Ret { ret = 1 }</c> envelope — see
    /// <see cref="UnwrapReturnEnvelope"/>). Returns false on malformed input. Only <c>show_msg</c>'s msg_id +
    /// timestamp are read (the echoed message itself also arrives as a normal chat Notify).
    /// </summary>
    public static bool TryParseSendChitChatMsgReply(ReadOnlySpan<byte> reply, out SendChitChatMsgReplyResult result)
    {
        result = default;
        long cdEnd = 0, showTs = 0, showId = 0;
        int err = 0;
        int pos = 0;
        while (pos < reply.Length)
        {
            if (!TryReadTag(reply, ref pos, out var field, out var wireType)) return false;
            if (field == 3 && wireType == 2)
            {
                if (!TryReadLengthDelimited(reply, ref pos, out var show)) return false;
                if (TryParseChitChatMsg(show, out var parsed)) { showTs = parsed.Timestamp; showId = parsed.MsgId; }
            }
            else if (field == 4 && wireType == 0)
            {
                if (!TryReadVarint(reply, ref pos, out var v)) return false;
                cdEnd = unchecked((long)v);
            }
            else if (field == 5 && wireType == 0)
            {
                if (!TryReadVarint(reply, ref pos, out var e)) return false;
                err = unchecked((int)e);
            }
            else if (!SkipField(reply, ref pos, wireType)) return false;
        }
        result = new SendChitChatMsgReplyResult(cdEnd, err, showTs, showId);
        return true;
    }

    /// <summary>
    /// Read <c>SendChitChatMsgRequest.channel_type</c> (field 2) out of the outbound
    /// <c>ChitChat.SendChitChatMsg { v_request = 1 }</c> envelope, so the reply's cooldown can be attributed to the
    /// channel that sent. Returns -1 on a shape mismatch / absent field.
    /// </summary>
    public static int ParseChannelTypeFromSendChitChatMsgRequest(ReadOnlySpan<byte> span)
    {
        if (!TryUnwrapVRequest(span, out var inner)) return -1;
        int pos = 0;
        while (pos < inner.Length)
        {
            if (!TryReadTag(inner, ref pos, out var f, out var w)) return -1;
            if (f == 2 && w == 0)
            {
                if (!TryReadVarint(inner, ref pos, out var v)) return -1;
                return (int)v;
            }
            if (!SkipField(inner, ref pos, w)) return -1;
        }
        return -1;
    }
}
