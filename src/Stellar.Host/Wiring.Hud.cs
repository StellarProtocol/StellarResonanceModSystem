using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private HudService? _hudService;
    private HudRenderer? _hudRenderer;

    private void BuildHudServices(BepInExPluginLog log)
    {
        // uGUI HUD toolkit (Task 6). The renderer bakes its pill/bar 9-slice sprites
        // from the active theme's HUD palette (Phase 8 built _themeRenderer first), so
        // the native HUDs reproduce the IMGUI HudOverlay chrome. HudService is injected
        // into the PluginServices aggregator below; AttachLayout + SetHud happen in
        // WirePhase9Ui once the layout deps are confirmed live. The theme-switch hook
        // rebakes the sprites + re-mounts every HUD on the next tick.
        _hudRenderer = new HudRenderer(log, _themeRenderer!.Colors);
        _hudService = new HudService(_hudRenderer, log, _menuState!, _clientState!);
        _namedTheme!.ActiveChanged += _hudRenderer.InvalidateTheme;
    }
}
