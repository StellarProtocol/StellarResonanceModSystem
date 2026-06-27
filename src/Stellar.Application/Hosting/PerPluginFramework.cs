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

    public PerPluginFramework(string guid, TickScheduler scheduler, IFramework shared)
    {
        _guid = guid;
        _scheduler = scheduler;
        _shared = shared;
        _scheduler.RegisterPlugin(_guid, dt => Update?.Invoke(dt));
    }

    public event Action<float>? Update;

    public long FrameCount => _shared.FrameCount;
    public int ScreenWidth => _shared.ScreenWidth;
    public int ScreenHeight => _shared.ScreenHeight;

    public int EffectiveUpdateRateHz => _scheduler.EffectiveRateFor(_guid);
    public IUpdateRateScope RequestUpdateRate(int hz) => _scheduler.RequestDynamicRate(_guid, hz);

    public void Unregister() => _scheduler.UnregisterPlugin(_guid);
}
