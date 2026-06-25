using Stellar.Abstractions.Diagnostics;

namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaExchangeProbe
{
    private void OnResolutionSucceeded()
        => _log.Info("[Stellar][Exchange] resolved tolua# LuaState bridge; WorldProxy exchange RPCs ready (Approach A)");

    private void OnResolutionFailure(string reason)
    {
        if (_resolutionFailureLogged) return;
        _resolutionFailureLogged = true;
        _log.Warning($"[Stellar][Exchange] bridge unresolved: {reason}");
    }

    private void Diag(string msg)
    {
        if (!StellarDiagnostics.IsEnabled) return;
        _log.Info($"[Stellar][Exchange] {msg}");
    }
}
