using System;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;

namespace Stellar.Application.Hosting;

/// <summary>
/// Per-plugin <see cref="IFramework"/> view. <see cref="Update"/> is raised by the
/// <see cref="TickScheduler"/> at this plugin's effective rate; frame/screen properties delegate to
/// the shared <see cref="FrameworkService"/>; rate-control routes to the scheduler keyed by the
/// plugin's GUID. One instance per loaded plugin.
/// </summary>
internal sealed class PerPluginFramework : IFramework
{
    private readonly string _guid;
    private readonly TickScheduler _scheduler;
    private readonly IFramework _shared;
    // Per-plugin Post()/Every() store, drained on THIS plugin's scheduler beat (main thread, at the plugin's
    // own rate) — so a plugin's posted work and downsampled timers respect its tick rate, not the shared one.
    private readonly FrameDispatch _dispatch = new();

    public PerPluginFramework(string guid, TickScheduler scheduler, IFramework shared)
    {
        _guid = guid;
        _scheduler = scheduler;
        _shared = shared;
        _scheduler.RegisterPlugin(_guid, dt => { _dispatch.Drain(dt); Update?.Invoke(dt); });
    }

    public event Action<float>? Update;

    public long FrameCount => _shared.FrameCount;
    public int ScreenWidth => _shared.ScreenWidth;
    public int ScreenHeight => _shared.ScreenHeight;
    public int CanvasWidth => _shared.CanvasWidth;
    public int CanvasHeight => _shared.CanvasHeight;

    public void Post(Action action) => _dispatch.Post(action);
    public IDisposable Every(TimeSpan interval, Action action) => _dispatch.Every(interval, action);
    public float TimeNow => _shared.TimeNow;

    public int EffectiveUpdateRateHz => _scheduler.EffectiveRateFor(_guid);
    public IUpdateRateScope RequestUpdateRate(int hz) => _scheduler.RequestDynamicRate(_guid, hz);

    public void Unregister() => _scheduler.UnregisterPlugin(_guid);
}
