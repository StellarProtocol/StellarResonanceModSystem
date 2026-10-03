using System;
namespace Stellar.Application.Imaging;

/// <summary>Alpha fix-up for an RGBA32 frame. Probe fact (ReShade design § 11.1): after ReShade draws into the capture
/// target, its alpha is 0 on every pixel while RGB is correct, so a PNG of it would be fully transparent.</summary>
internal static class RgbaAlpha
{
    /// <summary>Sets every pixel's alpha (each 4th byte) to 255 in place; RGB is untouched. Null/empty is a no-op.</summary>
    /// <exception cref="ArgumentException">The buffer is not a whole number of RGBA pixels.</exception>
    internal static void ForceOpaque(byte[]? rgba)
    {
        if (rgba is null || rgba.Length == 0) return;
        if (rgba.Length % 4 != 0) throw new ArgumentException("An RGBA32 buffer holds whole 4-byte pixels.", nameof(rgba));
        // Whole pixels as uint: OR in the alpha byte (byte 3 of each pixel = the high byte on little-endian).
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(rgba.AsSpan());
        var mask = BitConverter.IsLittleEndian ? 0xFF000000u : 0x000000FFu;
        for (var i = 0; i < pixels.Length; i++) pixels[i] |= mask;
    }
}
