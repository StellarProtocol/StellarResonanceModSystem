using System;
using Stellar.Abstractions.Domain;
namespace Stellar.Abstractions.Services;

/// <summary>Reference-counted hiding of scene layers. Main thread only.</summary>
/// <remarks>
/// Several of these switches are shared with the game itself (its own photo/camera mode and cutscenes toggle
/// nameplates, other-player visibility and parts of the HUD). The framework re-applies every held layer when the
/// game's photo mode or a cutscene ends, when the game re-initialises the UI root, the nameplate manager or the
/// camera-frame controller, on the nameplate manager's scene entry, and when a Stellar canvas is rebuilt.
/// Known limits: a game path that resets one of these switches WITHOUT any of those signals can undo a hide until
/// the next re-apply; other-player visibility restores the last value the game itself set through the camera-frame
/// switch (changes the game makes through any other mechanism are not seen), and treats a type the game never set
/// as shown.
/// </remarks>
public interface ISceneVisibility
{
    /// <summary>Hides <paramref name="layers"/> until the returned token is disposed.</summary>
    IDisposable Hide(VisibilityLayers layers);
    /// <summary>Layers actually hidden right now (unsupported layers are never reported).</summary>
    VisibilityLayers Hidden { get; }
    /// <summary>
    /// Layers this client can currently drive at all — not whether they're hidden, just whether hiding them would
    /// do anything. Use this to grey out a layer's control before the player clicks it. Optimistic (reports a layer
    /// available) until the game's own reflection target for it has been probed and found missing; refined once
    /// that probe runs. A layer never present in <see cref="Hidden"/> can still be reported available here.
    /// </summary>
    VisibilityLayers Available { get; }
    /// <summary>Raised when <see cref="Hidden"/> changes.</summary>
    event Action<VisibilityLayers>? Changed;
}
