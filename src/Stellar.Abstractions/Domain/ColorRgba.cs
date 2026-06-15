namespace Stellar.Abstractions.Domain;

/// <summary>Plain RGBA float colour. Mirrors UnityEngine.Color shape without depending on Unity.</summary>
public readonly record struct ColorRgba(float R, float G, float B, float A)
{
    /// <summary>Constructs an opaque colour from red, green, and blue components (alpha defaults to 1).</summary>
    public ColorRgba(float r, float g, float b) : this(r, g, b, 1f) { }

    /// <summary>Constructs a colour from a packed 32-bit RGBA hex value (e.g. <c>0xFF8040FF</c>).</summary>
    public static ColorRgba FromHex(uint rgba)
    {
        var r = ((rgba >> 24) & 0xff) / 255f;
        var g = ((rgba >> 16) & 0xff) / 255f;
        var b = ((rgba >> 8)  & 0xff) / 255f;
        var a = ( rgba        & 0xff) / 255f;
        return new ColorRgba(r, g, b, a);
    }
}
