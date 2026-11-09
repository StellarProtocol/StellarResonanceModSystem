using System;
using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.UI;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private LauncherRegistry? _launcher;
    private LauncherView? _launcherView;
    private IWindowControl? _launcherControl;
    private INativeUiElementHandle? _railButtonHandle;
    private INativeUiElementHandle? _loginSidebarButtonHandle;

    private void BuildLauncherServices(BepInExPluginLog log)
    {
        // Launcher registry (Phase B) — feeds the rail button + launcher menu.
        // Pinned ids + mode persist in the framework's "launcher" config section.
        _launcher = new LauncherRegistry(new LauncherPrefs(_pluginConfigService!.GetSection("launcher")), log);
    }

    // Registers the native uGUI launcher (the house-style redesign — Full menu + compact bar via LauncherView) on the
    // window service, and the native "Stellar" rail button that toggles it. The launcher's Settings entries open the
    // multi-tab uGUI Settings hub; its "N failed to load" header link asks for the Plugins tab.
    private void RegisterLauncher(BepInExPluginLog log)
    {
        var actions = new LauncherActions(
            OpenSettings: OpenSettingsFromLauncher,   // tab key → that tab; null → current tab (shown + fronted, never toggled)
            Close: () => _launcherControl?.SetVisible(false),
            ToggleLayoutEdit: () => _layoutEditor?.ToggleEditMode());   // = the Shift+` hotkey
        var style = new StellarStyle(_themeRenderer!.Colors, _namedTheme);
        _launcherView = new LauncherView(_launcher!, actions, _frameworkLocalization!, style, FailedPluginCount());
        _launcherControl = _windowService!.Register(new WindowRegistration(LauncherSpec(), _launcherView.Root));

        // Perf overlay (Shift+End, dev-only) is a uGUI window now (Phase E — no IMGUI). Registered here where
        // _windowService exists; _perfOverlay was constructed in Phase 8 alongside its hotkey.
        if (_perfOverlay != null) _perfOverlayControl = _windowService.Register(_perfOverlay.BuildRegistration());

        // BRANDED (internal path): accent star + pulsing halo, and always the first injected rail slot (directly
        // under the native top group) — plugin rail buttons registered via the public Register stack below it
        // and draw their icon untinted.
        _railButtonHandle = _uguiInjection!.RegisterBranded(new MenuButtonSpec(
            NativeUiAnchor.MainMenuRail, "Stellar", IconKey: null,
            Tooltip: "Open Stellar", OnClick: () => ToggleLauncher(),
            IconPng: LauncherIcons.Get("stellar")));

        // Same Stellar icon injected into the TITLE-SCREEN login sidebar (dark-circle styling applied by the
        // adapter for this anchor). login_main only exists at the login screen, so the injection service's
        // ~5 Hz availability probe injects it there only and self-heals if the game destroys the view.
        _loginSidebarButtonHandle = _uguiInjection!.Register(new MenuButtonSpec(
            NativeUiAnchor.LoginSidebar, "Stellar", IconKey: null,
            Tooltip: "Open Stellar", OnClick: () => ToggleLauncher(),
            IconPng: LauncherIcons.Get("stellar")));
        log.Info("[Launcher] uGUI launcher + Stellar rail button + login-sidebar button registered");
    }

    // Rail button / login-sidebar button / Shift+Home. Opening FRONTS the launcher: the Settings hub is fronted on every
    // open and ZFront is sticky across hide/show, so a plain re-open would land UNDER an open hub (CLAUDE.md Rule 5
    // exception; WindowBuilder-Patterns.md "ZFront is STICKY").
    private void ToggleLauncher()
    {
        var c = _launcherControl;
        if (c == null) return;
        // A pre-warm mount (Wiring.Prewarm.cs) is Visible to the window service but never on screen — that is "closed"
        // for the toggle, so opening right away (e.g. the login-sidebar button) never hides it or flashes it.
        if (c.IsShown && !_launcherPrewarm.Warming) { c.SetVisible(false); return; }
        _launcherPrewarm.EndForRealOpen(c);   // a show mid-warm-up takes over (flag cleared BEFORE SetVisible)
        c.SetVisible(true);
        c.BringToFront();
    }

    private WindowSpec LauncherSpec()
        // GlassMenu frosted frame, NO chrome title bar — the launcher self-draws its header in the body (top in
        // Full/vertical, a LEFT strip in horizontal). AutoSizeWidth tracks the active mode. Keeps the
        // "settings.hub" id so the saved drag POSITION carries over. Draggable (whole frame); the body draws ✕.
        // Framework chrome — shown in every phase (title/menus) EXCEPT hidden over the loading screen. The pre-warm AND-s
        // its own term onto that gate (never replaces it): suppressed while warming (Wiring.Prewarm.cs).
        => new WindowSpec("settings.hub", "", new WindowRect(2071, 1225, 420, 0f), WindowCategory.Tools, WindowPanelStyle.GlassMenu)
            { ShouldRender = () => !_launcherPrewarm.Warming && (_clientState!.UiState & GameUIState.Loading) == 0, StartVisible = false, Draggable = true, Closable = false, AutoSizeWidth = true, ShowTitleBar = false };

    // Live "N failed to load" count for the launcher header: recounted only when a plugin's enabled/errored state
    // changes (IPluginInventory.StatusChanged), so the per-refresh read is a field load, not a List() snapshot.
    private Func<int> FailedPluginCount()
    {
        var inventory = _pluginRegistry;
        if (inventory == null) return () => 0;
        var failed = 0;
        void Recount()
        {
            var n = 0;
            foreach (var p in inventory.List()) if (p.IsErrored) n++;
            failed = n;
        }
        Recount();
        inventory.StatusChanged += _ => Recount();
        return () => failed;
    }
}
