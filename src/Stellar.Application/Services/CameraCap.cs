using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Services;

/// <summary>The framework's hard limit (spec § 7): the override camera never goes more than 60 m from the local player.</summary>
internal static class CameraCap
{
    internal const float MaxDistance = 60f;

    /// <summary><paramref name="desired"/> projected onto the 60 m sphere around <paramref name="player"/> when outside it;
    /// <paramref name="last"/> when the player's position is unknown (never move blind).</summary>
    internal static Position3D Clamp(Position3D desired, Position3D? player, Position3D last)
    {
        if (player is not Position3D p) return last;
        float dx = desired.X - p.X, dy = desired.Y - p.Y, dz = desired.Z - p.Z;
        var d2 = dx * dx + dy * dy + dz * dz;
        if (d2 <= MaxDistance * MaxDistance) return desired;
        var k = MaxDistance / MathF.Sqrt(d2);
        return new Position3D(p.X + dx * k, p.Y + dy * k, p.Z + dz * k);
    }
}
