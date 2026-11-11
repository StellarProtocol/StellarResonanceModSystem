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
        // ONE subscription on the shared flag per plugin, fanned out to this plugin's own handlers; Unregister drops
        // it, so a disabled/unloaded plugin's handlers are never called again and never kept alive.
        _shared.DeveloperModeChanged += OnDeveloperModeChanged;
    }

    public event Action<float>? Update;

    public bool DeveloperMode => _shared.DeveloperMode;
    public event Action<bool>? DeveloperModeChanged;

    // Every handler runs even if one throws; the first failure is rethrown tagged with the plugin guid, so the shared
    // service's per-subscriber guard logs WHICH plugin threw (its own delegate name would only say PerPluginFramework).
    private void OnDeveloperModeChanged(bool on)
    {
        if (DeveloperModeChanged is not { } multicast) return;
        Exception? first = null;
        foreach (var d in multicast.GetInvocationList())
        {
            try { ((Action<bool>)d)(on); }
            catch (Exception ex) { first ??= ex; }
        }
        if (first != null) throw new InvalidOperationException($"plugin {_guid}: {first.Message}", first);
    }

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

    public void Unregister()
    {
        _scheduler.UnregisterPlugin(_guid);
        _shared.DeveloperModeChanged -= OnDeveloperModeChanged;
        DeveloperModeChanged = null;
    }
}
