using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace Stellar.Application.Services;

/// <summary>
/// Backing store for <see cref="Stellar.Abstractions.Services.IFrameworkTiming.Post"/> and
/// <see cref="Stellar.Abstractions.Services.IFrameworkTiming.Every"/>. Owned by each
/// <see cref="IFramework"/> implementation (the shared <see cref="FrameworkService"/> and every
/// per-plugin <c>PerPluginFramework</c>); the owner calls <see cref="Drain"/> once per its Update tick so
/// posted actions and downsampled timers run on the same (main) thread the tick runs on.
/// </summary>
internal sealed class FrameDispatch
{
    private readonly ConcurrentQueue<Action> _posts = new();
    private readonly List<EveryTimer> _timers = new();
    private readonly object _timersLock = new();

    public void Post(Action action)
    {
        if (action is not null) _posts.Enqueue(action);
    }

    public IDisposable Every(TimeSpan interval, Action action)
    {
        if (action is null) return NullScope.Instance;
        var seconds = interval.TotalSeconds;
        if (seconds < 0) seconds = 0;
        var timer = new EveryTimer(this, seconds, action);
        lock (_timersLock) _timers.Add(timer);
        return timer;
    }

    internal void Remove(EveryTimer timer)
    {
        lock (_timersLock) _timers.Remove(timer);
    }

    /// <summary>Runs every queued post exactly once, then advances every live timer by
    /// <paramref name="dt"/> seconds. Exceptions from user callbacks are swallowed so one bad callback can't
    /// break the tick loop. Call once per Update tick, on the tick thread.</summary>
    public void Drain(float dt)
    {
        // Snapshot the count so an action that re-Posts itself defers to the next tick (a self-re-posting
        // action must not loop within one tick — "runs on the next Update tick" is the contract).
        int n = _posts.Count;
        for (int i = 0; i < n && _posts.TryDequeue(out var action); i++)
        {
            try { action(); }
            catch { /* fire-and-forget: never break the tick on a plugin's post */ }
        }

        EveryTimer[] snapshot;
        lock (_timersLock)
        {
            if (_timers.Count == 0) return;
            snapshot = _timers.ToArray();
        }
        foreach (var timer in snapshot)
            timer.Tick(dt);
    }

    internal sealed class EveryTimer : IDisposable
    {
        private readonly FrameDispatch _owner;
        private readonly double _interval;
        private readonly Action _action;
        private double _accum;
        private bool _disposed;

        internal EveryTimer(FrameDispatch owner, double interval, Action action)
        {
            _owner = owner;
            _interval = interval;
            _action = action;
        }

        internal void Tick(float dt)
        {
            if (_disposed) return;
            _accum += dt;
            if (_accum < _interval) return;
            // Fire once per drain even if several intervals elapsed (downsample, don't burst-catch-up).
            _accum = 0;
            try { _action(); }
            catch { /* swallow: keep the timer alive across a throwing callback */ }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner.Remove(this);
        }
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}
