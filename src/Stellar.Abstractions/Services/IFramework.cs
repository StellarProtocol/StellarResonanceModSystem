using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Per-frame callbacks driven by the game's main update loop.
/// </summary>
public interface IFramework
{
    /// <summary>Fired once per game frame. Argument is deltaTime in seconds.</summary>
    event Action<float> Update;

    /// <summary>Monotonic frame counter incremented before each <see cref="Update"/> dispatch.</summary>
    long FrameCount { get; }

    /// <summary>Current display width in pixels. Updated once per frame before <see cref="Update"/> fires.</summary>
    int ScreenWidth { get; }

    /// <summary>Current display height in pixels. Updated once per frame before <see cref="Update"/> fires.</summary>
    int ScreenHeight { get; }
}
