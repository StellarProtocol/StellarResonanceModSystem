using Stellar.Abstractions.Diagnostics;

namespace Stellar.Application.Services;

/// <summary>
/// A per-consumer rate accumulator. <see cref="Crossed"/> returns true at most once per
/// <c>1/rateHz</c> seconds of accumulated time; <see cref="LastDt"/> is the real seconds elapsed
/// since the previous crossing (pass it as the consumer's deltaTime). After a long stall the
/// residue is clamped so the consumer does not fire a burst of catch-up ticks.
/// </summary>
internal sealed class RateGate
{
    private double _acc;
    private float _elapsed;

    /// <summary>Real seconds elapsed since the previous crossing (valid after <see cref="Crossed"/> returns true).</summary>
    public float LastDt { get; private set; }

    public bool Crossed(float dt, int rateHz)
    {
        var interval = 1.0 / PerfControls.ClampRate(rateHz);
        _acc += dt;
        _elapsed += dt;
        if (_acc < interval) return false;
        _acc -= interval;
        if (_acc > interval) _acc = 0.0;   // catch-up clamp: never fire more than once per Crossed call
        LastDt = _elapsed;
        _elapsed = 0f;
        return true;
    }
}
