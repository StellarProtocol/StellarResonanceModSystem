using System;
using System.Collections.Generic;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
namespace Stellar.Application.Services;

/// <summary>
/// Pin stack for <see cref="ITimeOfDay"/> (spec 2026-10-01 photo-studio-render-quality § 3): the newest held pin
/// wins, disposing it falls back to the previous held pin, and disposing the last one hands time back to the
/// server (once — only if the framework actually pinned it). <see cref="Reassert"/> is driven by game events
/// (scene change, the game's own time-of-day calls, a cutscene ending) and writes only when the live clock
/// differs from the pin. A pin or hand-back the backend cannot do yet (not in a stable world) stays pending for
/// the next re-assert. Main thread only.
/// </summary>
internal sealed class TimeOfDayService : ITimeOfDay
{
    private const float HourEpsilon = 0.05f;   // 3 in-game minutes
    private const float Day = 24f;

    private readonly ITimeOfDayBackend _backend;
    private readonly List<Pin> _pins = new();
    private bool _pinnedByUs;   // we stopped server-driven time: the last release must hand it back

    public TimeOfDayService(ITimeOfDayBackend backend) => _backend = backend;

    public float CurrentHour => _backend.ReadHour() ?? 0f;
    public bool IsAvailable => _backend.IsAvailable;

    ITimePin ITimeOfDay.Pin(float hour) => Add(hour);

    internal ITimePin Add(float hour)
    {
        var pin = new Pin(this, Clamp(hour));
        _pins.Add(pin);
        Apply();
        return pin;
    }

    /// <summary>Re-pins the newest held pin if the game moved the clock or re-enabled server time, or retries a
    /// pending hand-back. No-op when nothing is held or pending.</summary>
    internal void Reassert() => Apply();

    internal static float Clamp(float hour) => float.IsNaN(hour) ? 0f : Math.Clamp(hour, 0f, Day);

    /// <summary>Distance on the 24-hour circle (24:00 == 00:00).</summary>
    internal static float CircularDistance(float a, float b)
    {
        var d = Math.Abs(a - b) % Day;
        return Math.Min(d, Day - d);
    }

    private void Apply()
    {
        if (_pins.Count == 0)
        {
            if (!_pinnedByUs || !_backend.IsReady) return;
            _backend.ReleaseToServer();
            _pinnedByUs = false;
            return;
        }
        if (!_backend.IsReady) return;
        var target = _pins[^1].Hour;
        if (NeedsPin(target)) _backend.Pin(target);
        _pinnedByUs = true;   // a pin is in force: the last release hands time back to the server
    }

    // Write only when the live clock differs. An unreadable value counts as different (we cannot verify it); this
    // runs only on events, never per tick, so that cannot thrash.
    private bool NeedsPin(float target)
    {
        if (_backend.ReadServerDriven() is not false) return true;
        return _backend.ReadHour() is not float hour || CircularDistance(hour, target) > HourEpsilon;
    }

    private void SetHour(Pin pin, float hour)
    {
        pin.Hour = Clamp(hour);
        if (ReferenceEquals(_pins[^1], pin)) Apply();
    }

    private void Release(Pin pin)
    {
        var wasNewest = ReferenceEquals(_pins[^1], pin);
        _pins.Remove(pin);
        if (wasNewest) Apply();
    }

    private sealed class Pin : ITimePin
    {
        private TimeOfDayService? _svc;
        public Pin(TimeOfDayService svc, float hour)
        {
            _svc = svc;
            Hour = hour;
        }
        public float Hour { get; set; }
        public bool IsActive => _svc is not null;
        public void SetHour(float hour) => _svc?.SetHour(this, hour);
        public void Dispose()
        {
            var svc = _svc;
            _svc = null;
            svc?.Release(this);
        }
    }
}
