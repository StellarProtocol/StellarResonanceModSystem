using Stellar.Abstractions.Domain;
using Stellar.Application.Services;
using Stellar.Infrastructure.BepInExAdapters;
using Stellar.Infrastructure.Game;
using Stellar.Infrastructure.UI;

namespace Stellar.Host;

public sealed partial class BootstrapPlugin
{
    private UnityInputGateway? _inputGateway;
    private HotkeyService? _hotkeyService;
    private HotkeyKeyBlockPatch? _keyBlockPatch;
    private LayoutStorage? _layoutStorage;
    private LayoutEditorService? _layoutEditor;
    private LayoutEditorOverlay? _layoutOverlay;
    // Login-view probe: detects the game's login_main view active to latch Startup→TitleScreen. Ticked from
    // the Host's UN-gated per-tick path (RunGlobalRateWork), NOT _framework.Tick — it must run in Startup
    // where IsWorldActive is false. A pure UI active-state read, safe every phase (like the draw services).
    private Stellar.Infrastructure.Game.PandaLoginViewProbe? _loginViewProbe;
    // Loading-screen probe: sole owner of GameUIState.Loading. Also ticked UN-gated (RunGlobalRateWork) because
    // the loading screen is up exactly while IsWorldActive is false, when the gated menu-state probe is frozen.
    private Stellar.Infrastructure.Game.PandaLoadingScreenProbe? _loadingScreenProbe;

    private void BuildInputAndLayoutServices(BepInExPluginLog log)
    {
        _inputGateway  = new UnityInputGateway();
        // _inputGateway.DiagnosticLog = log.Info;  // enable to log every captured keypress + modifier flags (off in production)

        _keyBlockPatch = new HotkeyKeyBlockPatch();
        _keyBlockPatch.Install(PluginGuid, log.Info);

        // HotkeyService now persists user-bound keys via the framework's
        // "hotkeys" config section; missing keys fall back to SuggestedDefault.
        var hotkeySection = _pluginConfigService!.GetSection("hotkeys");
        _hotkeyService = new HotkeyService(_inputGateway, log, hotkeySection, _keyBlockPatch.Update, _keyBlockPatch.SetCaptureMode);
        _layoutStorage = new LayoutStorage(_pluginConfigService!, log);
        _layoutEditor  = new LayoutEditorService(_layoutStorage, log);

        _menuState = new Stellar.Infrastructure.Game.PandaMenuStateProbe();
        _loginViewProbe = new Stellar.Infrastructure.Game.PandaLoginViewProbe();
        _loadingScreenProbe = new Stellar.Infrastructure.Game.PandaLoadingScreenProbe();
        // Perf harness: route PerfProbe's periodic summary lines to the framework
        // log so the numbers are readable headlessly (scenario runs / log tail),
        // not only on the on-screen overlay. No-op unless STELLAR_PERFHUD=1.
        Stellar.Abstractions.Diagnostics.PerfProbe.LogSink = log.Info;
        _layoutOverlay = new LayoutEditorOverlay(_layoutEditor, _inputGateway, _layoutStorage, _themeRenderer!, log, _clientState!);

        // Framework-level edit-mode hotkey (Alt+E toggles layout edit mode).
        _hotkeyService.DeclareAction(
            new HotkeyAction(
                Id: "framework.layout-edit",
                Description: "Toggle layout edit mode",
                SuggestedDefault: new KeyBinding(StellarKeyCode.E, ModifierKeys.Alt)),
            callback: () => _layoutEditor.ToggleEditMode());

        // HUD-visibility hotkeys (Alt+H toggle + unbound hold-to-hide). Extracted to keep
        // BuildInputAndLayoutServices under the STELLAR0002 50-LoC gate.
        DeclareHudVisibilityHotkeys();
    }

    private void DeclareHudVisibilityHotkeys()
    {
        // Framework-level HUD-toggle hotkey (Alt+H hides/shows all HUD-category overlays).
        // Sets PerfControls.MasterHudKill, which WindowRenderer.ApplyValues reads to hide only
        // HUD-category windows (Tools/Debug/Settings untouched). Runtime static → resets to
        // false on process restart, so it is intentionally NOT persisted.
        _hotkeyService!.DeclareAction(
            new HotkeyAction(
                Id: "framework.hud-toggle",
                Description: "Toggle all HUD overlays",
                SuggestedDefault: new KeyBinding(StellarKeyCode.H, ModifierKeys.Alt)),
            callback: () => Stellar.Abstractions.Diagnostics.PerfControls.MasterHudKill
                            = !Stellar.Abstractions.Diagnostics.PerfControls.MasterHudKill);

        // Framework-level HOLD-to-hide-HUD hotkey (UNBOUND by default — user binds it in Settings → Hotkeys).
        // Hides all HUD-category overlays WHILE HELD and restores on release. Effect is polled each tick via
        // HotkeyService.IsActionHeld in TickInputAndHotkeys (edge-detected there), so the callback is a no-op.
        // Composes with the Alt+H toggle and the perf-overlay "Master HUD kill" checkbox: on release MasterHudKill
        // reverts to whatever it was before the hold began, not unconditionally false.
        _hotkeyService!.DeclareAction(
            new HotkeyAction(
                Id: "framework.hud-hold",
                Description: "Hold to hide all HUD overlays",
                SuggestedDefault: null),          // unbound by default
            callback: () => { });                  // no press action; effect is polled via IsActionHeld each tick
    }
}
