using System;
namespace Stellar.Application.Abstractions;

/// <summary>Game side of <c>ISceneFreeze</c>: the game clock's time pause, the drawn-position hold and the deferred removal of
/// monsters killed while paused. Main thread.</summary>
internal interface ISceneFreezeBackend
{
    /// <summary>Installs the time-scale and removal hooks on first use (never at boot).</summary>
    void EnsureHooks();
    void FreezeAll(bool holdPositions);
    void UnfreezeAll();
    /// <summary>True while drawn positions are being held.</summary>
    bool HoldsPositions { get; }
    /// <summary>Raised when the position hold turned itself off (over the frame budget or a failure).</summary>
    event Action? HoldDisabled;
}
