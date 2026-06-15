using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private WindowService? _windowService;
    private WindowRenderer? _windowRenderer;

    private void BuildWindowServices(BepInExPluginLog log)
    {
        // uGUI interactive window toolkit (SP1 window shell). Mirrors the HUD wiring: the
        // renderer bakes the frosted GlassMenu chrome from the active menu palette + owns the
        // window canvas (+ GraphicRaycaster, riding the game EventSystem). Injected into the
        // PluginServices aggregator below; the theme-switch hook rebakes + re-mounts next tick.
        _windowRenderer = new WindowRenderer(log, _themeRenderer!.Colors, _namedTheme!);
        _windowService = new WindowService(_windowRenderer, log, _menuState!, _clientState!);
        _namedTheme!.ActiveChanged += _windowRenderer.InvalidateTheme;
    }
}
