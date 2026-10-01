using System.Collections.Generic;
using Stellar.Abstractions.Services;
namespace Stellar.Application.Hosting;

/// <summary>
/// Per-plugin view of the shared <see cref="ITimeOfDay"/>. Tracks every pin it hands out; on plugin unload
/// <see cref="ReleaseAll"/> disposes them oldest first (a non-newest pin releases without touching the clock, so the
/// clock moves at most once), and the shared stack falls back to other plugins' pins or hands time back.
/// </summary>
internal sealed class PluginTimeOfDay : ITimeOfDay
{
    private readonly ITimeOfDay _inner;
    private readonly List<ITimePin> _pins = new();

    public PluginTimeOfDay(ITimeOfDay inner) => _inner = inner;

    public float CurrentHour => _inner.CurrentHour;
    public bool IsAvailable => _inner.IsAvailable;

    /// <summary>Pins still held (diagnostics / tests).</summary>
    internal int TrackedCount => _pins.Count;

    public ITimePin Pin(float hour)
    {
        _pins.RemoveAll(p => !p.IsActive);
        var pin = _inner.Pin(hour);
        _pins.Add(pin);
        return pin;
    }

    public void ReleaseAll()
    {
        foreach (var p in _pins) p.Dispose();
        _pins.Clear();
    }
}
