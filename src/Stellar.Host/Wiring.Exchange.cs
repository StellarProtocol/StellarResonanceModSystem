using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private PandaExchangeProbe? _exchangeProbe;
    private ExchangeService? _exchangeService;

    /// <summary>Constructs the trade/exchange probe + service. The probe drives the game's <c>trade</c>
    /// Lua VM via the tolua# bridge; the bridge resolves lazily after HybridCLR loads, so construction
    /// is safe pre-login. Drained from the Host service tick.</summary>
    private void BuildExchangeServices(BepInExPluginLog log, ReflectionGameTypeRegistry typeRegistry)
    {
        _exchangeProbe = new PandaExchangeProbe(log, typeRegistry);
        _exchangeService = new ExchangeService(_exchangeProbe);
    }
}
