using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.UI;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private LauncherRegistry? _launcher;
    private LauncherView? _launcherView;
    private IWindowControl? _launcherControl;
    private INativeUiElementHandle? _railButtonHandle;

    private void BuildLauncherServices()
    {
        // Launcher registry (Phase B) — feeds the rail button + launcher menu.
        // Pinned ids + mode persist in the framework's "launcher" config section.
        _launcher = new LauncherRegistry(new LauncherPrefs(_pluginConfigService!.GetSection("launcher")));
    }

    // Registers the native uGUI launcher (the redesigned hub — both modes via LauncherView) on the window
    // service, and the native "Stellar" rail button that toggles it. The launcher's ⚙ Settings entry opens the
    // multi-tab uGUI Settings hub (no more per-panel SETTINGS tiles).
    private void RegisterLauncher(BepInExPluginLog log)
    {
        _launcherView = new LauncherView(_launcher!,
            openSettings: () => Toggle(_settingsHubControl),
            close: () => _launcherControl?.SetVisible(false));
        _launcherControl = _windowService!.Register(new WindowRegistration(LauncherSpec(), _launcherView.Root));

        // Perf overlay (Shift+End, dev-only) is a uGUI window now (Phase E — no IMGUI). Registered here where
        // _windowService exists; _perfOverlay was constructed in Phase 8 alongside its hotkey.
        if (_perfOverlay != null) _perfOverlayControl = _windowService.Register(_perfOverlay.BuildRegistration());

        _railButtonHandle = _uguiInjection!.Register(new MenuButtonSpec(
            NativeUiAnchor.MainMenuRail, "Stellar", IconKey: null,
            Tooltip: "Open Stellar", OnClick: () => Toggle(_launcherControl),
            IconPng: LauncherIcons.Get("stellar")));
        log.Info("[Launcher] uGUI launcher + Stellar rail button registered");
    }

    private static void Toggle(IWindowControl? c) { if (c != null) c.SetVisible(!c.IsShown); }

    private static WindowSpec LauncherSpec()
        // GlassMenu frosted frame, NO chrome title bar — the launcher self-draws its header in the body (top in
        // Full/vertical, a LEFT strip in horizontal). AutoSizeWidth tracks the active mode. Keeps the
        // "settings.hub" id so the saved drag POSITION carries over. Draggable (whole frame); the body draws ✕.
        => new WindowSpec("settings.hub", "", new WindowRect(2071, 1225, 420, 0f), WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            { StartVisible = false, Draggable = true, Closable = false, AutoSizeWidth = true, ShowTitleBar = false };
}
