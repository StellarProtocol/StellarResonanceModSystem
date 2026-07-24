using System;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Per-connection byte buffer for stitching recv() chunks into complete
/// logical packets. Owned by a single ZTcpClient instance so accesses
/// from the network I/O thread are single-threaded for that client.
/// </summary>
internal sealed class ReassemblyBuffer
{
    private const int InitialBytes = 4096;
    // A buffer grown past this by one large packet (history reply, big sync) drops back to
    // InitialBytes once fully drained, instead of pinning the high-water mark forever.
    private const int ShrinkThresholdBytes = 256 * 1024;

    public byte[] Data = new byte[InitialBytes];
    public int Length;

    /// <summary>Monotonic ms (Environment.TickCount64) of the last Append — recency signal for
    /// evicting buffers whose connection died (relog/reconnect) and will never drain again.</summary>
    public long LastTouchedMs;

    public void Append(byte[] chunk)
    {
        if (Length + chunk.Length > Data.Length)
        {
            int newSize = Data.Length * 2;
            while (newSize < Length + chunk.Length) newSize *= 2;
            Array.Resize(ref Data, newSize);
        }
        System.Buffer.BlockCopy(chunk, 0, Data, Length, chunk.Length);
        Length += chunk.Length;
        LastTouchedMs = Environment.TickCount64;
    }

    public void Drop(int n)
    {
        if (n >= Length) { Length = 0; return; }
        System.Buffer.BlockCopy(Data, n, Data, 0, Length - n);
        Length -= n;
    }

    /// <summary>Release a large-packet high-water allocation once the buffer is empty.</summary>
    public void ShrinkIfDrained()
    {
        if (Length == 0 && Data.Length > ShrinkThresholdBytes) Data = new byte[InitialBytes];
    }
}
