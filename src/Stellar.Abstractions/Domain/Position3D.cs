namespace Stellar.Abstractions.Domain;

/// <summary>
/// World-space position of a game entity. Plain C# struct — intentionally has no
/// Unity dependency so <see cref="Services.IPlayerState"/> stays in
/// the BCL-only contracts layer.
/// </summary>
public readonly struct Position3D
{
    /// <summary>World-space X (east) coordinate.</summary>
    public float X { get; init; }
    /// <summary>World-space Y (up / elevation) coordinate.</summary>
    public float Y { get; init; }
    /// <summary>World-space Z (north) coordinate.</summary>
    public float Z { get; init; }

    /// <summary>Constructs a position from its three world-space components.</summary>
    public Position3D(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <summary>Origin (0, 0, 0) — default / unknown position.</summary>
    public static Position3D Zero => default;
}
