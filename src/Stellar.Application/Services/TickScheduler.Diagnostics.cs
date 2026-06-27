namespace Stellar.Application.Services;

/// <summary>
/// Diagnostic logging for <see cref="TickScheduler"/>, kept out of the scheduling logic per the
/// project's <c>.Diagnostics.cs</c> convention. Gated on the injected <c>_log</c> delegate — null in
/// tests / when no sink is attached, so the message is never built in those cases.
/// </summary>
internal sealed partial class TickScheduler
{
    // A held ramp outlived the max-hold safety cap (the plugin never released it). Force-released by the
    // leak-guard in ExpireStaleRamps; this records why the rate dropped. Rare (off the common Beat path).
    private void DiagRampAutoReleased(Entry e, RampScope r)
        => _log?.Invoke($"[TickScheduler] auto-released {r.Hz}Hz ramp on '{e.Guid}' after {_maxHoldSeconds:0}s (leaked scope?)");
}
