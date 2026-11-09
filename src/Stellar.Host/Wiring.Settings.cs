using Stellar.Abstractions.Domain;
using Stellar.Abstractions.Services;
using Stellar.Application.Abstractions;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.UI;
using Stellar.Infrastructure.UI.SettingsPanels;
using Stellar.Infrastructure.UI.Style;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private System.Action? _hotkeysCapturePoll;   // panels.Hotkeys.PollCaptureUgui (uGUI hub key capture)
    private System.Action? _themeEditorPoll;      // Themes drag-release flush + the hub's own tick (help popover, child window)
    private IWindowControl? _settingsHubControl;   // the uGUI Settings hub (launcher ⚙ opens + fronts it)
    private SettingsHub? _settingsHub;

    /// <summary>
    /// Wires the Phase 9a UI: instantiates the settings drawers, declares
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

        var panels = BuildSettingsPanels(log);

        RegisterSettingsHub(panels);
        RegisterLauncher(log);
        DeclareSettingsHotkey(log);
        ((HotkeyService)_hotkeyService!).RestoreSettingsHotkeyIfLocked();

        // Hand the native UI service to the overlay so Shift+` outlines + drags
        // game HUD elements alongside Stellar windows.
        _layoutOverlay.SetNativeUi(_nativeUi);
        // Localized toolbar/outline text (heading, filter chips, reset/exit buttons, native-UI entry names) —
        // same façade every other Phase 9a panel uses.
        _layoutOverlay.SetLocalization(_frameworkLocalization!);

        // uGUI window toolkit: bind layout storage + resolution provider (Tick from
        // RefreshPerTickServices; dispose from DisposePhase9).
        AttachOverlayLayout();
        _layoutOverlay.SetWindows(_windowService!);   // edit-mode toolbar registers as a uGUI window

        // NativeUiService.Tick is deliberately NOT subscribed here: _framework.Update is IsWorldActive-gated (frozen
        // through zone loads). It's ticked UN-gated from RunGlobalRateWork instead — see Wiring.ServiceTick.
        log.Info("[Launcher] uGUI launcher + rail button + uGUI Settings hub (7 tabs) registered");
    }

    // The 7 Settings drawers, wired to their services (extracted from WirePhase9Ui for the STELLAR0002 gate).
    // WirePhase9Ui null-checks every field before calling this, hence the `!`s.
    private SettingsHubPanels BuildSettingsPanels(BepInExPluginLog log)
    {
        var loc = _frameworkLocalization!;
        var registry = _pluginRegistry!;
        var theme = _themeRenderer!;
        var hotkeys = _hotkeyService!;
        // Display-only plugin names (and launcher icons) = each plugin's own (localized) launcher tile, else its
        // internal name.
        var names = new PluginDisplayNames(_launcher!, log);
        if (_localizationEngine != null) _localizationEngine.LanguageChanged += names.ForgetRemembered;
        // The layout editor's per-plugin filter captions each plugin by this same (localized) name.
        _windowService?.SetDisplayNames(names);
        var gameUi = new GameUiPanel(_nativeUi!, theme, log, _layoutEditor!, loc);
        return new SettingsHubPanels
        {
            Plugins = new PluginsPanel(registry, theme, registry.SetEnabled, loc) { DisplayNames = names },
            Hotkeys = new HotkeysPanel(hotkeys, hotkeys, registry, theme, PluginName, loc) { DisplayNames = names },   // registry = IPluginInventory (group-header fallback names); PluginName labels the framework's own group
            Themes  = new ThemesPanel(_namedTheme!, theme, _colorRegistry!, _customThemes!, _localizationEngine!, loc),
            Layout  = new LayoutPanel(_layoutStorage!, _layoutEditor!, theme, loc),
            GameUi  = gameUi,
            Perf    = new PerformancePanel(_perfPrefs!, theme, registry, _scheduler!.EffectiveRateFor, loc) { DisplayNames = names },
            // About → Diagnostics "Save HUD paths" runs the Game UI panel's recon walk (moved there from Game UI).
            About   = new AboutPanel(theme, loc, () => registry.List().Count, gameUi.ReconWalk),
        };
    }

    private void AttachOverlayLayout()
    {
        System.Func<Resolution> res = () => _inputGateway?.CurrentResolution ?? new Resolution(1920, 1080);
        // WindowService subscribes LayoutStorage.SlotsChanged itself inside AttachLayout (it holds the storage),
        // so mod windows reapply on a slot switch from either entry point.
        _windowService?.AttachLayout(_layoutStorage!, res);
        // The native game HUD has no resolution provider of its own, so subscribe it HERE (composition root) with
        // a closure that supplies the current resolution — keeps NativeUiService free of a resolution dependency.
        // applyVisibility:true: a slot switch is an explicit user action (matches the old toolbar
        // ReapplyForActiveSlot call that omitted the flag → defaulted true). ReapplyForActiveSlot only reads the
        // active slot + config and pushes to the adapter (through the SetRect idempotent guard), so it never
        // re-fires SlotsChanged — no feedback loop. The -= before += keeps re-wiring idempotent.
        if (_layoutStorage is not null)
        {
            _layoutStorage.SlotsChanged -= ReapplyNativeUiForSlotChange;
            _layoutStorage.SlotsChanged += ReapplyNativeUiForSlotChange;
        }
    }

    private void ReapplyNativeUiForSlotChange()
        => _nativeUi?.ReapplyForActiveSlot(_inputGateway?.CurrentResolution ?? new Resolution(1920, 1080),
                                           applyVisibility: true);

    // The house-style Settings hub (SettingsHub: side rail + ToolHeader + tab body + footer feedback, fixed 800 × 612),
    // its shared "?" help popover and the "Edit colours" child window, wired to the same services as before. Opened by
    // the launcher's ⚙ Settings entry (the control is held in _settingsHubControl) or OpenSettingsAt. Hidden at boot.
    private void RegisterSettingsHub(SettingsHubPanels panels)
    {
        if (_windowService == null) return;
        // Test hook (visual scenarios, mirrors STELLAR_AUTO_OPEN): STELLAR_SETTINGS_TAB=<0..6> preselects
        // the hub tab at registration so a scenario can capture a specific panel (e.g. 2 = Themes).
        var tab = 0;
        if (int.TryParse(System.Environment.GetEnvironmentVariable("STELLAR_SETTINGS_TAB"), out var tabEnv)
            && tabEnv is >= 0 and <= 6)
            tab = tabEnv;
        // Framework chrome — usable at title/menus in every phase, but hide over the loading screen. The title is
        // resolved by the chrome when the hub is first shown (SettingsHubSpec), not here: this is pre-Game.Init.
        System.Func<bool> gate = () => (_clientState!.UiState & GameUIState.Loading) == 0;
        // A pre-warm mount is "shown" to the window service (mounted + Visible) but never on screen — mask it.
        bool HubShown() => !_hubPrewarm.Warming && _settingsHubControl?.IsShown == true;
        var loc = _frameworkLocalization!;
        // The kit reads the live theme (colours + window opacity) and NO localization at construction.
        var style = new StellarStyle(_themeRenderer!.Colors, (IChromeStyle)_namedTheme!);
        var help = new StellarHelpPopover(style, loc, _framework!, SettingsHub.HelpWindowId, () => gate() && HubShown());
        SettingsHub? hub = null;
        var kit = new SettingsKit(style, help, new StellarFeedbackLine(style, () => hub?.FooterHint() ?? ""),
            new StellarRowFlash(), loc);
        hub = _settingsHub = new SettingsHub(kit, panels, tab) { HubShown = HubShown };
        // Hotkeys capture has no Event.current outside OnGUI — poll it per frame from the game loop.
        _hotkeysCapturePoll = panels.Hotkeys.PollCaptureUgui;
        // Colour editor drag-release flush + the hub tick (help popover re-anchor, child window centring).
        // Also pumps the one-shot launcher + hub pre-warms (Wiring.Prewarm.cs) — this poll runs right after WindowService.Tick.
        _themeEditorPoll = () => { PumpWindowPrewarms(); panels.Themes.PollEditorUgui(); hub.Tick(); };
        // The pre-warm AND-s its own term onto the loading gate (never replaces it): suppressed while warming.
        var spec = SettingsHubSpec.Create(loc, () => !_hubPrewarm.Warming && gate());
        _settingsHubControl = _windowService.Register(new WindowRegistration(spec, hub.BuildRoot(),
            OnClose: () => _settingsHubControl?.SetVisible(false)));
        help.Register(_windowService);
        panels.Themes.RegisterColoursWindow(_windowService, kit, () => gate() && HubShown(), () => _settingsHubControl);
    }

    /// <summary>Opens the Settings hub on <paramref name="tabKey"/> ("plugins", "layout", "themes", "hotkeys",
    /// "gameui", "performance", "about"; an unknown key keeps the current tab) and brings it to the front — for the
    /// launcher's per-tab shortcuts. Fronts every time: the hub may already be visible (or mid pre-warm), where only an
    /// explicit front puts it on top (CLAUDE.md Rule 5 exception).</summary>
    internal void OpenSettingsAt(string tabKey)
    {
        _settingsHub?.SelectTab(tabKey);
        ShowSettingsHub();
    }

    /// <summary>The launcher's Settings callback: a tab key opens there (the red "N failed to load" status passes
    /// "plugins"); null (Settings pill / compact Settings tile) opens on the current tab. Never toggles closed — an
    /// already-open hub is just fronted (the launcher sits over it otherwise).</summary>
    private void OpenSettingsFromLauncher(string? tabKey)
    {
        if (tabKey != null) OpenSettingsAt(tabKey);
        else ShowSettingsHub();
    }

    // Every hub open goes through here so every open fronts it (already-visible / mid-pre-warm opens; Rule 5 exception).
    private void ShowSettingsHub()
    {
        if (_settingsHubControl == null) return;
        _hubPrewarm.EndForRealOpen(_settingsHubControl);   // a show mid-warm-up takes over (flag cleared BEFORE SetVisible)
        _settingsHubControl.SetVisible(true);
        _settingsHubControl.BringToFront();
    }

    private void DeclareSettingsHotkey(BepInExPluginLog log)
    {
        var action = new HotkeyAction(
            Id: FrameworkHotkeyIds.SettingsToggle,
            // English on purpose — this runs in OnHotUpdateReady, before Game.Init: a T() here was an early
            // client-language read. HotkeysPanel relabels it render-driven (FrameworkHotkeyLabels).
            Description: "Toggle Stellar Settings",
            SuggestedDefault: new KeyBinding(StellarKeyCode.Home, ModifierKeys.Shift));
        _hotkeyService!.DeclareAction(action, () => ToggleLauncher());
        log.Info("[Settings] hotkey framework.settings-toggle declared (Shift+Home)");
    }
}
