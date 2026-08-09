using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Per-frame callbacks driven by the game's main update loop. Timing / main-thread-marshalling members
/// (<see cref="IFrameworkTiming.Post"/>, <see cref="IFrameworkTiming.Every"/>,
/// <see cref="IFrameworkTiming.TimeNow"/>) are inherited from <see cref="IFrameworkTiming"/>.
/// </summary>
public interface IFramework : IFrameworkTiming
{
    /// <summary>Fired once per game frame. Argument is deltaTime in seconds.</summary>
    event Action<float> Update;

    /// <summary>Monotonic frame counter incremented before each <see cref="Update"/> dispatch.</summary>
    long FrameCount { get; }

    /// <summary>Current display width in pixels. Updated once per frame before <see cref="Update"/> fires.</summary>
    int ScreenWidth { get; }

    /// <summary>Current display height in pixels. Updated once per frame before <see cref="Update"/> fires.</summary>
    int ScreenHeight { get; }

    /// <summary>Width of the window overlay in CANVAS UNITS (design space) = ScreenWidth ÷ UI scaleFactor. Position
    /// and size windows in these units so they scale with the UI (WindowRect is in canvas units). For HUD sizing
    /// that must track physical pixels, use <see cref="ScreenWidth"/> instead.</summary>
    int CanvasWidth { get; }

    /// <summary>Height of the window overlay in CANVAS UNITS (design space) = ScreenHeight ÷ UI scaleFactor.
    /// See <see cref="CanvasWidth"/>.</summary>
    int CanvasHeight { get; }

    /// <summary>The rate this plugin is currently ticking at (Hz). Reflects the user's per-plugin
    /// config plus any dynamic ramp this plugin is currently holding.</summary>
    int EffectiveUpdateRateHz { get; }

    /// <summary>Ask the framework to tick THIS plugin at no less than <paramref name="hz"/> until the
    /// returned scope is disposed. Requests stack (the maximum wins). The value is clamped to the
    /// supported range. Returns an inert (no-op) scope unless the user granted this plugin
    /// rate-control permission, so calling it is always safe.</summary>
    /// <param name="hz">Requested minimum tick rate in Hz.</param>
    IUpdateRateScope RequestUpdateRate(int hz);
}
