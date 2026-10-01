using System;
using System.Diagnostics.CodeAnalysis;
using Stellar.Abstractions.Domain;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Exclusive takeover of the game camera for a free camera. Main thread only. One holder at a time; a second
/// <see cref="TryAcquire"/> returns false. The framework ends the override on a zone change, a cutscene, the game's
/// own camera mode opening, a disconnect, the holder's unload, or an exception in the holder's
/// <see cref="ICameraControl.Frame"/> handler, and raises <see cref="Released"/> with the reason. The camera is
/// never placed more than 60 m from the local player, whatever the holder asks for.
/// </summary>
public interface ICameraOverride
{
    /// <summary>Takes the camera, starting at the game camera's current pose. False when another holder has it, the
    /// game camera is not available, or the free camera is turned off on this client.</summary>
    /// <param name="control">The control while held; null on failure.</param>
    bool TryAcquire([NotNullWhen(true)] out ICameraControl? control);

    /// <summary>True while any holder has the camera.</summary>
    bool IsOverridden { get; }

    /// <summary>Raised after an override ends, with the reason.</summary>
    event Action<CameraReleaseReason>? Released;

    /// <summary>Turns the local player's head toward the camera until disposed; the previous look-at state is restored
    /// on dispose (and on unload). Works with or without an override.</summary>
    IDisposable LookAtCamera();
}

/// <summary>A held camera override. Dispose to hand the camera back (the game blends back to its own camera).</summary>
public interface ICameraControl : IDisposable
{
    /// <summary>Places the camera. Roll is clamped to ±90°. The position is clamped to 60 m from the local player.</summary>
    /// <param name="position">World position.</param>
    /// <param name="yaw">Heading in degrees (0 = +Z).</param>
    /// <param name="pitch">Degrees; positive looks down.</param>
    /// <param name="roll">Degrees of roll.</param>
    void SetPose(Position3D position, float yaw, float pitch, float roll);

    /// <summary>Vertical field of view in degrees.</summary>
    float Fov { get; set; }

    /// <summary>The game camera's pose when this control was acquired (for "reset to entry pose").</summary>
    CameraPose GamePose { get; }

    /// <summary>True until disposed or released by the framework.</summary>
    bool IsActive { get; }

    /// <summary>Raised once per rendered frame while active, before the camera is drawn; the argument is the frame's
    /// unscaled delta time in seconds. Set the pose here. An exception releases the camera with
    /// <see cref="CameraReleaseReason.Error"/>.</summary>
    event Action<float>? Frame;
}
