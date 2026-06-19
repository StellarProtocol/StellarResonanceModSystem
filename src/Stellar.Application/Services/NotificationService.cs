using System;
using System.Collections.Generic;
using System.Diagnostics;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>One active toast: its text, kind, and the monotonic-clock time (seconds) at which it expires.</summary>
/// <param name="Message">The text to display.</param>
/// <param name="Kind">Severity / intent, used by the renderer to pick a colour.</param>
/// <param name="ExpiresAt">Monotonic-clock time (seconds) past which the toast should disappear.</param>
public readonly record struct ActiveToast(string Message, NotificationKind Kind, double ExpiresAt);

/// <summary>
/// <see cref="INotifications"/> implementation — a thread-safe queue of transient toasts.
/// <see cref="Notify"/> enqueues a toast expiring <c>now + (seconds ?? DefaultSeconds)</c>;
/// the host/renderer pulls the live, non-expired set each tick via <see cref="Drain"/>.
/// The "now" used by <see cref="Notify"/> comes from an injectable monotonic clock so the
/// queue/expiry logic is unit-testable without the game.
/// </summary>
internal sealed class NotificationService : INotifications
{
    /// <summary>Default toast lifetime in seconds when the caller passes <c>null</c>.</summary>
    public const float DefaultSeconds = 3f;

    // Hard cap so a misbehaving plugin can't grow the list unboundedly; oldest are dropped.
    private const int MaxToasts = 32;

    private readonly Func<double> _now;
    private readonly object _gate = new();
    private readonly List<ActiveToast> _toasts = new();

    /// <summary>Production ctor — uses a process-monotonic stopwatch clock.</summary>
    public NotificationService() : this(MakeStopwatchClock()) { }

    /// <summary>Test ctor — inject a controllable monotonic clock (seconds).</summary>
    public NotificationService(Func<double> now) => _now = now;

    /// <summary>The service's current monotonic-clock reading (seconds). The renderer passes
    /// this to <see cref="Drain"/> so enqueue + expiry share one clock.</summary>
    public double Now => _now();

    public void Notify(string message, NotificationKind kind = NotificationKind.Info, float? seconds = null)
    {
        if (string.IsNullOrEmpty(message)) return;
        var life = seconds ?? DefaultSeconds;
        if (life <= 0f) return;

        var toast = new ActiveToast(message, kind, _now() + life);
        lock (_gate)
        {
            _toasts.Add(toast);
            if (_toasts.Count > MaxToasts) _toasts.RemoveAt(0);
        }
    }

    /// <summary>
    /// Returns a snapshot of the currently-active toasts in insertion order (oldest first),
    /// dropping any that expired at or before <paramref name="now"/>. Called by the renderer
    /// each tick on the main thread.
    /// </summary>
    public IReadOnlyList<ActiveToast> Drain(double now)
    {
        lock (_gate)
        {
            _toasts.RemoveAll(t => t.ExpiresAt <= now);
            return _toasts.Count == 0 ? Array.Empty<ActiveToast>() : _toasts.ToArray();
        }
    }

    private static Func<double> MakeStopwatchClock()
    {
        var sw = Stopwatch.StartNew();
        return () => sw.Elapsed.TotalSeconds;
    }
}
