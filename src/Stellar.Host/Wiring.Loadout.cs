using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    // ── Loadout services (Wiring.Loadout.cs) ────────────────────────────────
    private PandaLoadoutProbe? _loadoutProbe;
    private LoadoutService? _loadoutService;

    /// <summary>
    /// Constructs the loadout (profession-project) switch probe + service. The
    /// probe dispatches the switch through the game's own Lua VM
    /// (<c>Z.VMMgr.GetVM(...).&lt;ApplyFn&gt;(id, token)</c> via the tolua# LuaState
    /// bridge) and polls the <c>CurrentProfessionProjectId</c> container for
    /// completion. The Lua bridge + current-id container resolve lazily after
    /// HybridCLR loads the game assemblies, so construction is safe pre-login.
    /// <see cref="LoadoutService.Tick"/> is driven from the Host service tick.
    /// </summary>
    private void BuildLoadoutServices(BepInExPluginLog log, ReflectionGameTypeRegistry typeRegistry)
    {
        _loadoutProbe = new PandaLoadoutProbe(log, typeRegistry);
        _loadoutService = new LoadoutService(_loadoutProbe);
    }
}
