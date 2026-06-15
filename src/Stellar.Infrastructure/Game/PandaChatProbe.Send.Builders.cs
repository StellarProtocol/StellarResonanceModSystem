using System;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Outbound packet construction for <see cref="PandaChatProbe"/>:
/// the ChitChat.SendChitChatMsg protobuf envelope builder, the BPSR Call
/// wire-frame builder, and the big-endian / varint byte writers they share.
/// All members are <c>private static</c> — these helpers have no instance
/// state and are reachable from the send-path orchestration in
/// <c>PandaChatProbe.Send.cs</c>.
/// </summary>
internal sealed partial class PandaChatProbe
{
    /// <summary>
    /// Construct the protobuf payload for ChitChat.SendChitChatMsg, including
    /// the outer envelope. Mirrors BPSR-B's <c>SendChitChatMsgCommand.build_payload</c>:
    /// <code>
    ///   ChitChat.SendChitChatMsg {
    ///     v_request = 1 : SendChitChatMsgRequest {
    ///       channel_type = 2;     // ChitChatChannelType (varint enum)
    ///       msg_info     = 4 : ChatMsgInfo {
    ///         msg_type  = 1;       // ChitChatMsgType (varint, 0 = TextMessage)
    ///         target_id = 2;       // int64 (whispers only)
    ///         msg_text  = 3;       // string
    ///       }
    ///     }
    ///   }
    /// </code>
    /// All field numbers verified against
    /// <c>(local reference)</c>
    /// and <c>stru_chat_msg_info.proto</c>.
    /// </summary>
    private static byte[] BuildSendChitChatMsgEnvelope(int channelType, string text, long whisperTargetId)
    {
        // Build ChatMsgInfo first — innermost message.
        // msg_type (field 1, varint) = 0 (ChatMsgTextMessage).
        // target_id (field 2, varint) — only for whispers.
        // msg_text  (field 3, length-delimited string).
        var msgInfoMs = new System.IO.MemoryStream();
        WriteVarint(msgInfoMs, ((ulong)1 << 3) | 0); // tag: field=1 wire=0 (varint)
        WriteVarint(msgInfoMs, 0UL);                  // msg_type = 0 (ChatMsgTextMessage)
        if (whisperTargetId != 0L)
        {
            WriteVarint(msgInfoMs, ((ulong)2 << 3) | 0); // tag: field=2 wire=0
            WriteVarint(msgInfoMs, unchecked((ulong)whisperTargetId));
        }
        var textBytes = System.Text.Encoding.UTF8.GetBytes(text);
        WriteVarint(msgInfoMs, ((ulong)3 << 3) | 2); // tag: field=3 wire=2 (length-delimited)
        WriteVarint(msgInfoMs, (ulong)textBytes.Length);
        msgInfoMs.Write(textBytes, 0, textBytes.Length);
        var msgInfoBytes = msgInfoMs.ToArray();

        // Build SendChitChatMsgRequest.
        // channel_type (field 2, varint enum).
        // msg_info     (field 4, length-delimited nested).
        var reqMs = new System.IO.MemoryStream();
        WriteVarint(reqMs, ((ulong)2 << 3) | 0); // tag: field=2 wire=0
        WriteVarint(reqMs, unchecked((ulong)channelType));
        WriteVarint(reqMs, ((ulong)4 << 3) | 2); // tag: field=4 wire=2
        WriteVarint(reqMs, (ulong)msgInfoBytes.Length);
        reqMs.Write(msgInfoBytes, 0, msgInfoBytes.Length);
        var reqBytes = reqMs.ToArray();

        // Build outer envelope: ChitChat.SendChitChatMsg { v_request = 1 }.
        // Field 1 (length-delimited) wraps the request. Mirrors the receive-side
        // unwrap in UnwrapNotifyEnvelope / UnwrapReturnEnvelope.
        var envMs = new System.IO.MemoryStream();
        WriteVarint(envMs, ((ulong)1 << 3) | 2); // tag: field=1 wire=2
        WriteVarint(envMs, (ulong)reqBytes.Length);
        envMs.Write(reqBytes, 0, reqBytes.Length);
        return envMs.ToArray();
    }

    /// <summary>
    /// Build the full BPSR wire frame for an outbound Call:
    /// <code>
    ///   [size: u32 BE][flags: u16 BE][service_uuid: u64 BE]
    ///   [stub_id: u32 BE][call_id: u32 BE][method_id: u32 BE][payload]
    /// </code>
    /// All fields are big-endian, size includes the 4-byte size prefix itself.
    /// Mirrors BPSR-B's <c>Packet.create_call_packet</c>.
    /// </summary>
    private static byte[] BuildCallPacket(uint serviceUuid, uint stubId, uint callId, uint methodId, byte[] payload)
    {
        // 4 size + 2 flags + 8 svc + 4 stub + 4 call + 4 method = 26 fixed header bytes
        int totalLength = 26 + payload.Length;
        var packet = new byte[totalLength];
        int pos = 0;

        // size (u32 BE) — includes itself.
        WriteUInt32BigEndian(packet, ref pos, (uint)totalLength);

        // flags (u16 BE) — high bit = zstd-compressed (we never set it), low 15 = msg_type.
        WriteUInt16BigEndian(packet, ref pos, ZprotoMsgTypeIdCall);

        // service_uuid (u64 BE) — 32-bit value padded into the high half is zero.
        WriteUInt64BigEndian(packet, ref pos, serviceUuid);

        // stub_id (u32 BE)
        WriteUInt32BigEndian(packet, ref pos, stubId);

        // call_id (u32 BE)
        WriteUInt32BigEndian(packet, ref pos, callId);

        // method_id (u32 BE)
        WriteUInt32BigEndian(packet, ref pos, methodId);

        // payload
        Buffer.BlockCopy(payload, 0, packet, pos, payload.Length);
        return packet;
    }

    private static void WriteUInt16BigEndian(byte[] buf, ref int pos, ushort value)
    {
        buf[pos++] = (byte)(value >> 8);
        buf[pos++] = (byte)value;
    }

    private static void WriteUInt32BigEndian(byte[] buf, ref int pos, uint value)
    {
        buf[pos++] = (byte)(value >> 24);
        buf[pos++] = (byte)(value >> 16);
        buf[pos++] = (byte)(value >> 8);
        buf[pos++] = (byte)value;
    }

    private static void WriteUInt64BigEndian(byte[] buf, ref int pos, ulong value)
    {
        buf[pos++] = (byte)(value >> 56);
        buf[pos++] = (byte)(value >> 48);
        buf[pos++] = (byte)(value >> 40);
        buf[pos++] = (byte)(value >> 32);
        buf[pos++] = (byte)(value >> 24);
        buf[pos++] = (byte)(value >> 16);
        buf[pos++] = (byte)(value >> 8);
        buf[pos++] = (byte)value;
    }

    private static void WriteVarint(System.IO.MemoryStream ms, ulong value)
    {
        while (value >= 0x80)
        {
            ms.WriteByte((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        ms.WriteByte((byte)value);
    }
}
