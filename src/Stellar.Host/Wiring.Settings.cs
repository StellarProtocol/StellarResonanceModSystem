using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.UI;
using Stellar.Infrastructure.UI.SettingsPanels;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private System.Action? _hotkeysCapturePoll;   // panels.Hotkeys.PollCaptureUgui (uGUI hub key capture)
    private System.Action? _themeEditorPoll;      // panels.Themes.PollEditorUgui (colour-edit drag-release flush)
    private IWindowControl? _settingsHubControl;   // the uGUI multi-tab Settings hub (launcher ⚙ toggles it)

    /// <summary>
    /// Wires the Phase 9a UI: instantiates the 7 settings windows, declares
    /// framework.settings-toggle (Shift+Home), runs the lockout safety net,
    /// and auto-shows settings.layout on Shift+`. Called from
    /// <c>OnHotUpdateReady</c> after SetupPerfOverlay; all Stellar surfaces are
    /// uGUI now (no OnGUI sink). NativeUiService.Tick is driven from the
    /// UN-gated global-rate beat (RunGlobalRateWork), NOT wired here — it must
    /// run during zone loads (IsWorldActive false) to catch the HUD rebuild.
    /// </summary>
    private void WirePhase9Ui(BepInExPluginLog log)
    {
        if (_themeRenderer is null || _hotkeyService is null
         || _nativeUi is null || _pluginRegistry is null || _layoutEditor is null
         || _layoutStorage is null || _namedTheme is null || _layoutOverlay is null
         || _inputGateway is null || _framework is null || _launcher is null
         || _uguiInjection is null)
        {
            log.Error("[Settings] Phase 9a UI wire-up missing dependency; aborting");
            return;
        }

        var loc = _frameworkLocalization!;
        var panels = new SettingsPanelSet
        {
            Plugins = new PluginsPanel(_pluginRegistry, _themeRenderer, _pluginRegistry.SetEnabled, loc),
            Hotkeys = new HotkeysPanel((IHotkeyDirectory)_hotkeyService, (IHotkeyBlockDirectory)_hotkeyService, _pluginRegistry, _themeRenderer, PluginName, loc),   // _pluginRegistry = IPluginInventory (group-header names); PluginName labels the framework's own group
            Themes  = new ThemesPanel(_namedTheme, _themeRenderer, _colorRegistry!, _customThemes!, _localizationEngine!, loc),
            Layout  = new LayoutPanel(_layoutStorage, _layoutEditor, _themeRenderer, loc),
            GameUi  = new GameUiPanel(_nativeUi, _themeRenderer, log, _layoutEditor, loc),
            Perf    = new PerformancePanel(_perfPrefs!, _themeRenderer, _pluginRegistry, _scheduler!.EffectiveRateFor, loc),
            About   = new AboutPanel(_themeRenderer, loc),
        };

        RegisterSettingsHub(panels);
        RegisterLauncher(log);
        DeclareSettingsHotkey(log);
        ((HotkeyService)_hotkeyService!).RestoreSettingsHotkeyIfLocked();

        // Phase 9a visual redo: settings windows are drag-by-title-bar in
        // normal mode (via GUI.DragWindow in the SettingsDialog chrome) so
        // they no longer need to be auto-shown on Shift+` entry. The Layout
        // panel still hosts the slot picker + inspector — the user opens it
        // explicitly via the hub icon when they want to use it.

        // Hand the native UI service to the overlay so Shift+` outlines + drags
        // game HUD elements alongside Stellar windows.
        _layoutOverlay.SetNativeUi(_nativeUi);

        // uGUI window toolkit: bind layout storage + resolution provider (Tick from
        // RefreshPerTickServices; dispose from DisposePhase9).
        AttachOverlayLayout();
        _layoutOverlay.SetWindows(_windowService!);   // edit-mode toolbar registers as a uGUI window

        // NativeUiService.Tick is deliberately NOT subscribed here: _framework.Update is IsWorldActive-gated (frozen
        // through zone loads). It's ticked UN-gated from RunGlobalRateWork instead — see Wiring.ServiceTick.
        log.Info("[Launcher] uGUI launcher + rail button + uGUI Settings hub (7 tabs) registered");
    }

    private void AttachOverlayLayout()
    {
        System.Func<Resolution> res = () => _inputGateway?.CurrentResolution ?? new Resolution(1920, 1080);
        _windowService?.AttachLayout(_layoutStorage!, res);
    }

    // The native-uGUI multi-tab Settings hub: a GlassMenu window with an icon+label tab strip + a Conditional
    // body showing each panel's Describe() tree, wired to the SAME services as the (now-retired) IMGUI panels.
    // Opened by the launcher's ⚙ Settings entry (the control is held in _settingsHubControl). Hidden at boot.
    private void RegisterSettingsHub(SettingsPanelSet panels)
    {
        if (_windowService == null) return;
        // Test hook (visual scenarios, mirrors STELLAR_AUTO_OPEN): STELLAR_SETTINGS_TAB=<0..6> preselects
        // the hub tab at registration so a scenario can capture a specific panel (e.g. 2 = Themes).
        var tab = 0;
        if (int.TryParse(System.Environment.GetEnvironmentVariable("STELLAR_SETTINGS_TAB"), out var tabEnv)
            && tabEnv is >= 0 and <= 6)
            tab = tabEnv;
        var spec = new WindowSpec("stellar.settings.ugui", "Stellar Settings",
            new WindowRect(1591f, 722f, 600f, 0f), WindowCategory.Tools, WindowPanelStyle.GlassMenu)   // wide enough for Hotkeys rows
        // Framework chrome — usable at title/menus in every phase, but hide over the loading screen.
        { ShouldRender = () => (_clientState!.UiState & GameUIState.Loading) == 0, Closable = true, Draggable = true, StartVisible = false };
        // Hotkeys capture has no Event.current outside OnGUI — poll it per frame from the game loop.
        _hotkeysCapturePoll = panels.Hotkeys.PollCaptureUgui;
        // Colour editor: coalesce ColorPicker-drag edits to one persist+rebake on mouse-release.
        _themeEditorPoll = panels.Themes.PollEditorUgui;
        var root = new ColumnElement(new HudElement[]
        {
            new RowElement(BuildHubTabs(() => tab, i => tab = i), Gap: 6f),
            new SeparatorElement(),
            new ConditionalElement(() => tab == 0, panels.Plugins.Describe()),
            new ConditionalElement(() => tab == 1, panels.Layout.Describe()),
            new ConditionalElement(() => tab == 2, panels.Themes.Describe()),
            new ConditionalElement(() => tab == 3, panels.Hotkeys.Describe()),
            new ConditionalElement(() => tab == 4, panels.GameUi.Describe()),
            new ConditionalElement(() => tab == 5, panels.Perf.Describe()),
            new ConditionalElement(() => tab == 6, panels.About.Describe()),
        });
        _settingsHubControl = _windowService.Register(new WindowRegistration(spec, root,
            OnClose: () => _settingsHubControl?.SetVisible(false)));
    }

    // Icon + label tabs (each: the panel's launcher icon + a localized label Button highlighted when active).
    private HudElement[] BuildHubTabs(System.Func<int> getTab, System.Action<int> setTab)
    {
        var loc = _frameworkLocalization!;
        var tabs = new (string Key, string Icon)[]
        {
            ("tab.plugins", "plugins"), ("tab.layout", "layout"), ("tab.themes", "theme"),
            ("tab.hotkeys", "hotkeys"), ("tab.gameui", "gameui"), ("tab.performance", "settings"), ("tab.about", "about"),
        };
        var els = new HudElement[tabs.Length];
        for (var i = 0; i < tabs.Length; i++)
        {
            var idx = i; var (key, icon) = tabs[i];
            // Icon INSIDE the button → co-centred with the label by the button layout (font-robust alignment).
            els[i] = new ButtonElement(() => loc.T(key), () => setTab(idx), Active: () => getTab() == idx,
                Icon: () => LauncherIcons.Get(icon));
        }
        return els;
    }

    private void DeclareSettingsHotkey(BepInExPluginLog log)
    {
        var action = new HotkeyAction(
            Id: "framework.settings-toggle",
            Description: "Toggle Stellar Settings",
            SuggestedDefault: new KeyBinding(StellarKeyCode.Home, ModifierKeys.Shift));
        _hotkeyService!.DeclareAction(action, () => Toggle(_launcherControl));
        log.Info("[Settings] hotkey framework.settings-toggle declared (Shift+Home)");
    }

    /// <summary>Bag of the 7 settings drawers, consumed by the uGUI hub via each panel's Describe().</summary>
    private sealed class SettingsPanelSet
    {
        public PluginsPanel      Plugins { get; init; } = null!;
        public HotkeysPanel      Hotkeys { get; init; } = null!;
        public ThemesPanel       Themes  { get; init; } = null!;
        public LayoutPanel       Layout  { get; init; } = null!;
        public GameUiPanel       GameUi  { get; init; } = null!;
        public PerformancePanel  Perf    { get; init; } = null!;
        public AboutPanel        About   { get; init; } = null!;
    }
}
