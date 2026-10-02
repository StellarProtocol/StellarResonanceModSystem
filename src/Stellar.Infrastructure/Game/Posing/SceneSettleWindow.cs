using System;
using System.Diagnostics;
namespace Stellar.Infrastructure.Game.Posing;

/// <summary>
/// The scene-change settle window (docs/il2cpp-probing-safety.md § How to gate it #2): for ~2 s after a scene leave or
/// enter the game tears down and rebuilds every entity and model, and a reflected read can land on a freed object. While
/// <see cref="Settling"/>, posing reads nothing live — no person listing, no kind lookup, no open, no per-frame position.
/// A time comparison on read (armed by the scene events, never polled). Pure (unit-tested). Main thread.
/// </summary>
internal sealed class SceneSettleWindow
{
    /// <summary>The window the replay probe uses (<c>IsWithinReplaySettle</c>).</summary>
    internal static readonly TimeSpan Span = TimeSpan.FromSeconds(2);

    private readonly Func<long> _now;
    private readonly long _span;
    private long _until;
    private bool _armed;

    public SceneSettleWindow() : this(Stopwatch.GetTimestamp, (long)(Span.TotalSeconds * Stopwatch.Frequency)) { }

    public SceneSettleWindow(Func<long> nowTicks, long spanTicks)
    {
        _now = nowTicks;
        _span = spanTicks;
    }

    /// <summary>True inside the window after the latest <see cref="Arm"/>. Note (review M3): this getter MUTATES — the
    /// first read after the window has passed drops the armed latch. That is safe because the latch only short-circuits
    /// the clock read: once expired the answer stays false until the next <see cref="Arm"/>, whoever reads first and
    /// however often, so no reader can observe a different value because of another reader.</summary>
    public bool Settling
    {
        get
        {
            if (!_armed) return false;
            if (_now() < _until) return true;
            _armed = false;
            return false;
        }
    }

    /// <summary>A scene change (leave or enter): (re)starts the window.</summary>
    public void Arm()
    {
        _until = _now() + _span;
        _armed = true;
    }
}

/// <summary>Runs an open's <c>loaded</c> callback exactly once whatever the path (a synchronous NPC callback inside the
/// request, a failure after it, an exception) — <c>IPosingBackend.Open</c>'s contract. Pure (unit-tested).</summary>
internal sealed class LoadedOnce
{
    private readonly Action<bool> _loaded;

    public LoadedOnce(Action<bool> loaded) => _loaded = loaded;

    public bool Reported { get; private set; }

    public void Report(bool ok)
    {
        if (Reported) return;
        Reported = true;
        _loaded(ok);
    }
}
