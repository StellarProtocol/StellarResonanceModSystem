using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Application.Abstractions;

/// <summary>Game side of <c>ICameraOverride</c>: a top-priority virtual camera plus a per-render-frame tick. Main thread.</summary>
internal interface ICameraBackend
{
    /// <summary>The game camera's pose now, or null when there is no game camera.</summary>
    CameraPose? ReadGamePose();
    /// <summary>The local player's logical (server) position, or null when unknown.</summary>
    Position3D? ReadLocalPlayerPosition();
    /// <summary>Creates and enables the override camera at <paramref name="start"/>; false when the game refuses.
    /// Idempotent: returns true without creating a second camera if one is already live.</summary>
    bool TryBegin(CameraPose start);
    /// <summary>Moves the override camera.</summary>
    void Apply(CameraPose pose);
    /// <summary>Destroys the override camera; the game blends back to its own.</summary>
    void End();
    /// <summary>Raised once per rendered frame between <see cref="TryBegin"/> and <see cref="End"/>.</summary>
    event Action<float>? Frame;
}
