using System;
using System.Collections.Generic;
using System.Threading.Tasks;
namespace Stellar.Infrastructure.Rendering;

/// <summary>
/// Hand-off of "resume on the main thread" requests (usually made from a thread-pool thread after the off-thread
/// encode/write) to the main thread. A successful grab <see cref="Expect"/>s one resume; its pump runs only while an
/// expected resume is outstanding (<see cref="PumpShouldRun"/>) and stops the moment it is served — no idle per-frame
/// work. A request that arrives with no pump live stays queued (<see cref="HasQueued"/>) for the framework's main-thread
/// tick to <see cref="Drain"/>; nothing here ever completes a request on the requesting thread. Pure — unit-tested.
/// </summary>
internal sealed class ResumeQueue
{
    /// <summary>Leak guard, not a deadline: a pump still waiting after this long logs loudly and stands down.</summary>
    public const float LeakGuardSeconds = 600f;

    private readonly object _gate = new();
    private readonly List<TaskCompletionSource<bool>> _queued = new();
    private int _expected;
    private int _pumps;
    private volatile bool _hasQueued;

    /// <summary>Live pump coroutines.</summary>
    public int Pumps { get { lock (_gate) return _pumps; } }

    /// <summary>Lock-free check for the main-thread tick: is anything waiting to be completed?</summary>
    public bool HasQueued => _hasQueued;

    /// <summary>True while an expected resume is outstanding or a request is waiting.</summary>
    public bool PumpShouldRun { get { lock (_gate) return _expected > 0 || _queued.Count > 0; } }

    /// <summary>A grab succeeded on the main thread: exactly one resume will follow.</summary>
    public void Expect() { lock (_gate) _expected++; }

    public void PumpStarted() { lock (_gate) _pumps++; }

    public void PumpStopped() { lock (_gate) _pumps = Math.Max(0, _pumps - 1); }

    /// <summary>Any thread: queue a request for the main thread.</summary>
    public void Enqueue(TaskCompletionSource<bool> tcs)
    {
        lock (_gate)
        {
            _queued.Add(tcs);
            _hasQueued = true;
        }
    }

    /// <summary>Main thread: completes every queued request (continuations run inline, here) and returns how many.</summary>
    public int Drain()
    {
        TaskCompletionSource<bool>[] ready;
        lock (_gate)
        {
            if (_queued.Count == 0) return 0;
            ready = _queued.ToArray();
            _queued.Clear();
            _hasQueued = false;
            _expected = Math.Max(0, _expected - ready.Length);
        }
        foreach (var r in ready) r.TrySetResult(true);
        return ready.Length;
    }

    /// <summary>Leak guard tripped: stop expecting (queued requests are still drained by the tick).</summary>
    public void Abandon() { lock (_gate) _expected = 0; }

    /// <summary>The host is gone: fault every queued request and reset.</summary>
    public void FailAll(Exception ex)
    {
        TaskCompletionSource<bool>[] ready;
        lock (_gate)
        {
            ready = _queued.ToArray();
            _queued.Clear();
            _hasQueued = false;
            _expected = 0;
            _pumps = 0;
        }
        foreach (var r in ready) r.TrySetException(ex);
    }

    public static bool LeakGuardExpired(float startedAt, float now) => now - startedAt > LeakGuardSeconds;
}
