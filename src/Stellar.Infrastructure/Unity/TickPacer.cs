namespace Stellar.Infrastructure.Unity;

/// <summary>When the framework tick fires, from either driver (scene freeze time pause, devkit recon
/// <c>free-camera-recon.md</c> § Run 9 R9-3: at <c>Time.timeScale = 0</c> the <c>InvokeRepeating</c> schedule never fires, so
/// every plugin, hotkey and toast stopped):
/// <list type="bullet">
/// <item><b>Scheduled</b> — <see cref="StellarTicker"/>'s <c>InvokeRepeating</c>, the normal driver. Fires as before; the one
/// added rule (at most one tick per rendered frame) never changes it, since Unity runs a repeating invoke at most once per
/// frame.</item>
/// <item><b>Unscaled</b> — a per-frame <c>Update</c> enabled only while the clock is paused (<see cref="Unscaled"/>): fires
/// when a whole interval of REAL time has passed since the last tick, so the rate stays the configured one.</item>
/// </list>
/// Both share one last-tick time (the tick's dt is real seconds since the previous tick, whichever driver fired it) and one
/// last-tick frame, so a frame never ticks twice across the switch. Pure (unit-tested); main thread.</summary>
internal sealed class TickPacer
{
    // A tick arriving this much before a full interval still counts (frame timing jitter).
    private const float Slack = 0.0005f;

    private float _last;
    private int _lastFrame = int.MinValue;

    /// <summary>True while the clock is paused: the unscaled driver may fire.</summary>
    public bool Unscaled { get; set; }

    /// <summary>Starts the real-time origin (the ticker's <c>Start</c>).</summary>
    public void Start(float now) => _last = now;

    /// <summary>The scheduled driver fired at real time <paramref name="now"/> on frame <paramref name="frame"/>: true (with
    /// <paramref name="dt"/>) unless this frame already ticked.</summary>
    public bool TryScheduled(float now, int frame, out float dt) => TryFire(now, frame, out dt);

    /// <summary>The unscaled driver's per-frame question at real time <paramref name="now"/>: true only while
    /// <see cref="Unscaled"/>, a full interval at <paramref name="hz"/> has passed, and this frame has not ticked.</summary>
    public bool TryUnscaled(float now, int frame, int hz, out float dt)
    {
        dt = 0f;
        if (!Unscaled || hz <= 0 || now - _last < 1f / hz - Slack) return false;
        return TryFire(now, frame, out dt);
    }

    private bool TryFire(float now, int frame, out float dt)
    {
        dt = 0f;
        if (frame == _lastFrame) return false;
        dt = now - _last;
        _last = now;
        _lastFrame = frame;
        return true;
    }
}
