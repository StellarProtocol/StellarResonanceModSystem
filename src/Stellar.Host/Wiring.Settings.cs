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
    /// auto-shows settings.layout on Shift+`, and ticks NativeUiService each
    /// frame. Called from <c>OnHotUpdateReady</c> after SetupPerfOverlay; all
    /// Stellar surfaces are uGUI now (no OnGUI sink).
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

        var panels = new SettingsPanelSet
        {
            Plugins = new PluginsPanel(_pluginRegistry, _themeRenderer, _pluginRegistry.SetEnabled),
            Hotkeys = new HotkeysPanel((IHotkeyDirectory)_hotkeyService, (IHotkeyBlockDirectory)_hotkeyService, _themeRenderer),
            Themes  = new ThemesPanel(_namedTheme, _namedTheme, _themeRenderer, _colorRegistry!, _customThemes!),
            Layout  = new LayoutPanel(_layoutStorage, _layoutEditor, _themeRenderer),
            GameUi  = new GameUiPanel(_nativeUi, _themeRenderer, log),
            Perf    = new PerformancePanel(_perfPrefs!, _themeRenderer, _pluginRegistry, _scheduler!.EffectiveRateFor),
            About   = new AboutPanel(_themeRenderer),
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

        // uGUI HUD + window toolkits: bind layout storage + resolution provider (Tick from
        // RefreshPerTickServices; dispose from DisposePhase9), and hand the HUD to the layout editor.
        AttachOverlayLayout();
        _layoutOverlay.SetHud(_hudService!);
        _layoutOverlay.SetWindows(_windowService!);   // edit-mode toolbar registers as a uGUI window

        // Per-frame Tick — NativeUiService re-asserts at 1 Hz, polls for
        // resolution at 5 Hz. Cheap on idle frames.
        _framework.Update += dt => _nativeUi.Tick(dt, _inputGateway.CurrentResolution);

        log.Info("[Launcher] uGUI launcher + rail button + uGUI Settings hub (7 tabs) registered");
    }

    private void AttachOverlayLayout()
    {
        System.Func<Resolution> res = () => _inputGateway?.CurrentResolution ?? new Resolution(1920, 1080);
        _hudService?.AttachLayout(_layoutStorage!, res);
        _windowService?.AttachLayout(_layoutStorage!, res);
    }

    // The native-uGUI multi-tab Settings hub: a GlassMenu window with an icon+label tab strip + a Conditional
    // body showing each panel's Describe() tree, wired to the SAME services as the (now-retired) IMGUI panels.
    // Opened by the launcher's ⚙ Settings entry (the control is held in _settingsHubControl). Hidden at boot.
    private void RegisterSettingsHub(SettingsPanelSet panels)
    {
        if (_windowService == null) return;
        var tab = 0;
        var spec = new WindowSpec("stellar.settings.ugui", "Stellar Settings",
            new WindowRect(1591f, 722f, 600f, 0f), WindowCategory.Tools, WindowPanelStyle.GlassMenu)   // wide enough for Hotkeys rows
        { Closable = true, Draggable = true, StartVisible = false };
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

    // Icon + label tabs (each: the panel's launcher icon + a label Button highlighted when active).
    private static HudElement[] BuildHubTabs(System.Func<int> getTab, System.Action<int> setTab)
    {
        var tabs = new (string Label, string Icon)[]
        {
            ("Plugins", "plugins"), ("Layout", "layout"), ("Themes", "theme"),
            ("Hotkeys", "hotkeys"), ("Game UI", "gameui"), ("Performance", "settings"), ("About", "about"),
        };
        var els = new HudElement[tabs.Length];
        for (var i = 0; i < tabs.Length; i++)
        {
            var idx = i; var (lbl, icon) = tabs[i];
            // Icon INSIDE the button → co-centred with the label by the button layout (font-robust alignment).
            els[i] = new ButtonElement(() => lbl, () => setTab(idx), Active: () => getTab() == idx,
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
