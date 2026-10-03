using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private void WireReShadeProbe(BepInExPluginLog log)
    {
        if (!ReShadeBridgeProbe.Armed) return;
        var probe = new ReShadeBridgeProbe(log, System.IO.Directory.GetCurrentDirectory());
        _framework!.Update += dt => { if (_clientState!.IsWorldActive) probe.Tick(dt); };
        log.Info("[RsProbe] armed");
    }
}
