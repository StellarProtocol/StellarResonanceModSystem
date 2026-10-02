using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Pauses the whole game world while any token is held: the game's clock stops, so every character — yours included — NPCs,
/// pets, mounts, monsters, skills mid-cast, effects and particles hold still where they are. Players and monsters the server
/// moves while paused stay where they were drawn (unless turned off — <see cref="HoldsPositions"/>). The framework, its
/// windows and hotkeys, plugin updates, the free camera and screen capture keep running, and so do the network and the
/// server's world: a fight goes on server-side, and damage or a death taken meanwhile shows when the world runs again. A
/// monster killed while paused stays visible until the pause ends. Local only. Reference-counted; the framework releases
/// every token on a zone change, a cutscene, the game's own camera mode, a logout (a disconnect is seen only as the game's
/// logout, <see cref="IClientState.Logout"/>) and the holder's unload — never on a free-camera release — and releases every
/// token on its own if a pause is ever lost (outside the world, a stalled framework tick). Main thread only.
/// <para><b>Unity's scaled time stops for every plugin while frozen</b>, not only for the game: <c>Time.deltaTime</c> is 0
/// and <c>Time.time</c> stands still, so <c>WaitForSeconds</c>, <c>UniTask.Delay</c> with its default
/// <c>DelayType.DeltaTime</c> and any animation driven by scaled time wait until the world runs again. Use
/// <c>Time.unscaledDeltaTime</c> / <c>Time.realtimeSinceStartup</c>, <c>WaitForSecondsRealtime</c>,
/// <c>DelayType.Realtime</c> or the framework's own timers for anything that must keep running.</para>
/// <para>Known limits: the position hold covers players and monsters within 80 m of the local player (farther ones may glide
/// in their paused pose); the server still drives the local player's hit reactions, so your own character can re-pose in
/// stop-motion when hit while frozen. A <see cref="Changed"/> handler that throws is logged and skipped; it never keeps the
/// freeze from starting or ending.</para>
/// </summary>
public interface ISceneFreeze
{
    /// <summary>Pauses the world until the returned token is disposed.</summary>
    IDisposable Freeze();

    /// <summary>True while the world is paused.</summary>
    bool IsFrozen { get; }

    /// <summary>True while the drawn positions of other players and monsters are being held in place (false when turned off
    /// or over budget: then a player or monster the server moves glides, in a paused pose).</summary>
    bool HoldsPositions { get; }

    /// <summary>Raised when <see cref="IsFrozen"/> or <see cref="HoldsPositions"/> changes; the argument is <see cref="IsFrozen"/>.</summary>
    event Action<bool>? Changed;
}
