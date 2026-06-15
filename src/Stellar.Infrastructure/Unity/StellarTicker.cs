using System;
using UnityEngine;

namespace Stellar.Infrastructure.Unity;

/// <summary>
/// Injected MonoBehaviour that drives Stellar's entire per-frame work from an
/// <c>InvokeRepeating</c> schedule at the configured rate (<see cref="Stellar.Abstractions.Diagnostics.PerfControls.UpdateRateHz"/>)
/// instead of a per-frame Unity <c>Update</c>. Unity runs the "is it time yet?" check for the
/// invoke list on its NATIVE side, so managed is entered only at the tick rate — most rendered
/// frames have ZERO managed-runtime entry, which is the ~12-18 fps "managed crossing" tax this
/// avoids (measured: a frame with no managed entry costs nothing; cost is linear in entries/sec).
///
/// <para>This type deliberately has NO <c>Update</c>/<c>FixedUpdate</c>/<c>LateUpdate</c>/<c>OnGUI</c>
/// method — any of those would make Unity cross into managed every frame and reinstate the tax.</para>
/// </summary>
public sealed class StellarTicker : MonoBehaviour
{
    // Required by Il2CppInterop for managed MonoBehaviour subclasses.
    public StellarTicker(IntPtr ptr) : base(ptr) { }

    /// <summary>Tick callback; argument is real seconds elapsed since the previous tick.</summary>
    internal static Action<float>? OnTick;
    internal static Action<string>? OnError;

    private int _scheduledRateHz;
    private float _lastTickTime;

    private void Start()
    {
        _lastTickTime = Time.realtimeSinceStartup;
        Schedule(Stellar.Abstractions.Diagnostics.PerfControls.UpdateRateHz);
    }

    // (Re)schedule the repeating tick at the given Hz. At MaxUpdateRateHz the interval (~4 ms) is
    // shorter than a frame, so the invoke fires every frame ("every frame" mode). Unity invokes at
    // most once per frame, so it never over-fires.
    private void Schedule(int hz)
    {
        CancelInvoke(nameof(Tick));
        _scheduledRateHz = hz;
        InvokeRepeating(nameof(Tick), 0f, 1f / hz);
    }

    /// <summary>Called by the host when the rate setting changes (Settings → Performance slider).</summary>
    internal void Reschedule()
    {
        var hz = Stellar.Abstractions.Diagnostics.PerfControls.UpdateRateHz;
        if (hz != _scheduledRateHz) Schedule(hz);
    }

    // Public so Unity's InvokeRepeating can find it by name on the injected type.
    public void Tick()
    {
        var now = Time.realtimeSinceStartup;
        var dt = now - _lastTickTime;
        _lastTickTime = now;
        try { OnTick?.Invoke(dt); }
        catch (Exception ex) { OnError?.Invoke(ex.Message); }
    }
}
