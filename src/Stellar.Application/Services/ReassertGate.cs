namespace Stellar.Application.Services;

/// <summary>
/// One "re-assert requested" latch: armed by a game event, taken on the first framework tick where the world is in a
/// stable scene. Stays armed through a zone load, so a request made mid-transition is never lost — and nothing runs
/// while nothing is armed (no polling of game state).
/// </summary>
internal sealed class ReassertGate
{
    private bool _pending;

    public bool IsPending => _pending;

    public void Request() => _pending = true;

    /// <summary>True (and disarms) when armed and <paramref name="ready"/>; otherwise false and stays as it was.</summary>
    public bool TryTake(bool ready)
    {
        if (!_pending || !ready) return false;
        _pending = false;
        return true;
    }
}
