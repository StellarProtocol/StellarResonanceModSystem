using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private void WireHideProbe(BepInExPluginLog log)
    {
        if (!HideSelfEffectsProbe.Armed) return;
        var probe = new HideSelfEffectsProbe(_gameTypeRegistry!, log, () => _combatService?.LocalEntityId.Value ?? 0L);
        _framework!.Update += dt => { if (_clientState!.IsWorldActive) probe.Tick(dt); };
        log.Info("[HideProbe] armed");
    }
}
