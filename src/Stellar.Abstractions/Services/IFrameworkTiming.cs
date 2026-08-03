using System;

namespace Stellar.Abstractions.Services;

/// <summary>
/// Timing / main-thread-marshalling surface of <see cref="IFramework"/>. Split out from the frame/screen
/// metrics so each interface stays within the framework's shape budget; consumers reach these through
/// <c>services.Framework</c> exactly as if they were declared on <see cref="IFramework"/>.
/// </summary>
public interface IFrameworkTiming
{
    /// <summary>Queues <paramref name="action"/> to run once on the next Update tick of this plugin. Thread-safe:
    /// the marshal is the supported way to hop work collected on a background thread (chat/network callbacks)
    /// onto the Unity main thread before it touches the game.</summary>
    /// <param name="action">Work to run on the next tick; exceptions are swallowed.</param>
    void Post(Action action);

    /// <summary>Registers a recurring callback fired at most once per <paramref name="interval"/>, downsampled
    /// from this plugin's Update tick (so it never runs faster than the plugin ticks). Dispose the returned
    /// scope to cancel. Replaces the hand-rolled <c>_accum += dt; if (_accum &gt;= period)</c> idiom.</summary>
    /// <param name="interval">Minimum time between invocations.</param>
    /// <param name="action">Work to run each interval; exceptions are swallowed.</param>
    IDisposable Every(TimeSpan interval, Action action);

    /// <summary>Monotonic seconds since framework start. Backed by a process stopwatch — it never reads
    /// <c>UnityEngine.Time</c>, so it cannot throw the IL2CPP "get_realtimeSinceStartup can only be called from
    /// the main thread" exception and is safe to read from any thread. Use for relative timing only (the epoch
    /// is arbitrary).</summary>
    float TimeNow { get; }
}
