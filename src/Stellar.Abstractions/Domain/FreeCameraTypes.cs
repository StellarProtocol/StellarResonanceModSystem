namespace Stellar.Abstractions.Domain;

/// <summary>A camera pose in world space. Angles follow Unity's convention, in degrees.</summary>
/// <param name="Position">World position of the camera.</param>
/// <param name="Yaw">Heading around world up; 0 looks along +Z, 90 along +X.</param>
/// <param name="Pitch">Up/down angle; positive looks down.</param>
/// <param name="Roll">Roll (Dutch angle); positive tilts clockwise as seen by the player.</param>
/// <param name="Fov">Vertical field of view.</param>
public readonly record struct CameraPose(Position3D Position, float Yaw, float Pitch, float Roll, float Fov);

/// <summary>Why a camera override ended.</summary>
public enum CameraReleaseReason
{
    /// <summary>The holder disposed its control.</summary>
    Disposed,
    /// <summary>A zone change or loading screen.</summary>
    SceneChanged,
    /// <summary>A cutscene started.</summary>
    Cutscene,
    /// <summary>The game's own camera / selfie mode opened.</summary>
    GamePhotoMode,
    /// <summary>The player logged out or was disconnected.</summary>
    Disconnected,
    /// <summary>The holding plugin was disabled or unloaded.</summary>
    PluginUnloaded,
    /// <summary>The holder's per-frame code threw, or the scene freeze's watchdog found its pause lost (the framework tick
    /// stalled, or its paused-frame driver was destroyed); the framework released the camera.</summary>
    Error,
}

/// <summary>One emote (pose) the local player has unlocked, as the game's emote wheel lists it.</summary>
/// <param name="Id">The game's emote/action id.</param>
/// <param name="Name">Display name in the client's language.</param>
/// <param name="IconPath">Game asset path of the icon (load it with <c>IGameAssets.LoadByPath</c>).</param>
/// <param name="Looping">True for a held (looping) pose, false for a one-shot action.</param>
public sealed record EmoteInfo(int Id, string Name, string IconPath, bool Looping);

/// <summary>Outcome of asking the game to play an emote.</summary>
public enum EmoteResult
{
    /// <summary>The game accepted and played it.</summary>
    Played,
    /// <summary>The game's own checks refused it; the game shows its own message.</summary>
    Refused,
    /// <summary>The emote system is not available right now (not in the world yet).</summary>
    Unavailable,
    /// <summary>The call failed unexpectedly (logged).</summary>
    Failed,
}
