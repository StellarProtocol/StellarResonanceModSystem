using System;
namespace Stellar.Abstractions.Services;

/// <summary>
/// Pins the in-world clock (time of day) to a chosen hour, then hands it back to the server. Shared by every
/// plugin. Main thread only.
/// </summary>
/// <remarks>
/// Interiors and cutscenes set the clock themselves; while a pin is held the framework re-asserts it after those
/// (event-driven). The clock is driven through the game's own time-of-day calls; the server clock itself is never
/// touched. Pins a plugin still holds are released when it unloads.
/// </remarks>
public interface ITimeOfDay
{
    /// <summary>
    /// Pins the clock to <paramref name="hour"/> (0..24, clamped) until the pin is disposed. The newest held pin
    /// wins; disposing it falls back to the previous held pin, and disposing the last one hands time back to the
    /// server.
    /// </summary>
    ITimePin Pin(float hour);
    /// <summary>The in-world hour right now (0..24), or 0 when it cannot be read.</summary>
    float CurrentHour { get; }
    /// <summary>Whether this client can pin the clock at all (grey the control out when false).</summary>
    bool IsAvailable { get; }
}

/// <summary>A held time-of-day pin. Dispose to release it.</summary>
public interface ITimePin : IDisposable
{
    /// <summary>Changes this pin's hour (0..24, clamped). Applied at once when this is the newest held pin;
    /// otherwise remembered for when it becomes the newest again. No-op once disposed.</summary>
    void SetHour(float hour);
    /// <summary>True until disposed.</summary>
    bool IsActive { get; }
}
