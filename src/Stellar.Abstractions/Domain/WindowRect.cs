namespace Stellar.Abstractions.Domain;

/// <summary>Plain (X, Y, Width, Height) rectangle used for window positions and sizes.
/// Named <c>WindowRect</c> to avoid collision with <c>UnityEngine.Rect</c> in plugin/UI consumers.</summary>
public readonly record struct WindowRect(float X, float Y, float Width, float Height)
{
    /// <summary>X coordinate of the right edge (X + Width).</summary>
    public float Right  => X + Width;
    /// <summary>Y coordinate of the bottom edge (Y + Height).</summary>
    public float Bottom => Y + Height;

    /// <summary>Returns true when the point (<paramref name="px"/>, <paramref name="py"/>) lies within or on the border of this rectangle.</summary>
    public bool Contains(float px, float py) =>
        px >= X && px <= Right && py >= Y && py <= Bottom;
}
