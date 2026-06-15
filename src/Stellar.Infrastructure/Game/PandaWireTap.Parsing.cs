using System;
using Stellar.Application.Abstractions;
using ZstdSharp;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Wire-header decode + payload preparation for <see cref="PandaWireTap"/>.
/// Pure parsing — no I/O, no Harmony, no reflection. Stays on the network
/// I/O thread but allocates only the payload byte[] (and the zstd decompress
/// intermediate when applicable).
/// </summary>
internal sealed partial class PandaWireTap
{
    // ZprotoMsgTypeId enum (low 15 bits of flags). Mirrors BPSR-B's
    // zproto_message_type catalog.
    private const ushort MsgTypeCall      = 1;
    private const ushort MsgTypeNotify    = 2;
    private const ushort MsgTypeReturn    = 3;
    private const ushort MsgTypeEcho      = 4;
    private const ushort MsgTypeFrameUp   = 5;
    private const ushort MsgTypeFrameDown = 6;

    private const int MaxFrameUnwrapDepth = 4;

    // First-time diagnostic flags. Plain bools — single-threaded on the
    // I/O path, so no interlock needed.
    private bool _zstdDecompressFailLogged;
    private bool _zstdUnwrapFailLogged;
    private bool _frameUnwrapDepthLogged;

    // Parsed RPC header fields. Stack-only struct — never allocates.
    private readonly struct WireHeader
    {
        public WireHeader(WireMessageKind kind, ulong serviceUuid, uint stubId, uint callId, uint methodId, uint errorCode)
        {
            Kind = kind;
            ServiceUuid = serviceUuid;
            StubId = stubId;
            CallId = callId;
            MethodId = methodId;
            ErrorCode = errorCode;
        }
        public WireMessageKind Kind { get; }
        public ulong ServiceUuid { get; }
        public uint StubId { get; }
        public uint CallId { get; }
        public uint MethodId { get; }
        public uint ErrorCode { get; }
    }

    // Decode the kind-specific header fields and produce the payload-start
    // offset. Returns false for unknown message types (Echo / FrameUp ack /
    // future expansion) and for frames too short to contain the kind's
    // mandatory header.
    private static bool TryParseWireHeader(
        ReadOnlySpan<byte> span,
        ushort msgTypeRaw,
        out WireHeader header,
        out int payloadOffset)
    {
        switch (msgTypeRaw)
        {
            case MsgTypeReturn: return TryReadReturnHeader(span, out header, out payloadOffset);
            case MsgTypeNotify: return TryReadNotifyHeader(span, out header, out payloadOffset);
            case MsgTypeCall:   return TryReadCallHeader(span, out header, out payloadOffset);
            default:
                // Unknown message type — caller skips silently (no consumer).
                header = default;
                payloadOffset = 0;
                return false;
        }
    }

    // Return wire layout (NO service_uuid — server-side correlation only):
    //   [size:4][flags:2][stub_id:4][call_id:4][error_id:4][payload]
    private static bool TryReadReturnHeader(ReadOnlySpan<byte> span, out WireHeader header, out int payloadOffset)
    {
        if (span.Length < 18) { header = default; payloadOffset = 0; return false; }
        uint stubId    = ((uint)span[6]  << 24) | ((uint)span[7]  << 16) | ((uint)span[8]  << 8) | span[9];
        uint callId    = ((uint)span[10] << 24) | ((uint)span[11] << 16) | ((uint)span[12] << 8) | span[13];
        uint errorCode = ((uint)span[14] << 24) | ((uint)span[15] << 16) | ((uint)span[16] << 8) | span[17];
        header = new WireHeader(WireMessageKind.Return, 0, stubId, callId, 0, errorCode);
        payloadOffset = 18;
        return true;
    }

    // Notify wire layout (no call_id — server push, no correlation):
    //   [size:4][flags:2][service_uuid:8][stub_id:4][method_id:4][payload]
    private static bool TryReadNotifyHeader(ReadOnlySpan<byte> span, out WireHeader header, out int payloadOffset)
    {
        if (span.Length < 22) { header = default; payloadOffset = 0; return false; }
        ulong serviceUuid =
            ((ulong)span[6]  << 56) | ((ulong)span[7]  << 48) | ((ulong)span[8]  << 40) | ((ulong)span[9]  << 32) |
            ((ulong)span[10] << 24) | ((ulong)span[11] << 16) | ((ulong)span[12] << 8)  | span[13];
        uint stubId   = ((uint)span[14] << 24) | ((uint)span[15] << 16) | ((uint)span[16] << 8) | span[17];
        uint methodId = ((uint)span[18] << 24) | ((uint)span[19] << 16) | ((uint)span[20] << 8) | span[21];
        header = new WireHeader(WireMessageKind.Notify, serviceUuid, stubId, 0, methodId, 0);
        payloadOffset = 22;
        return true;
    }

    // Call wire layout:
    //   [size:4][flags:2][service_uuid:8][stub_id:4][call_id:4][method_id:4][payload]
    private static bool TryReadCallHeader(ReadOnlySpan<byte> span, out WireHeader header, out int payloadOffset)
    {
        if (span.Length < 26) { header = default; payloadOffset = 0; return false; }
        ulong serviceUuid =
            ((ulong)span[6]  << 56) | ((ulong)span[7]  << 48) | ((ulong)span[8]  << 40) | ((ulong)span[9]  << 32) |
            ((ulong)span[10] << 24) | ((ulong)span[11] << 16) | ((ulong)span[12] << 8)  | span[13];
        uint stubId   = ((uint)span[14] << 24) | ((uint)span[15] << 16) | ((uint)span[16] << 8) | span[17];
        uint callId   = ((uint)span[18] << 24) | ((uint)span[19] << 16) | ((uint)span[20] << 8) | span[21];
        uint methodId = ((uint)span[22] << 24) | ((uint)span[23] << 16) | ((uint)span[24] << 8) | span[25];
        header = new WireHeader(WireMessageKind.Call, serviceUuid, stubId, callId, methodId, 0);
        payloadOffset = 26;
        return true;
    }

    /// <summary>
    /// FrameUp/FrameDown body = [sequence:u32][nested zproto packet(s)].
    /// Returns the (possibly zstd-decompressed) nested bytes, or false if malformed.
    /// </summary>
    private bool TryUnwrapNested(ReadOnlySpan<byte> span, bool isZstd, out byte[] nested)
    {
        nested = System.Array.Empty<byte>();
        if (span.Length < 10) return false;   // 4-byte size + 2-byte flags + 4-byte sequence
        var rest = span.Slice(10);
        if (rest.Length == 0) return true;
        if (!isZstd) { nested = rest.ToArray(); return true; }   // copies nested buffer per wrapper frame; acceptable because FrameUp/FrameDown are rare in this game — revisit if framing changes
        try
        {
            using var dec = new ZstdSharp.Decompressor();
            nested = dec.Unwrap(rest.ToArray()).ToArray();
            return true;
        }
        catch (System.Exception ex)
        {
            if (!_zstdUnwrapFailLogged)
            {
                _zstdUnwrapFailLogged = true;
                _log.Warning($"[WireTap] FrameDown zstd decompress failed: {ex.GetType().Name}: {ex.Message}");
            }
            return false;
        }
    }

    // Materialize the payload bytes. If zstd-compressed, decompress; otherwise
    // copy the slice into a managed byte[] so the envelope can outlive the
    // span (Dispatch may hand it to async consumers — even though the current
    // contract says handlers run synchronously on the I/O thread, the
    // materialised copy avoids subtle aliasing risk).
    private bool TryPreparePayload(
        ReadOnlySpan<byte> span,
        int payloadOffset,
        bool isZstdCompressed,
        in WireHeader header,
        out ReadOnlyMemory<byte> payload)
    {
        if (!isZstdCompressed)
        {
            payload = span.Slice(payloadOffset).ToArray();
            return true;
        }

        try
        {
            using var dec = new Decompressor();
            // Use ToArray then Unwrap — Decompressor doesn't accept Span/Memory.
            var rawArr = span.Slice(payloadOffset).ToArray();
            payload = dec.Unwrap(rawArr).ToArray();
            return true;
        }
        catch (Exception ex)
        {
            if (!_zstdDecompressFailLogged)
            {
                _zstdDecompressFailLogged = true;
                _log.Warning($"[WireTap] zstd decompress failed for kind={header.Kind} svc={header.ServiceUuid} method={header.MethodId} callId={header.CallId}: {ex.GetType().Name}: {ex.Message}");
            }
            payload = default;
            return false;
        }
    }
}
