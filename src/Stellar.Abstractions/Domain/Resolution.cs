namespace Stellar.Abstractions.Domain;

/// <summary>Screen or texture resolution in integer pixels.</summary>
/// <param name="Width">Horizontal pixel count.</param>
/// <param name="Height">Vertical pixel count.</param>
public readonly record struct Resolution(int Width, int Height)
{
    /// <summary>String key in the form "WIDTHxHEIGHT" (e.g. "1920x1080"), usable as a dictionary key.</summary>
    public string Key => $"{Width}x{Height}";

    /// <summary>Euclidean distance in pixels.</summary>
    public double DistanceTo(Resolution other)
    {
        var dw = (double)(Width  - other.Width);
        var dh = (double)(Height - other.Height);
        return System.Math.Sqrt(dw * dw + dh * dh);
    }
}
