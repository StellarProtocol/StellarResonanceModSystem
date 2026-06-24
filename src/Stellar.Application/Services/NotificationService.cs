using System;
using System.Collections.Generic;
using System.Diagnostics;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;

namespace Stellar.Application.Services;

/// <summary>One active toast: a stable identity plus its text, kind, and lifetime window
/// (monotonic-clock seconds). The renderer diffs the live set by <paramref name="Id"/> so it
/// can animate spawn/exit/reflow, and uses <paramref name="CreatedAt"/>/<paramref name="Duration"/>
/// for the linear countdown bar.</summary>
/// <param name="Id">Monotonic, process-unique identity assigned at enqueue. The renderer keys
/// its per-card state off this so re-draining the same toast doesn't respawn it.</param>
/// <param name="Message">The text to display.</param>
/// <param name="Kind">Severity / intent, used by the renderer to pick a colour.</param>
/// <param name="CreatedAt">Monotonic-clock time (seconds) at which the toast was enqueued.</param>
/// <param name="ExpiresAt">Monotonic-clock time (seconds) past which the toast should disappear.</param>
/// <param name="Duration">Configured lifetime in seconds (<c>ExpiresAt - CreatedAt</c>), kept
/// explicitly so the countdown bar doesn't have to recompute it from the two clocks.</param>
/// <param name="IconTexture">Optional boxed <c>UnityEngine.Texture</c> handle for a custom icon;
/// <c>null</c> keeps the baked kind glyph. Kept renderer-neutral (<c>object?</c>) so this
/// Application-layer record carries no Unity dependency.</param>
/// <param name="IconUv">Atlas UV sub-rect for <paramref name="IconTexture"/> (ignored when null).</param>
public readonly record struct ActiveToast(
    long Id, string Message, NotificationKind Kind, double CreatedAt, double ExpiresAt, float Duration,
    object? IconTexture = null, UvRect IconUv = default);

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
    private long _nextId;   // monotonic toast identity; assigned under _gate in Notify

    /// <summary>Production ctor — uses a process-monotonic stopwatch clock.</summary>
    public NotificationService() : this(MakeStopwatchClock()) { }

    /// <summary>Test ctor — inject a controllable monotonic clock (seconds).</summary>
    public NotificationService(Func<double> now) => _now = now;

    /// <summary>The service's current monotonic-clock reading (seconds). The renderer passes
    /// this to <see cref="Drain"/> so enqueue + expiry share one clock.</summary>
    public double Now => _now();

    public void Notify(string message, NotificationKind kind = NotificationKind.Info, float? seconds = null)
        => new Builder(this).WithMessage(message).WithKind(kind).WithDuration(seconds ?? DefaultSeconds).Show();

    public INotificationBuilder Create() => new Builder(this);

    // Enqueue a fully-built toast. Called by the fluent Builder on Show(); validates message/lifetime
    // and assigns the monotonic identity + expiry under the gate.
    private void Enqueue(string message, NotificationKind kind, float life, object? iconTexture, UvRect iconUv)
    {
        if (string.IsNullOrEmpty(message)) return;
        if (life <= 0f) return;

        var createdAt = _now();
        lock (_gate)
        {
            var toast = new ActiveToast(
                ++_nextId, message, kind, createdAt, createdAt + life, life, iconTexture, iconUv);
            _toasts.Add(toast);
            if (_toasts.Count > MaxToasts) _toasts.RemoveAt(0);
        }
    }

    /// <summary>Fluent <see cref="INotificationBuilder"/> — accumulates fields, enqueues on <see cref="Show"/>.</summary>
    private sealed class Builder : INotificationBuilder
    {
        private readonly NotificationService _owner;
        private string _message = "";
        private NotificationKind _kind = NotificationKind.Info;
        private float _life = DefaultSeconds;
        private object? _iconTexture;
        private UvRect _iconUv;

        public Builder(NotificationService owner) => _owner = owner;

        public INotificationBuilder WithMessage(string message) { _message = message; return this; }
        public INotificationBuilder WithKind(NotificationKind kind) { _kind = kind; return this; }
        public INotificationBuilder WithDuration(float seconds) { _life = seconds; return this; }

        public INotificationBuilder WithIcon(object? texture, UvRect uv)
        {
            _iconTexture = texture; _iconUv = uv; return this;
        }

        public void Show() => _owner.Enqueue(_message, _kind, _life, _iconTexture, _iconUv);
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
