namespace Stellar.Infrastructure.Game;

internal sealed partial class PandaExchangeReconProbe
{
    private void OnResolutionSucceeded()
        => _log.Info("[Stellar][ExchangeRecon] resolved tolua# LuaState bridge; enumerating exchange VMs");

    private void OnResolutionFailure(string reason)
    {
        if (_resolutionFailureLogged) return;   // log the first failure only — avoids per-tick spam pre-login
        _resolutionFailureLogged = true;
        _log.Warning($"[Stellar][ExchangeRecon] bridge unresolved: {reason}");
    }
}
