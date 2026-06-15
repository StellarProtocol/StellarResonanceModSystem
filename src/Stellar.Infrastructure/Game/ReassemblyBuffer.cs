using System;

namespace Stellar.Infrastructure.Game;

/// <summary>
/// Per-connection byte buffer for stitching recv() chunks into complete
/// logical packets. Owned by a single ZTcpClient instance so accesses
/// from the network I/O thread are single-threaded for that client.
/// </summary>
internal sealed class ReassemblyBuffer
{
    public byte[] Data = new byte[4096];
    public int Length;

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
    }

    public void Drop(int n)
    {
        if (n >= Length) { Length = 0; return; }
        System.Buffer.BlockCopy(Data, n, Data, 0, Length - n);
        Length -= n;
    }
}
