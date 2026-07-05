namespace Stellar.Infrastructure.Game;

// Diagnostics for PandaWorldAttrProbe — gated on StellarDiagnostics.IsEnabled per the standards.
// These confirm the ZWorld AttrDeathCount(348) read path in-game (the accessor is a lua-bridge
// method not visible in static tooling, so the first live run validates it).
internal sealed partial class PandaWorldAttrProbe
{
    private bool _resolveMissingLogged;

    private void DiagDefeated(int value)
    {
        if (!Stellar.Abstractions.Diagnostics.StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Defeated] ZWorld AttrDeathCount(348) = {value} — latched (runId={_state.CurrentRunId})");
    }

    private void DiagResolveMissing(string what)
    {
        if (!Stellar.Abstractions.Diagnostics.StellarDiagnostics.IsEnabled) return;
        if (_resolveMissingLogged) return;
        _resolveMissingLogged = true;
        _log.Warning($"[Defeated] ZWorld read disabled — could not resolve {what}");
    }

    private void DiagFaulted(System.Exception ex)
    {
        if (!Stellar.Abstractions.Diagnostics.StellarDiagnostics.IsEnabled) return;
        _log.Warning($"[Defeated] ZWorld read disabled after fault: {ex.GetType().Name}: {ex.Message}");
    }
}
