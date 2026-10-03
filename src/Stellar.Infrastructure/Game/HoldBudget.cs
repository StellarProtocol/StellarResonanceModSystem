namespace Stellar.Infrastructure.Game;

/// <summary>Rolling 60-frame average of the position-hold cost; trips (and stays tripped until <see cref="Reset"/>) when
/// a window averages more than the spec's 0.30 ms per frame.</summary>
internal sealed class HoldBudget
{
    internal const double LimitMs = 0.30;
    internal const int Window = 60;

    private double _sum;
    private int _count;

    public bool Exceeded { get; private set; }
    public double LastAverageMs { get; private set; }

    public void Reset()
    {
        _sum = 0;
        _count = 0;
        Exceeded = false;
    }

    /// <summary>Adds one frame's cost; true once the budget is exceeded.</summary>
    public bool Record(double ms)
    {
        if (Exceeded) return true;
        _sum += ms;
        if (++_count < Window) return false;
        LastAverageMs = _sum / _count;
        _sum = 0;
        _count = 0;
        Exceeded = LastAverageMs > LimitMs;
        return Exceeded;
    }
}
