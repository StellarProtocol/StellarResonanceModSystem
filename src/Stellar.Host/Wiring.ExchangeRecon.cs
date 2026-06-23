using Stellar.Abstractions.Diagnostics;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // RECON-ONLY (Phase 0). Null unless STELLAR_EXCHANGE_RECON is set. Remove with the
    // probe when the real PandaExchangeProbe lands in Phase 1.
    private PandaExchangeReconProbe? _exchangeReconProbe;

    private void BuildExchangeReconServices(BepInExPluginLog log, ReflectionGameTypeRegistry typeRegistry)
    {
        if (!PerfControls.Flag("EXCHANGE_RECON")) return;   // zero overhead in normal play
        log.Info("[boot] STELLAR_EXCHANGE_RECON set — wiring throwaway exchange-VM recon probe");
        _exchangeReconProbe = new PandaExchangeReconProbe(log, typeRegistry);
    }
}
