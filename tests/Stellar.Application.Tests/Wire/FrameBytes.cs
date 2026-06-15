using System.IO;

namespace Stellar.Application.Tests.Wire;

/// <summary>
/// Builds complete length-prefixed Zproto frames for tests.
/// Outer: [size:u32 BE][flags:u16 BE], flags = (isZstd&lt;&lt;15)|msgType.
/// Bodies match openbpsr-bot/src/bpsr_client/packet.py.
/// </summary>
internal static class FrameBytes
{
    private const ushort Call = 1, Notify = 2, Return = 3, FrameDown = 6;

    public static byte[] ReturnFrame(uint stubId, uint callId, uint errorId, byte[] payload)
    {
        var body = new MemoryStream();
        WriteU16(body, Return, isZstd: false);
        WriteU32(body, stubId); WriteU32(body, callId); WriteU32(body, errorId);
        body.Write(payload, 0, payload.Length);
        return Frame(body.ToArray());
    }

    public static byte[] NotifyFrame(ulong svc, uint stub, uint method, byte[] payload)
    {
        var body = new MemoryStream();
        WriteU16(body, Notify, isZstd: false);
        WriteU64(body, svc); WriteU32(body, stub); WriteU32(body, method);
        body.Write(payload, 0, payload.Length);
        return Frame(body.ToArray());
    }

    public static byte[] CallFrame(ulong svc, uint stub, uint callId, uint method, byte[] payload)
    {
        var body = new MemoryStream();
        WriteU16(body, Call, isZstd: false);
        WriteU64(body, svc); WriteU32(body, stub); WriteU32(body, callId); WriteU32(body, method);
        body.Write(payload, 0, payload.Length);
        return Frame(body.ToArray());
    }

    /// <summary>Wrap one-or-more already-built frames in a FrameDown wrapper.</summary>
    public static byte[] FrameDownWrapping(uint sequence, byte[] nestedFrames, bool zstd = false)
    {
        var body = new MemoryStream();
        WriteU16(body, FrameDown, isZstd: zstd);
        WriteU32(body, sequence);
        var inner = zstd ? Zstd(nestedFrames) : nestedFrames;
        body.Write(inner, 0, inner.Length);
        return Frame(body.ToArray());
    }

    private static byte[] Zstd(byte[] raw)
    {
        using var c = new ZstdSharp.Compressor();
        return c.Wrap(raw).ToArray();
    }

    private static byte[] Frame(byte[] bodyWithFlags)
    {
        var ms = new MemoryStream();
        WriteU32(ms, (uint)(bodyWithFlags.Length + 4));
        ms.Write(bodyWithFlags, 0, bodyWithFlags.Length);
        return ms.ToArray();
    }

    private static void WriteU16(Stream s, ushort msgType, bool isZstd)
    {
        ushort flags = (ushort)((isZstd ? 0x8000 : 0) | msgType);
        s.WriteByte((byte)(flags >> 8)); s.WriteByte((byte)flags);
    }
    private static void WriteU32(Stream s, uint v)
    { s.WriteByte((byte)(v>>24)); s.WriteByte((byte)(v>>16)); s.WriteByte((byte)(v>>8)); s.WriteByte((byte)v); }
    private static void WriteU64(Stream s, ulong v)
    { for (int i = 56; i >= 0; i -= 8) s.WriteByte((byte)(v>>i)); }
}
